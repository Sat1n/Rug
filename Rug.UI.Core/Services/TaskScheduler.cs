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
    private readonly IAnomalyLogger _anomalies;
    private readonly string _pluginsRoot;
    private readonly Func<ICaptureService> _captureFactory;
    private readonly Func<IInputService> _inputFactory;
    private readonly Func<ICaptureService, IInputService, IScriptRuntime> _runtimeFactory;
    private readonly ILogger _logger;
    private readonly ICommandBus? _commandBus;
    private readonly TimeSpan _tickInterval;
    private readonly TimeSpan _watchdogTimeout;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskExecutionContext> _instances = new();
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pluginStartGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycleGate = new();
    private TaskCompletionSource<bool> _startsDrained = CompletedSignal();
    private int _starting;
    private int _startingInput;
    private TaskCompletionSource<bool> _inputStartsDrained = CompletedSignal();
    private Dictionary<string, PluginManifest>? _catalog;
    private bool _disposed;
    private readonly IInputArbiter _inputArbiter;
    private readonly IDisposable _inputStopRegistration;
    private readonly IInputInterventionCoordinator? _intervention;
    private readonly IRecoveryCoordinator _recovery;

    public TaskScheduler(IPluginManager plugins, string pluginsRoot,
        Func<ICaptureService> captureFactory, Func<IInputService> inputFactory,
        Func<ICaptureService, IInputService, IScriptRuntime> runtimeFactory,
        ILogger<TaskScheduler> logger, IAnomalyLogger anomalies, TimeSpan? tickInterval = null,
        TimeSpan? watchdogTimeout = null, ICommandBus? commandBus = null,
        IAutomationPermissionChecker? permissionChecker = null, IPluginAuthorization? authorization = null,
        IInputArbiter? inputArbiter = null, IInputInterventionCoordinator? intervention = null,
        IRecoveryCoordinator? recovery = null)
    {
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _anomalies = anomalies ?? throw new ArgumentNullException(nameof(anomalies));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);
        _pluginsRoot = pluginsRoot;
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _inputFactory = inputFactory ?? throw new ArgumentNullException(nameof(inputFactory));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _commandBus = commandBus;
        _tickInterval = tickInterval ?? TimeSpan.FromMilliseconds(50);
        _watchdogTimeout = watchdogTimeout ?? TimeSpan.FromSeconds(1);
        _inputArbiter = inputArbiter ?? InputArbiter.Shared;
        _intervention = intervention;
        _inputStopRegistration = _inputArbiter.RegisterStopHandler(StopInputRunsAsync);
        if (_tickInterval <= TimeSpan.Zero || _watchdogTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tickInterval));
        Gateway = new AutomationGateway(logger, permissionChecker, getPlugin: GetPluginAsync,
            start: async (plugin, hwnd, inputs, ready, token) =>
            {
                RunIdentity? started = null;
                await StartTaskWithReadyAsync(plugin, hwnd, inputs,
                    identity => { started = identity; ready?.Invoke(identity); }, token).ConfigureAwait(false);
                return started!;
            }, stop: StopTaskAsync, pause: PauseTask, resume: ResumeTask, authorization: authorization, verifyRecovery: VerifyRecoveryAsync);
        _recovery = recovery ?? new RecoveryCoordinator(anomalies, Gateway.Authorization, new RecoveryValidatorRegistry());
    }

    public AutomationGateway Gateway { get; }
    public IReadOnlyCollection<TaskExecutionContext> Instances => _instances.Values.ToArray();
    public event EventHandler? InstancesChanged;
    public event EventHandler<LifecycleChangedEventArgs>? LifecycleChanged;
    public LifecycleSnapshot? GetLifecycle(Guid instanceId) =>
        _instances.TryGetValue(instanceId, out var context) ? context.Snapshot : null;

    /// <summary>Replace the cached catalog after a plugin file save, under the same gate used by starts.</summary>
    public async Task RefreshPluginsAsync(CancellationToken cancellationToken = default)
    {
        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TaskScheduler));
            _catalog = await Task.Run(() => _plugins.ScanPlugins(_pluginsRoot)
                .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase), cancellationToken)
                .ConfigureAwait(false);
            foreach (var manifest in _catalog.Values) Gateway.Authorization.Register(manifest);
        }
        finally { _catalogGate.Release(); }
    }

    public Task<Guid> StartTaskAsync(string pluginId, nint hwnd, CancellationToken cancellationToken = default) =>
        StartTaskWithReadyAsync(pluginId, hwnd, new Dictionary<string, object?>(), null, cancellationToken);

    public Task<Guid> StartTaskAsync(string pluginId, nint hwnd,
        IReadOnlyDictionary<string, object?> launchInputs, CancellationToken cancellationToken = default) =>
        StartTaskWithReadyAsync(pluginId, hwnd, launchInputs, null, cancellationToken);

    public Task<Guid> StartTaskAsync(string pluginId, nint hwnd,
        IReadOnlyDictionary<string, object?> launchInputs, Action<RunIdentity> onReady,
        CancellationToken cancellationToken = default) =>
        StartTaskWithReadyAsync(pluginId, hwnd, launchInputs, onReady, cancellationToken);

    private async Task<Guid> StartTaskWithReadyAsync(string pluginId, nint hwnd,
        IReadOnlyDictionary<string, object?> launchInputs, Action<RunIdentity>? onReady,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(launchInputs);
        lock (_lifecycleGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TaskScheduler));
            if (++_starting == 1)
                _startsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        SemaphoreSlim gate = _pluginStartGates.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try { return await StartCoreAsync(pluginId, hwnd, launchInputs, onReady, linked.Token).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        finally
        {
            lock (_lifecycleGate)
            {
                if (--_starting == 0) _startsDrained.TrySetResult(true);
            }
        }
    }

    /// <summary>Prevent starts of one plugin while its old runtime and catalog are replaced.</summary>
    public async Task<IAsyncDisposable> AcquirePluginReloadAsync(string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (_disposed) throw new ObjectDisposedException(nameof(TaskScheduler));
        SemaphoreSlim gate = _pluginStartGates.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        return new PluginReloadLease(gate);
    }

    private async Task<Guid> StartCoreAsync(string pluginId, nint hwnd,
        IReadOnlyDictionary<string, object?> launchInputs, Action<RunIdentity>? onReady,
        CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        ICaptureService? capture = null;
        IInputService? input = null;
        IScriptRuntime? runtime = null;
        RunIdentity? identity = null;
        AuthorizationLease? authorizationLease = null;
        CancellationTokenRegistration authorizationCancellation = default;
        CancellationTokenRegistration inputCancellation = default;
        CancellationToken inputEpoch = default;
        bool inputStarting = false;
        IInputSession? inputSession = null;
        TaskExecutionContext? context = null;
        InputTarget? target = null;
        try
        {
            PluginManifest source = await GetPluginAsync(pluginId, cancellation.Token).ConfigureAwait(false);
            var ticket = Gateway.Authorization.Bind(source);
            authorizationLease = Gateway.Authorization.Admit(ticket, source.Permissions);
            authorizationCancellation = authorizationLease.Token.Register(() => cancellation.Cancel());
            cancellation.Token.ThrowIfCancellationRequested();
            bool declaresInput = source.Permissions.Contains(Permission.ControlInput);
            if (declaresInput)
            {
                lock (_lifecycleGate)
                {
                    inputStarting = true;
                    if (++_startingInput == 1) _inputStartsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                inputEpoch = _inputArbiter.AutomationToken;
                inputCancellation = inputEpoch.Register(() => cancellation.Cancel());
                target = _inputArbiter.BindTarget(hwnd);
                cancellation.Token.ThrowIfCancellationRequested();
            }
            bool needsWindow = source.Permissions.Contains(Permission.Capture) ||
                source.Permissions.Contains(Permission.ControlInput);
            if (needsWindow && hwnd == 0)
                throw new ArgumentException("Target window handle is required.", nameof(hwnd));
            Guid id = Guid.NewGuid();
            long generation = _generations.AddOrUpdate(source.Id, 1, (_, previous) => checked(previous + 1));
            identity = new RunIdentity(source.Id, id, generation,
                hwnd == 0 ? null : Guid.NewGuid(), hwnd);
            if (declaresInput && _intervention is not null)
            {
                inputSession = await _intervention.BeginSessionAsync(InputOwner.ForRun(identity), cancellation.Token).ConfigureAwait(false);
                inputCancellation.Dispose();
                inputCancellation = inputSession.Token.Register(() => cancellation.Cancel());
                cancellation.Token.ThrowIfCancellationRequested();
            }
            PluginManifest manifest = source with { TargetWindow = hwnd };
            var configuration = source.Configuration is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(source.Configuration, StringComparer.Ordinal);
            if (_commandBus is not null)
                foreach (var pair in _commandBus.Snapshot(StateScope.Plugin(source.Id)))
                    configuration[pair.Key] = pair.Value;
            capture = _captureFactory() ?? throw new InvalidOperationException("Capture factory returned null.");
            input = _inputFactory() ?? throw new InvalidOperationException("Input factory returned null.");
            runtime = _runtimeFactory(capture, input) ?? throw new InvalidOperationException("Runtime factory returned null.");
            runtime.ConfigureGateway(Gateway);
            runtime.ConfigureInputArbiter(_inputArbiter);
            _commandBus?.OpenInstance(identity.InstanceScope);
            runtime.ConfigureState(identity, launchInputs, configuration);
            if (hwnd != 0) target ??= _inputArbiter.BindTarget(hwnd);
            context = new TaskExecutionContext(identity, cancellation, runtime, capture, input)
            {
                AuthorizationLease = authorizationLease, AuthorizationCancellation = authorizationCancellation,
                InputCancellation = inputCancellation, InputEpochToken = inputEpoch,
                DeclaresInput = declaresInput, InputSession = inputSession, RecoveryAuthorization = ticket
            };
            context.ValidateResume = () =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                using var admission = Gateway.Authorization.Admit(ticket, source.Permissions);
                admission.Token.ThrowIfCancellationRequested();
                if (target is not null) _inputArbiter.ValidateTarget(target, declaresInput);
            };
            context.ValidateRecovery = () =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                using var admission = Gateway.Authorization.Admit(ticket, source.Permissions);
                admission.Token.ThrowIfCancellationRequested();
                if (target is not null)
                {
                    try { _inputArbiter.ValidateTarget(target, requireControl: false); }
                    catch (InputUnavailableException) { throw new RecoveryFailedException(RecoveryError.ResourceUnavailable); }
                }
            };
            if (declaresInput)
            {
                inputCancellation.Dispose();
                context.AttachInput(inputEpoch);
                context.HandoffRecovery = async () =>
                {
                    var recoveryId = context.Snapshot.Recovery!.RecoveryId;
                    Task takeover = _inputArbiter.TakeOverForRecoveryAsync(() => context.PreserveAndRetireInput(recoveryId));
                    // Remove detector registration outside both state locks before takeover drains sessions.
                    context.InputCancellation.Dispose();
                    context.InputSession?.Dispose(); context.InputSession = null;
                    await takeover.WaitAsync(cancellation.Token).ConfigureAwait(false);
                };
                context.AcquireRecoveryInput = async () =>
                {
                    context.CheckRecovery(context.Snapshot.Recovery!.RecoveryId);
                    context.ValidateResume(); // Must have explicitly returned control and valid target/grants.
                    if (!context.PreservedForHuman) return;
                    IInputSession? next = _intervention is null ? null :
                        await _intervention.BeginSessionAsync(InputOwner.ForRun(identity), cancellation.Token).ConfigureAwait(false);
                    try
                    {
                        context.CheckRecovery(context.Snapshot.Recovery!.RecoveryId);
                        context.InputSession = next;
                        context.AttachInput(_inputArbiter.AutomationToken);
                    }
                    catch { next?.Dispose(); throw; }
                };
            }
            runtime.ConfigureLifecycle(context);
            context.LifecycleChanged += ForwardLifecycle;
            lock (_lifecycleGate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(TaskScheduler));
                cancellation.Token.ThrowIfCancellationRequested();
                if (!_instances.TryAdd(id, context)) throw new InvalidOperationException("Duplicate instance id.");
            }
            Gateway.RegisterStartingRun(context, manifest);
            TaskExecutionContext owned = context;
            context.LifetimeCancellation = cancellation.Token.Register(owned.Cancel);
            NotifyInstancesChanged();
            // Capture is ready before Lua on_init can call rug.capture().
            if (hwnd != 0)
                await capture.StartAsync(hwnd, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            await runtime.InitializeAsync(Path.Combine(source.PluginDirectory, source.Entry),
                manifest, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            await runtime.PrepareLifecycleAsync().ConfigureAwait(false);
            context.Prepared = true;
            Gateway.CompleteStartingRun(context.InstanceId);
            runtime.ConfigureRecoveryHandler((reason, detail) => _recovery.RequestAsync(context, reason, detail));
            cancellation.Token.ThrowIfCancellationRequested();
            onReady?.Invoke(identity);
            lock (_lifecycleGate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(TaskScheduler));
                cancellation.Token.ThrowIfCancellationRequested();
                context.Runner = Task.Run(() => RunAsync(context));
            }
            NotifyInstancesChanged();
            return id;
        }
        catch (Exception error)
        {
            if (context is not null)
            {
                if (error is not OperationCanceledException || !cancellation.IsCancellationRequested)
                    context.RecordFailure(error);
                context.Cancel();
                await CleanupAsync(context).ConfigureAwait(false);
                throw;
            }
            cancellation.Cancel();
            if (identity is not null) _commandBus?.CloseInstance(identity.InstanceScope);
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
            authorizationCancellation.Dispose();
            inputCancellation.Dispose();
            inputSession?.Dispose();
            authorizationLease?.Dispose();
            cancellation.Dispose();
            throw;
        }
        finally
        {
            if (inputStarting) lock (_lifecycleGate)
            {
                if (--_startingInput == 0) _inputStartsDrained.TrySetResult(true);
            }
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
        await context.Completion.ConfigureAwait(false);
    }

    private Task<RecoveryResult> VerifyRecoveryAsync(Guid instanceId, Guid recoveryId, CancellationToken token)
    {
        if (!_instances.TryGetValue(instanceId, out var context))
            throw new AutomationGateway.GatewayRejectedException(OperationError.InstanceNotFound);
        return _recovery.VerifyAndContinueAsync(context, recoveryId, token);
    }

    private async Task StopInputRunsAsync()
    {
        Task starts;
        lock (_lifecycleGate) starts = _inputStartsDrained.Task;
        await starts.ConfigureAwait(false);
        var affected = Instances.Where(c => c.DeclaresInput && !c.PreservedForHuman).ToArray();
        await Task.WhenAll(affected.Select(c => StopTaskAsync(c.InstanceId))).ConfigureAwait(false);
        if (affected.Any(c => c.LastError is InputCleanupException))
            throw new InputUnavailableException("Input cleanup failed during takeover.");
    }

    private async Task<PluginManifest> GetPluginAsync(string pluginId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _catalogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _catalog ??= await Task.Run(() => _plugins.ScanPlugins(_pluginsRoot)
                .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);
            if (!_catalog.TryGetValue(pluginId, out PluginManifest? manifest))
                throw new KeyNotFoundException($"Plugin '{pluginId}' was not found.");
            Gateway.Authorization.Register(manifest);
            return manifest;
        }
        finally { _catalogGate.Release(); }
    }

    private async Task RunAsync(TaskExecutionContext context)
    {
        CancellationToken token = context.Cancellation.Token;
        try
        {
            await DriveHookAsync(context, token).ConfigureAwait(false); // on_init
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
            context.RecordFailure(ex);
            _logger.LogError(ex, "Automation instance {InstanceId} faulted", context.InstanceId);
        }
        finally
        {
            context.Cancel();
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
            // Ready/yield is a boundary too: pause must win before returning to on_tick.
            if (context.PauseRequested && result.State is ScriptState.Ready or ScriptState.Yielded or ScriptState.Paused)
                await context.WaitIfPausedAsync(token).ConfigureAwait(false);
            switch (result.State)
            {
                case ScriptState.Ready: return;
                case ScriptState.Yielded: resume = true; break;
                case ScriptState.Paused: break;
                case ScriptState.Stopped when context.IsBusinessClosed || token.IsCancellationRequested || context.InputEpochToken.IsCancellationRequested ||
                    context.AuthorizationLease?.Token.IsCancellationRequested == true:
                    // Linked cancellation callbacks can wake Lua before they reach the
                    // scheduler. A cancelled host-owned epoch/grant is sufficient evidence;
                    // a closed host lifecycle also proves Stop before token propagation.
                    // propagate it before classifying this terminal result as cancellation.
                    context.Cancel();
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
            // Gateway may have returned cancellation while native work is still alive.
            // Do not run on_stop or touch any service handle until actual work ends.
            await context.DrainOperationsAsync().ConfigureAwait(false);
            try { if (context.Prepared) await context.Runtime.InvokeStopHookAsync(_watchdogTimeout).ConfigureAwait(false); }
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
            try
            {
                TryCleanup(() => _commandBus?.CloseInstance(context.InstanceScope), "state close");
                TryCleanup(context.AuthorizationCancellation.Dispose, "authorization subscription");
                TryCleanup(context.InputCancellation.Dispose, "input subscription");
                TryCleanup(context.LifetimeCancellation.Dispose, "lifetime subscription");
                TryCleanup(() => context.InputSession?.Dispose(), "input session");
                TryCleanup(() => context.AuthorizationLease?.Dispose(), "authorization lease");
                TryCleanup(context.Cancellation.Dispose, "cancellation disposal");
                context.SetStatus(context.LastError is null ? AutomationTaskStatus.Stopped : AutomationTaskStatus.Faulted);
                _instances.TryRemove(context.InstanceId, out _);
                NotifyInstancesChanged();
            }
            finally
            {
                try { Gateway.EndRun(context.InstanceId); }
                finally { context.Complete(); }
            }
            void TryCleanup(Action action, string stage)
            { try { action(); } catch (Exception error) { RecordCleanupError(context, error, stage); } }
        }
    }

    private void ForwardLifecycle(object? sender, LifecycleChangedEventArgs args)
    {
        foreach (EventHandler<LifecycleChangedEventArgs> handler in LifecycleChanged?.GetInvocationList() ?? [])
            try { handler(this, args); } catch (Exception error) { _logger.LogWarning(error, "Lifecycle notification failed"); }
    }

    private void NotifyInstancesChanged()
    {
        try { InstancesChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception error) { _logger.LogWarning(error, "Instance-list notification failed"); }
    }

    private void RecordCleanupError(TaskExecutionContext context, Exception ex, string stage)
    {
        context.RecordFailure(ex);
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
        _inputStopRegistration.Dispose();
        _catalogGate.Dispose();
        _shutdown.Dispose();
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(true);
        return signal;
    }

    private sealed class PluginReloadLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
