#nullable enable
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Bounded latest-save-wins Prepare/Validate pipeline. Never stops/restarts a run.</summary>
public sealed class HotReloadService : IHotReloadService
{
    public const int MaxPending = 64, MaxHandlers = 128;
    private readonly string _root;
    private readonly ITaskScheduler _scheduler;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly PluginUpdatePipeline _pipeline;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Ticks, long Revision)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _knownIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _entryFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<long, Func<PluginSnapshot, CancellationToken, Task<IPluginUiUpdate>>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FileSystemWatcher? _watcher;
    private Task? _worker;
    private long _revision, _nextHandler;
    private int _handlerCount;
    private bool _disposed;
    internal bool WatchFiles { get; set; } = true;
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }
    internal int HandlerCount { get { lock (_gate) return _handlerCount; } }
    public HotReloadService(string pluginsRootPath, ITaskScheduler scheduler, ILogger<HotReloadService> logger,
        TimeSpan? debounce = null, PluginUpdatePipeline? pipeline = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRootPath);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsRootPath));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler)); _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _debounce = debounce ?? TimeSpan.FromMilliseconds(400); _pipeline = pipeline ?? new();
        if (_debounce < TimeSpan.FromMilliseconds(50) || _debounce > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(debounce));
    }
    public Task Ready => _ready.Task;
    public event EventHandler<HotReloadEventArgs>? Reloaded;
    public event EventHandler<HotReloadFailedEventArgs>? ReloadFailed;
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); if (_worker is not null) return;
            Directory.CreateDirectory(_root);
            if (WatchFiles)
            {
                _watcher = new(_root) { Filter = "*", IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime, InternalBufferSize = 64 * 1024 };
                _watcher.Changed += (_, e) => QueueFile(e.FullPath); _watcher.Created += (_, e) => QueueFile(e.FullPath);
                _watcher.Deleted += (_, e) => QueueFile(e.FullPath);
                _watcher.Renamed += (_, e) => { QueueFile(e.OldFullPath); QueueFile(e.FullPath); };
                _watcher.Error += (_, e) =>
                {
                    _logger.LogWarning(e.GetException(), "Watcher overflow; bounded rescan");
                    try { foreach (var dir in Directory.EnumerateDirectories(_root).Take(MaxPending)) RequestReload(dir); }
                    catch (Exception error) { Failure("watcher", _root, error); }
                };
                _watcher.EnableRaisingEvents = true;
            }
            _worker = Task.Run(WatchLoopAsync);
        }
    }
    public IDisposable RegisterUiHandler(string pluginId, Func<PluginSnapshot, CancellationToken, Task<IPluginUiUpdate>> prepareUiAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId); ArgumentNullException.ThrowIfNull(prepareUiAsync);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handlerCount >= MaxHandlers) throw new InvalidOperationException("UI update handler capacity exceeded.");
            if (!_handlers.TryGetValue(pluginId, out var handlers)) _handlers[pluginId] = handlers = new();
            var switching = _scheduler.RuntimeUpdates?.RegisterUiHandler(pluginId, prepareUiAsync);
            long id = ++_nextHandler; handlers.Add(id, prepareUiAsync); _handlerCount++; return new Registration(this, pluginId, id, switching);
        }
    }
    private void QueueFile(string path)
    {
        var parts = Path.GetRelativePath(_root, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is "." or "..") return;
        string directory = Path.Combine(_root, parts[0]);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".json" or ".lua"))
            lock (_gate) if (!_entryFiles.TryGetValue(directory, out var entry) || !entry.Equals(path, StringComparison.OrdinalIgnoreCase)) return;
        RequestReload(directory);
    }
    /// <summary>Trusted host retry after a pending update; never accepts a Lua-supplied path.</summary>
    public void RequestReload(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(Path.GetDirectoryName(directory), _root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Update must be an immediate plugin installation.");
        bool overflow = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_pending.ContainsKey(directory) && _pending.Count >= MaxPending || !_latest.ContainsKey(directory) && _latest.Count >= MaxPending * 2) overflow = true;
            else { long revision = ++_revision; _latest[directory] = revision; _pending[directory] = (Stopwatch.GetTimestamp(), revision); }
        }
        if (overflow) Failure(Path.GetFileName(directory), directory, new InvalidOperationException("Update queue capacity exceeded."));
    }
    private async Task WatchLoopAsync()
    {
        try
        {
            var snapshots = await _scheduler.GetPluginSnapshotsAsync(_shutdown.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                foreach (var snapshot in snapshots) { _knownIds[snapshot.Directory] = snapshot.PluginId; _entryFiles[snapshot.Directory] = Path.GetFullPath(Path.Combine(snapshot.Directory, snapshot.EntryCode.Entry)); }
            }
            _ready.TrySetResult();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                KeyValuePair<string, (long Ticks, long Revision)>? next;
                lock (_gate)
                {
                    next = _pending.Where(p => Stopwatch.GetElapsedTime(p.Value.Ticks) >= _debounce).OrderBy(p => p.Value.Revision)
                        .Select(p => (KeyValuePair<string, (long Ticks, long Revision)>?)p).FirstOrDefault();
                    if (next is { } item) _pending.Remove(item.Key);
                }
                if (next is { } ready) await UpdateAsync(ready.Key, ready.Value.Revision, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { _ready.TrySetCanceled(); }
        catch (Exception error) { _ready.TrySetException(error); Failure("watcher", _root, error); }
    }
    private bool Current(string directory, long revision) { lock (_gate) return !_disposed && _latest.GetValueOrDefault(directory) == revision; }
    /// <summary>@shape candidate: one bounded revision; prepare all UI trees before replacing any.
    /// @source snapshot: Services/PluginUpdatePipeline.cs#class:PluginUpdatePipeline</summary>
    private async Task UpdateAsync(string directory, long revision, CancellationToken token)
    {
        string id; lock (_gate) id = _knownIds.GetValueOrDefault(directory) ?? Path.GetFileName(directory);
        var stages = new List<IPluginUiUpdate>(); bool published = false;
        try
        {
            var snapshot = await _pipeline.ValidateAsync(await _pipeline.PrepareAsync(directory, revision, token).ConfigureAwait(false), token).ConfigureAwait(false);
            lock (_gate) if (_knownIds.TryGetValue(directory, out var known) && known != snapshot.PluginId) throw new InvalidDataException("Plugin ID changed at the same installation.");
            id = snapshot.PluginId;
            await using var lease = await _scheduler.AcquirePluginReloadAsync(id, token).ConfigureAwait(false);
            if (!Current(directory, revision)) { Notify(snapshot, PluginUpdateStatus.Superseded); return; }
            var status = await _scheduler.ClassifyPluginUpdateAsync(snapshot, token).ConfigureAwait(false);
            if (status is PluginUpdateStatus.Unchanged or PluginUpdateStatus.RuntimePending)
            {
                lock (_gate) if (_knownIds.ContainsKey(directory)) _entryFiles[directory] = Path.GetFullPath(Path.Combine(directory, snapshot.EntryCode.Entry));
                Notify(snapshot, status); return;
            }
            Func<PluginSnapshot, CancellationToken, Task<IPluginUiUpdate>>[] handlers;
            lock (_gate) handlers = _handlers.TryGetValue(id, out var registered) ? registered.Values.ToArray() : [];
            foreach (var handler in handlers) stages.Add(await handler(snapshot, token).ConfigureAwait(false));
            // Commit decision: earlier observed saves win. Later saves wait for this serial publication.
            // No external callback or UI wait executes under the revision lock.
            if (!Current(directory, revision)) { Notify(snapshot, PluginUpdateStatus.Superseded); return; }
            token.ThrowIfCancellationRequested();
            foreach (var stage in stages) await stage.CommitAsync(token).ConfigureAwait(false);
            await _scheduler.PublishPluginSnapshotAsync(snapshot, token).ConfigureAwait(false);
            published = true;
            lock (_gate) { _knownIds[directory] = id; _entryFiles[directory] = Path.GetFullPath(Path.Combine(directory, snapshot.EntryCode.Entry)); }
            foreach (var stage in stages) await stage.DisposeAsync().ConfigureAwait(false);
            stages.Clear();
            Notify(snapshot, status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (Current(directory, revision)) Failure(id, directory, error); }
        finally
        {
            foreach (var stage in stages.AsEnumerable().Reverse())
            {
                try { if (!published) await stage.RollbackAsync().ConfigureAwait(false); await stage.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { Failure(id, directory, error); }
            }
            lock (_gate) if (!_pending.ContainsKey(directory)) _latest.Remove(directory);
        }
    }
    private void Notify(PluginSnapshot snapshot, PluginUpdateStatus status)
    {
        try { Reloaded?.Invoke(this, new(snapshot.PluginId, snapshot.Directory, DateTimeOffset.Now, status: status, revision: snapshot.Revision, contentVersion: snapshot.ContentVersion)); }
        catch (Exception error) { _logger.LogWarning(error, "Update observer failed"); }
    }
    private void Failure(string id, string directory, Exception error)
    {
        _logger.LogError(error, "Plugin update failed for {PluginId}", id);
        try { ReloadFailed?.Invoke(this, new(id, directory, error)); } catch (Exception notification) { _logger.LogWarning(notification, "Update failure observer failed"); }
    }
    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_gate) { if (_disposed) return; _disposed = true; _watcher?.Dispose(); _watcher = null; worker = _worker; _pending.Clear(); _latest.Clear(); _knownIds.Clear(); _entryFiles.Clear(); _handlers.Clear(); _handlerCount = 0; }
        _shutdown.Cancel(); _ready.TrySetCanceled(); if (worker is not null) await worker.ConfigureAwait(false); _shutdown.Dispose();
    }
    private sealed class Registration(HotReloadService owner, string pluginId, long id, IDisposable? switching) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            switching?.Dispose();
            lock (owner._gate) if (owner._handlers.TryGetValue(pluginId, out var handlers))
            { if (handlers.Remove(id)) owner._handlerCount--; if (handlers.Count == 0) owner._handlers.Remove(pluginId); }
        }
    }
}
