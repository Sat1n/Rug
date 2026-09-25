#nullable enable
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NLua;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Schema;

namespace Rug.UI.Core.Services;

/// <summary>Debounces file-system bursts per plugin, then reloads tasks and UI in order.</summary>
public sealed class HotReloadService : IHotReloadService
{
    private readonly string _root;
    private readonly ITaskScheduler _scheduler;
    private readonly ILogger<HotReloadService> _logger;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _knownIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<nint>> _pendingRestarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<long, Func<string, CancellationToken, Task>>> _uiHandlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private FileSystemWatcher? _watcher;
    private Task? _worker;
    private long _nextHandler;
    private bool _disposed;

    public HotReloadService(string pluginsRootPath, ITaskScheduler scheduler,
        ILogger<HotReloadService> logger, TimeSpan? debounce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRootPath);
        _root = Path.GetFullPath(pluginsRootPath);
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _debounce = debounce ?? TimeSpan.FromMilliseconds(400);
        if (_debounce < TimeSpan.FromMilliseconds(50) || _debounce > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(debounce));
    }

    public event EventHandler<HotReloadEventArgs>? Reloaded;
    public event EventHandler<HotReloadFailedEventArgs>? ReloadFailed;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher is not null) return;
            Directory.CreateDirectory(_root);
            foreach (string directory in Directory.EnumerateDirectories(_root))
                _knownIds[directory] = ReadPluginId(directory) ?? Path.GetFileName(directory);
            _worker = Task.Run(WatchLoopAsync);
            _watcher = new FileSystemWatcher(_root)
            {
                Filter = "*", IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;
        }
    }

    public IDisposable RegisterUiHandler(string pluginId,
        Func<string, CancellationToken, Task> reloadUiAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(reloadUiAsync);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long id = ++_nextHandler;
            if (!_uiHandlers.TryGetValue(pluginId, out var handlers))
                _uiHandlers[pluginId] = handlers = new();
            handlers.Add(id, reloadUiAsync);
            return new Registration(this, pluginId, id);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => QueueFile(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        QueueFile(e.OldFullPath);
        QueueFile(e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "Plugin watcher lost events; scheduling a full rescan");
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(_root)) QueueDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not rescan plugin directories after watcher overflow");
        }
    }

    private void QueueFile(string path)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".lua", StringComparison.OrdinalIgnoreCase)) return;
        string relative = Path.GetRelativePath(_root, path);
        string[] parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is "." or "..") return;
        QueueDirectory(Path.Combine(_root, parts[0]));
    }

    private void QueueDirectory(string directory)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending[directory] = Stopwatch.GetTimestamp();
        }
    }

    private async Task WatchLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                string[] ready;
                lock (_gate)
                {
                    long now = Stopwatch.GetTimestamp();
                    ready = _pending.Where(item => Stopwatch.GetElapsedTime(item.Value, now) >= _debounce)
                        .Select(item => item.Key).ToArray();
                    foreach (string directory in ready) _pending.Remove(directory);
                }
                foreach (string directory in ready)
                {
                    _shutdown.Token.ThrowIfCancellationRequested();
                    try { await ReloadDirectoryAsync(directory, _shutdown.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected watcher error for {Directory}", directory);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ReloadDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        string pluginId;
        Func<string, CancellationToken, Task>[] handlers;
        lock (_gate)
        {
            pluginId = ReadPluginId(directory) ??
                (_knownIds.TryGetValue(directory, out string? previous) ? previous : Path.GetFileName(directory));
            _knownIds[directory] = pluginId;
            handlers = _uiHandlers.TryGetValue(pluginId, out var registered)
                ? registered.Values.ToArray() : [];
        }

        try
        {
            nint[] targets;
            await using (await _scheduler.AcquirePluginReloadAsync(pluginId, cancellationToken)
                .ConfigureAwait(false))
            {
                var active = _scheduler.Instances
                    .Where(context => context.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase))
                    .Select(context => (context.InstanceId, context.TargetHwnd)).ToArray();
                lock (_gate)
                {
                    if (!_pendingRestarts.TryGetValue(pluginId, out var pending))
                        _pendingRestarts[pluginId] = pending = [];
                    foreach (var item in active) pending.Add(item.TargetHwnd);
                    targets = pending.ToArray();
                }
                await Task.WhenAll(active.Select(item => _scheduler.StopTaskAsync(item.InstanceId)))
                    .ConfigureAwait(false);

                string schemaPath = Path.Combine(directory, "ui.json");
                if (File.Exists(schemaPath))
                    await new SchemaParser().ParseFileAsync(schemaPath, cancellationToken).ConfigureAwait(false);
                await ValidateLuaScriptsAsync(directory, cancellationToken).ConfigureAwait(false);
                foreach (var handler in handlers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await handler(schemaPath, cancellationToken).ConfigureAwait(false);
                }

                await _scheduler.RefreshPluginsAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (nint hwnd in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _scheduler.StartTaskAsync(pluginId, hwnd, cancellationToken)
                    .ConfigureAwait(false);
            }
            lock (_gate) _pendingRestarts.Remove(pluginId);
            var result = new HotReloadEventArgs(pluginId, directory, DateTimeOffset.Now, targets.Length);
            _logger.LogInformation("Hot reloaded plugin {PluginId} ({Instances} instances)", pluginId, targets.Length);
            try { Reloaded?.Invoke(this, result); }
            catch (Exception ex) { _logger.LogWarning(ex, "Reloaded notification failed for {PluginId}", pluginId); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hot reload failed for plugin {PluginId}", pluginId);
            try { ReloadFailed?.Invoke(this, new HotReloadFailedEventArgs(pluginId, directory, ex)); }
            catch (Exception notificationError)
            {
                _logger.LogWarning(notificationError, "Reload failure notification failed for {PluginId}", pluginId);
            }
        }
    }

    private static async Task ValidateLuaScriptsAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string path in Directory.EnumerateFiles(directory, "*.lua", SearchOption.AllDirectories))
        {
            string source = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            await Task.Run(() =>
            {
                using var lua = new Lua();
                lua.State.Encoding = Encoding.UTF8;
                lua["__hot_reload_source"] = source;
                lua.DoString("return assert(load(__hot_reload_source, '@hotreload', 't'))");
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? ReadPluginId(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    if (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(property.Value.GetString()))
                        return property.Value.GetString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
            _shutdown.Cancel();
            worker = _worker;
            _pending.Clear();
            _pendingRestarts.Clear();
            _uiHandlers.Clear();
        }
        if (worker is not null) await worker.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private sealed class Registration(HotReloadService owner, string pluginId, long id) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate)
            {
                if (owner._uiHandlers.TryGetValue(pluginId, out var handlers))
                {
                    handlers.Remove(id);
                    if (handlers.Count == 0) owner._uiHandlers.Remove(pluginId);
                }
            }
        }
    }
}
