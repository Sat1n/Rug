#nullable enable
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Services;

namespace Rug.UI.Core.Models;

public enum AutomationTaskStatus { Starting, Running, Paused, Stopping, Stopped, Faulted }

/// <summary>Single transition authority and actual-work lifetime for one run.
/// @shape identity: host RunIdentity; counters: actual Lua segments / service tasks.
/// @source services: Services/TaskScheduler.cs#class:TaskScheduler</summary>
public sealed class TaskExecutionContext
{
    private readonly object _gate = new();
    private TaskCompletionSource _resume = Completed(), _activation = Signal(), _pauseSignal = Signal();
    private TaskCompletionSource _inputDrained = Completed(), _segmentsDrained = Completed(), _workDrained = Completed();
    private readonly TaskCompletionSource _completion = Signal();
    private AutomationTaskStatus _status = AutomationTaskStatus.Starting, _beforePause;
    private bool _pauseRequested, _resumeRequested;
    private long _revision = 1;
    private int _inputs, _segments, _work;

    internal TaskExecutionContext(RunIdentity identity, CancellationTokenSource cancellation,
        IScriptRuntime runtime, ICaptureService capture, IInputService input)
    { Identity = identity; Cancellation = cancellation; Runtime = runtime; Capture = capture; Input = input; }

    public RunIdentity Identity { get; }
    public Guid InstanceId => Identity.InstanceId;
    public string PluginId => Identity.PluginId;
    public long RunGeneration => Identity.RunGeneration;
    public Guid? TargetBindingId => Identity.TargetBindingId;
    public nint TargetHwnd => Identity.TargetHwnd;
    public StateScope InstanceScope => Identity.InstanceScope;
    public CancellationTokenSource Cancellation { get; }
    public IScriptRuntime Runtime { get; }
    public AutomationTaskStatus TaskStatus => Snapshot.Status;
    public bool PauseRequested => Snapshot.PauseRequested;
    public LifecycleSnapshot Snapshot { get { lock (_gate) return SnapshotLocked(); } }
    public event EventHandler<LifecycleChangedEventArgs>? LifecycleChanged;
    public Exception? LastError { get; private set; }
    internal ICaptureService Capture { get; }
    internal IInputService Input { get; }
    internal Task? Runner { get; set; }
    internal Task Completion => _completion.Task;
    internal Task PauseSignal { get { lock (_gate) return _pauseSignal.Task; } }
    internal AuthorizationLease? AuthorizationLease { get; set; }
    internal CancellationTokenRegistration AuthorizationCancellation { get; set; }
    internal CancellationTokenRegistration InputCancellation { get; set; }
    internal CancellationTokenRegistration LifetimeCancellation { get; set; }
    // Never query a later input epoch after Return control.
    internal CancellationToken InputEpochToken { get; set; }
    internal bool DeclaresInput { get; set; }
    internal IInputSession? InputSession { get; set; }
    internal Action? ValidateResume { get; set; }
    internal bool Prepared { get; set; }

    private LifecycleSnapshot SnapshotLocked() => new(Identity, _status, _pauseRequested,
        _resumeRequested, _revision, LastError is not null);

    internal void SetStatus(AutomationTaskStatus status)
    {
        lock (_gate)
        {
            // on_init can finish just before a Pause wins this transition.
            // Preserve pending confirmation, but resume into the completed startup.
            if (_status == AutomationTaskStatus.Starting && status == AutomationTaskStatus.Running && _pauseRequested)
            { _beforePause = AutomationTaskStatus.Running; return; }
            bool legal = (_status, status) switch
            {
                (AutomationTaskStatus.Starting, AutomationTaskStatus.Running) => !_pauseRequested,
                (AutomationTaskStatus.Starting or AutomationTaskStatus.Running or AutomationTaskStatus.Paused,
                    AutomationTaskStatus.Stopping) => true,
                (AutomationTaskStatus.Stopping, AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted) => true,
                _ => false
            };
            if (!legal) return;
            _status = status; _revision++;
            if (status is AutomationTaskStatus.Stopping or AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted)
            { _pauseRequested = _resumeRequested = false; _resume.TrySetResult(); _pauseSignal.TrySetResult(); PulseActivation(); }
        }
        Notify();
    }

    /// <summary>True means accepted (also for duplicates), never confirmed Paused.</summary>
    internal bool Pause()
    {
        lock (_gate)
        {
            if (_status is not (AutomationTaskStatus.Starting or AutomationTaskStatus.Running or AutomationTaskStatus.Paused) || Cancellation.IsCancellationRequested) return false;
            if (_pauseRequested || _status == AutomationTaskStatus.Paused) return true;
            _beforePause = _status; _pauseRequested = true; _revision++;
            _resume = Signal(); _pauseSignal.TrySetResult();
        }
        Notify(); return true;
    }

    internal bool Resume()
    {
        lock (_gate) if (_status != AutomationTaskStatus.Paused || !_pauseRequested) return false;
        try { ValidateResume?.Invoke(); } // Service checks never run under the state lock.
        catch (Exception) when (Snapshot.Status is AutomationTaskStatus.Stopping or AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted)
        { return false; }
        lock (_gate)
        {
            if (_status != AutomationTaskStatus.Paused || !_pauseRequested || Cancellation.IsCancellationRequested) return false;
            if (_resumeRequested) return true;
            _resumeRequested = true; _revision++; _resume.TrySetResult();
        }
        Notify(); return true;
    }

    internal void Cancel()
    {
        lock (_gate) if (_status is AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted) return;
        SetStatus(AutomationTaskStatus.Stopping);
        try { if (!Cancellation.IsCancellationRequested) Cancellation.Cancel(); }
        catch (Exception error) { RecordFailure(error); }
        try { Runtime.Stop(); }
        catch (Exception error) { RecordFailure(error); }
    }

    /// <summary>Only the serial driver confirms pause/resumes Lua, after actual input and Lua drain.
    /// @shape token: run lifetime; boundary: all admitted input and Lua segments.
    /// @source runtime: Services/LuaRuntime.cs#class:LuaRuntime</summary>
    internal async Task WaitIfPausedAsync(CancellationToken token)
    {
        if (!PauseRequested) { token.ThrowIfCancellationRequested(); return; }
        Task boundary;
        lock (_gate) boundary = Task.WhenAll(_inputDrained.Task, _segmentsDrained.Task);
        await boundary.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (LastError is InputCleanupException error) throw error;
        Runtime.Pause();
        Task resume;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_status is AutomationTaskStatus.Stopping or AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted) return;
            if (_status != AutomationTaskStatus.Paused) { _status = AutomationTaskStatus.Paused; _revision++; }
            resume = _resume.Task;
        }
        Notify();
        await resume.WaitAsync(token).ConfigureAwait(false);
        ValidateResume?.Invoke();
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_status != AutomationTaskStatus.Paused || !_resumeRequested) return;
            _pauseRequested = _resumeRequested = false;
            _pauseSignal = Signal(); _status = _beforePause; _revision++; PulseActivation();
        }
        Runtime.Resume(); // Flag update only; Lua resumes in the existing serial driver.
        Notify();
    }

    internal async Task WaitForBusinessAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                ValidateBusinessLocked();
                token.ThrowIfCancellationRequested();
                if (!_pauseRequested) return;
                wait = _activation.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    internal IDisposable? TryEnterLuaSegment()
    {
        lock (_gate)
        {
            if (_pauseRequested || _status is not (AutomationTaskStatus.Starting or AutomationTaskStatus.Running) || Cancellation.IsCancellationRequested) return null;
            if (_segments++ == 0) _segmentsDrained = Signal();
            return new WorkLease(this, segment: true, input: false);
        }
    }

    // Gateway admission precedes dispatch queuing. Retain until REAL work/releases end.
    internal IDisposable EnterOperation(bool input)
    {
        lock (_gate)
        {
            ValidateBusinessLocked();
            Cancellation.Token.ThrowIfCancellationRequested();
            if (input && _pauseRequested) throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidLifecycleState);
            if (_work++ == 0) _workDrained = Signal();
            if (input && _inputs++ == 0) _inputDrained = Signal();
            return new WorkLease(this, segment: false, input);
        }
    }
    // Status and admission share the transition lock. Stopping closes the gate
    // before Notify/Cancel run outside it; cancellation propagation is not admission.
    internal bool IsBusinessClosed
    {
        get { lock (_gate) return _status is AutomationTaskStatus.Stopping or AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted; }
    }
    internal void ValidateBusiness()
    {
        lock (_gate) ValidateBusinessLocked();
    }
    private void ValidateBusinessLocked()
    {
        if (_status is AutomationTaskStatus.Stopping or AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted)
            throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidLifecycleState);
    }
    internal Task DrainOperationsAsync() { lock (_gate) return Task.WhenAll(_workDrained.Task, _segmentsDrained.Task); }
    private sealed class WorkLease(TaskExecutionContext context, bool segment, bool input) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (context._gate)
            {
                if (segment) { if (--context._segments == 0) context._segmentsDrained.TrySetResult(); }
                else
                {
                    if (--context._work == 0) context._workDrained.TrySetResult();
                    if (input && --context._inputs == 0) context._inputDrained.TrySetResult();
                }
            }
        }
    }
    internal void Complete() { LifecycleChanged = null; _completion.TrySetResult(); }
    internal void RecordFailure(Exception error)
    {
        lock (_gate)
        {
            if (LastError is not null) return;
            LastError = error; _revision++;
        }
        Notify();
    }
    private void PulseActivation() { _activation.TrySetResult(); _activation = Signal(); }
    private void Notify()
    {
        var args = new LifecycleChangedEventArgs(Snapshot);
        foreach (EventHandler<LifecycleChangedEventArgs> handler in LifecycleChanged?.GetInvocationList() ?? [])
            try { handler(this, args); } catch { /* Observers cannot alter lifecycle. */ }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource Completed() { var signal = Signal(); signal.SetResult(); return signal; }
}
