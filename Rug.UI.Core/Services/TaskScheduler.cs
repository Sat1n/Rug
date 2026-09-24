#nullable enable
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Owns one capture/input/Lua session per plugin and window instance.</summary>
public sealed class TaskScheduler : ITaskScheduler
{
    private readonly IPluginManager _plugins;
    private readonly string _pluginsRoot;
    private readonly Func<ICaptureService> _captureFactory;
    private readonly Func<IInputService> _inputFactory;
    private readonly Func<ICaptureService, IInputService, IScriptRuntime> _runtimeFactory;
    private readonly ILogger _logger;
    private readonly TimeSpan _tickInterval;
    private readonly TimeSpan _watchdogTimeout;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskExecutionContext> _instances = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycleGate = new();
    private TaskCompletionSource<bool> _startsDrained = CompletedSignal();
    private int _starting;
    private Dictionary<string, PluginManifest>? _catalog;
    private bool _disposed;

    public TaskScheduler(IPluginManager plugins, string pluginsRoot,
        Func<ICaptureService> captureFactory, Func<IInputService> inputFactory,
        Func<ICaptureService, IInputService, IScriptRuntime> runtimeFactory,
        ILogger<TaskScheduler> logger, TimeSpan? tickInterval = null,
        TimeSpan? watchdogTimeout = null)
    {
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);
        _pluginsRoot = pluginsRoot;
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _inputFactory = inputFactory ?? throw new ArgumentNullException(nameof(inputFactory));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tickInterval = tickInterval ?? TimeSpan.FromMilliseconds(50);
        _watchdogTimeout = watchdogTimeout ?? TimeSpan.FromSeconds(1);
        if (_tickInterval <= TimeSpan.Zero || _watchdogTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tickInterval));
    }

    public IReadOnlyCollection<TaskExecutionContext> Instances => _instances.Values.ToArray();

    public async Task<Guid> StartTaskAsync(string pluginId, nint hwnd, CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TaskScheduler));
            if (++_starting == 1)
                _startsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { return await StartCoreAsync(pluginId, hwnd, cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_lifecycleGate)
            {
                if (--_starting == 0) _startsDrained.TrySetResult(true);
            }
        }
    }

    private async Task<Guid> StartCoreAsync(string pluginId, nint hwnd, CancellationToken cancellationToken)
    {
        if (hwnd == 0) throw new ArgumentException("Target window handle is required.", nameof(hwnd));
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        ICaptureService? capture = null;
        IInputService? input = null;
        IScriptRuntime? runtime = null;
        try
        {
            PluginManifest source = await GetPluginAsync(pluginId, cancellation.Token).ConfigureAwait(false);
            PluginManifest manifest = source with { TargetWindow = hwnd };
            capture = _captureFactory() ?? throw new InvalidOperationException("Capture factory returned null.");
            input = _inputFactory() ?? throw new InvalidOperationException("Input factory returned null.");
            runtime = _runtimeFactory(capture, input) ?? throw new InvalidOperationException("Runtime factory returned null.");
            // Capture is ready before Lua on_init can call rug.capture().
            await capture.StartAsync(hwnd, cancellation.Token).ConfigureAwait(false);
            await runtime.InitializeAsync(Path.Combine(source.PluginDirectory, source.Entry),
                manifest, cancellation.Token).ConfigureAwait(false);
            await runtime.PrepareLifecycleAsync().ConfigureAwait(false);
            Guid id = Guid.NewGuid();
            var context = new TaskExecutionContext(id, source.Id, hwnd, cancellation, runtime, capture, input);
            lock (_lifecycleGate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(TaskScheduler));
                if (!_instances.TryAdd(id, context)) throw new InvalidOperationException("Duplicate instance id.");
                context.Runner = Task.Run(() => RunAsync(context));
            }
            return id;
        }
        catch
        {
            cancellation.Cancel();
            if (runtime is not null)
            {
                runtime.Stop();
                await TryCleanupAsync(() => runtime.DisposeAsync().AsTask(), "runtime startup disposal").ConfigureAwait(false);
            }
            if (capture is not null)
            {
                await TryCleanupAsync(capture.StopAsync, "capture startup stop").ConfigureAwait(false);
                await TryCleanupAsync(() => capture.DisposeAsync().AsTask(), "capture startup disposal").ConfigureAwait(false);
            }
            if (input is not null)
                await TryCleanupAsync(() => input.DisposeAsync().AsTask(), "input startup disposal").ConfigureAwait(false);
            cancellation.Dispose();
            throw;
        }
    }

    public bool PauseTask(Guid instanceId) =>
        _instances.TryGetValue(instanceId, out TaskExecutionContext? context) && context.Pause();

    public bool ResumeTask(Guid instanceId) =>
        _instances.TryGetValue(instanceId, out TaskExecutionContext? context) && context.Resume();

    public async Task StopTaskAsync(Guid instanceId)
    {
        if (!_instances.TryGetValue(instanceId, out TaskExecutionContext? context)) return;
        context.Cancel();
        if (context.Runner is Task runner)
            await runner.WaitAsync(_watchdogTimeout + TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private async Task<PluginManifest> GetPluginAsync(string pluginId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _catalogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _catalog ??= await Task.Run(() => _plugins.ScanPlugins(_pluginsRoot)
                .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);
            return _catalog.TryGetValue(pluginId, out PluginManifest? manifest)
                ? manifest : throw new KeyNotFoundException($"Plugin '{pluginId}' was not found.");
        }
        finally { _catalogGate.Release(); }
    }

    private async Task RunAsync(TaskExecutionContext context)
    {
        CancellationToken token = context.Cancellation.Token;
        try
        {
            await DriveHookAsync(context, token).ConfigureAwait(false); // on_init
            if (context.TaskStatus != AutomationTaskStatus.Paused)
                context.SetStatus(AutomationTaskStatus.Running);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await context.WaitIfPausedAsync(token).ConfigureAwait(false);
                await context.Runtime.BeginTickAsync().ConfigureAwait(false);
                await DriveHookAsync(context, token).ConfigureAwait(false);
                await Task.Delay(_tickInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            context.LastError = ex;
            context.SetStatus(AutomationTaskStatus.Faulted);
            _logger.LogError(ex, "Automation instance {InstanceId} faulted", context.InstanceId);
        }
        finally
        {
            context.Cancellation.Cancel();
            context.Runtime.Stop();
            await CleanupAsync(context).ConfigureAwait(false);
        }
    }

    private static async Task DriveHookAsync(TaskExecutionContext context, CancellationToken token)
    {
        bool resume = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await context.WaitIfPausedAsync(token).ConfigureAwait(false);
            ScriptExecutionResult result = resume
                ? await context.Runtime.ResumeAsync().ConfigureAwait(false)
                : await context.Runtime.StepAsync().ConfigureAwait(false);
            switch (result.State)
            {
                case ScriptState.Ready: return;
                case ScriptState.Yielded: resume = true; break;
                case ScriptState.Paused: break;
                case ScriptState.Stopped when token.IsCancellationRequested:
                    throw new OperationCanceledException(token);
                case ScriptState.Faulted:
                    throw result.Error ?? new InvalidOperationException("Lua hook faulted.");
                default:
                    throw new InvalidOperationException($"Unexpected Lua hook state: {result.State}");
            }
        }
    }

    private async Task CleanupAsync(TaskExecutionContext context)
    {
        try
        {
            try { await context.Runtime.InvokeStopHookAsync(_watchdogTimeout).ConfigureAwait(false); }
            catch (Exception ex) { RecordCleanupError(context, ex, "on_stop"); }
            try { await context.Capture.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { RecordCleanupError(context, ex, "capture stop"); }
            try { await context.Runtime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { RecordCleanupError(context, ex, "runtime disposal"); }
            try { await context.Input.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { RecordCleanupError(context, ex, "input disposal"); }
            try { await context.Capture.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { RecordCleanupError(context, ex, "capture disposal"); }
        }
        finally
        {
            context.SetStatus(context.LastError is null ? AutomationTaskStatus.Stopped : AutomationTaskStatus.Faulted);
            _instances.TryRemove(context.InstanceId, out _);
            context.Cancellation.Dispose();
        }
    }

    private void RecordCleanupError(TaskExecutionContext context, Exception ex, string stage)
    {
        context.LastError ??= ex;
        _logger.LogError(ex, "Automation instance {InstanceId} failed during {Stage}", context.InstanceId, stage);
    }

    private async Task TryCleanupAsync(Func<Task> action, string stage)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Failed during {Stage}", stage); }
    }

    public async ValueTask DisposeAsync()
    {
        Task drained;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            drained = _startsDrained.Task;
        }
        _shutdown.Cancel();
        await drained.ConfigureAwait(false);
        await Task.WhenAll(_instances.Keys.Select(StopTaskAsync)).ConfigureAwait(false);
        _catalogGate.Dispose();
        _shutdown.Dispose();
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(true);
        return signal;
    }
}
