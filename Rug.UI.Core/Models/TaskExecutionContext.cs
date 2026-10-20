#nullable enable
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Services;

namespace Rug.UI.Core.Models;

public enum AutomationTaskStatus { Starting, Running, Paused, Recovering, AwaitingHuman, Stopping, Stopped, Faulted }

/// <summary>Single transition authority and actual-work lifetime for one run.
/// @shape identity: host RunIdentity; counters: actual Lua segments / service tasks.
/// @source services: Services/TaskScheduler.cs#class:TaskScheduler</summary>
public sealed partial class TaskExecutionContext
{
    internal sealed class HostStopWakeCancellation : OperationCanceledException;
    private readonly object _gate = new();
    private TaskCompletionSource _resume = Completed(), _activation = Signal(), _pauseSignal = Signal();
    private TaskCompletionSource _inputDrained = Completed(), _segmentsDrained = Completed(), _workDrained = Completed();
    private readonly TaskCompletionSource _completion = Signal();
    private AutomationTaskStatus _status = AutomationTaskStatus.Starting, _beforePause;
    private bool _pauseRequested, _resumeRequested;
    private long _revision = 1, _recoveryGeneration, _inputVersion;
    private readonly TaskCompletionSource _stopping = Signal();
    private RecoverySnapshot? _recovery;
    private AutomationTaskStatus _beforeRecovery;
    internal Task StopSignal => _stopping.Task;
    internal AuthorizationTicket? RecoveryAuthorization { get; set; }
    internal Action? ValidateRecovery { get; set; }
    internal Func<Task>? HandoffRecovery { get; set; }
    internal Func<Task>? AcquireRecoveryInput { get; set; }
    private bool _preservedForHuman;
    private bool _checkpointPending;
    private TaskCompletionSource _checkpointFinished = Completed();
    private long _checkpointSequence;
    private int _checkpointObservations;
    private TaskCompletionSource _checkpointObservationsDrained = Completed();
    internal bool CheckpointObservationInFlight { get { lock (_gate) return _checkpointObservations != 0; } }
    internal CheckpointRunMetadata? CheckpointMetadata { get; set; }
    internal SemaphoreSlim CheckpointGate { get; } = new(1);
    internal int CheckpointWaiters;
    internal bool PreservedForHuman { get { lock (_gate) return _preservedForHuman; } }
    private int _inputs, _segments, _work, _recoveryWork;
    internal bool RecoveryWorkInFlight { get { lock (_gate) return _recoveryWork != 0; } }

    internal TaskExecutionContext(RunIdentity identity, CancellationTokenSource cancellation,
        IScriptRuntime runtime, ICaptureService capture, IInputService input)
    { Identity = identity; Cancellation = cancellation; LifetimeToken = cancellation.Token; Runtime = runtime; Capture = capture; Input = input; }
    internal CancellationToken LifetimeToken { get; }

    public RunIdentity Identity { get; }
    public CheckpointOrigin? CheckpointOrigin { get; internal set; }
    internal void CommitCheckpointStart(CheckpointStartBoundary boundary, CancellationToken original,
        CancellationToken execution, CancellationToken grant, DateTimeOffset deadline)
    {
        lock (_gate)
        {
            if (_status != AutomationTaskStatus.Starting || LastError is not null) throw new HostStopWakeCancellation();
            original.ThrowIfCancellationRequested(); execution.ThrowIfCancellationRequested();
            LifetimeToken.ThrowIfCancellationRequested(); grant.ThrowIfCancellationRequested();
            InputEpochToken.ThrowIfCancellationRequested();
            if (InputSession is { } session) session.Token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline) throw new OperationCanceledException("Restore deadline exceeded.");
            boundary.Commit(Identity);
        }
    }
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
        _resumeRequested, _revision, LastError is not null, _recovery);

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
                (AutomationTaskStatus.Starting or AutomationTaskStatus.Running or AutomationTaskStatus.Paused or AutomationTaskStatus.Recovering or AutomationTaskStatus.AwaitingHuman,
                    AutomationTaskStatus.Stopping) => true,
                (AutomationTaskStatus.Stopping, AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted) => true,
                _ => false
            };
            if (!legal) return;
            _status = status; _revision++;
            if (status is AutomationTaskStatus.Stopping or AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted)
            { SwitchGroup?.Interrupt(); _stopping.TrySetResult(); _preservedForHuman = false; _pauseRequested = _resumeRequested = false; _resume.TrySetResult(); _pauseSignal.TrySetResult(); PulseActivation(); }
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
        lock (_gate) if (_updateFrozen || _status != AutomationTaskStatus.Paused || !_pauseRequested) return false;
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
        Task checkpoint;
        lock (_gate) checkpoint = _checkpointFinished.Task;
        await checkpoint.WaitAsync(token).ConfigureAwait(false);
        if (!PauseRequested) { token.ThrowIfCancellationRequested(); return; }
        Task boundary;
        lock (_gate) boundary = Task.WhenAll(_inputDrained.Task, _segmentsDrained.Task);
        await boundary.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (LastError is InputCleanupException error) throw error;
        lock (_gate) if (!_pauseRequested || _status is AutomationTaskStatus.Recovering or AutomationTaskStatus.AwaitingHuman) return;
        Runtime.Pause();
        Task resume;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (!_pauseRequested || _status is not (AutomationTaskStatus.Starting or AutomationTaskStatus.Running or AutomationTaskStatus.Paused)) return;
            if (_status != AutomationTaskStatus.Paused) { _status = AutomationTaskStatus.Paused; _revision++; }
            resume = _resume.Task;
        }
        Notify();
        await resume.WaitAsync(token).ConfigureAwait(false);
        lock (_gate)
        {
            if (IsTerminalLocked()) throw new HostStopWakeCancellation();
            token.ThrowIfCancellationRequested();
            if (_status != AutomationTaskStatus.Paused || !_resumeRequested) return;
        }
        try { ValidateResume?.Invoke(); } // Explicit resume only, outside the transition lock.
        catch (InputUnavailableException) when (IsBusinessClosed) { throw new HostStopWakeCancellation(); }
        lock (_gate)
        {
            if (IsTerminalLocked()) throw new HostStopWakeCancellation();
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
            if (SwitchGroup is { IsOpen: false } || _pauseRequested || _updateFrozen || _status is not (AutomationTaskStatus.Starting or AutomationTaskStatus.Running) || Cancellation.IsCancellationRequested) return null;
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
            if (_checkpointPending) throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidLifecycleState);
            Cancellation.Token.ThrowIfCancellationRequested();
            if (input && (_pauseRequested || _checkpointPending)) throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidLifecycleState);
            if (_work++ == 0) _workDrained = Signal();
            if (input && _inputs++ == 0) _inputDrained = Signal();
            return new WorkLease(this, segment: false, input);
        }
    }
    // Status and admission share the transition lock. Stopping closes the gate
    // before Notify/Cancel run outside it; cancellation propagation is not admission.
    internal bool IsBusinessClosed
    {
        get { lock (_gate) return SwitchGroup is { IsInterrupted: true } || _status is AutomationTaskStatus.Stopping or AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted; }
    }
    internal void ValidateBusiness()
    {
        lock (_gate) ValidateBusinessLocked();
    }
    internal void ValidateInputBusiness()
    {
        lock (_gate)
        { ValidateBusinessLocked(); if (_checkpointPending) throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidLifecycleState); }
    }
    private void ValidateBusinessLocked(bool preparation = false)
    {
        if (SwitchGroup is { IsInterrupted: true } || (!preparation && SwitchGroup is { IsOpen: false }) || _updateFrozen || _status is AutomationTaskStatus.Stopping or AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted or AutomationTaskStatus.Recovering or AutomationTaskStatus.AwaitingHuman)
            throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidLifecycleState);
    }
    internal (IDisposable Work, long Sequence) EnterCheckpoint(bool preparation = false)
    {
        lock (_gate)
        {
            ValidateBusinessLocked(preparation); Cancellation.Token.ThrowIfCancellationRequested();
            if (_checkpointPending) throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidLifecycleState);
            long sequence = checked(_checkpointSequence + 1);
            _checkpointPending = true; _checkpointFinished = Signal();
            if (_work++ == 0) _workDrained = Signal();
            _checkpointSequence = sequence;
            return (new CheckpointLease(this), sequence);
        }
    }
    internal void CheckCheckpoint()
    {
        lock (_gate)
        {
            if (IsTerminalLocked()) throw new HostStopWakeCancellation();
            Cancellation.Token.ThrowIfCancellationRequested();
            if (!_checkpointPending || _status is not (AutomationTaskStatus.Starting or AutomationTaskStatus.Running or AutomationTaskStatus.Paused))
                throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidLifecycleState);
        }
    }
    internal bool BeginCheckpointPublish(CheckpointRunMetadata metadata, CancellationToken token,
        CancellationToken callerToken, CancellationToken authorizationToken, DateTimeOffset deadline)
    {
        lock (_gate)
        {
            if (IsTerminalLocked() || !_checkpointPending || !ReferenceEquals(CheckpointMetadata, metadata) || Cancellation.IsCancellationRequested || LastError is not null)
                return false;
            // Linked-token propagation may be blocked by an unrelated cancellation callback.
            // Check the original sources at the same decision boundary as lifecycle/deadline.
            callerToken.ThrowIfCancellationRequested();
            authorizationToken.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            return DateTimeOffset.UtcNow < deadline;
        }
    }
    internal IDisposable EnterCheckpointObservation()
    { lock (_gate) { CheckCheckpoint(); if (_work++ == 0) _workDrained = Signal(); if (_checkpointObservations++ == 0) _checkpointObservationsDrained = Signal(); return new WorkLease(this, false, false, checkpointObservation: true); } }
    internal Task DrainCheckpointObservationsAsync() { lock (_gate) return _checkpointObservationsDrained.Task; }
    internal Task DrainCheckpointBoundaryAsync() { lock (_gate) return Task.WhenAll(_inputDrained.Task, _segmentsDrained.Task); }
    private sealed class CheckpointLease(TaskExecutionContext context) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (context._gate)
            { context._checkpointPending = false; context._checkpointFinished.TrySetResult(); if (--context._work == 0) context._workDrained.TrySetResult(); }
        }
    }
    internal RecoverySnapshot BeginRecovery(string reason)
    {
        RecoverySnapshot snapshot;
        lock (_gate)
        {
            ValidateBusinessLocked(); Cancellation.Token.ThrowIfCancellationRequested();
            if (_status is not (AutomationTaskStatus.Starting or AutomationTaskStatus.Running))
                throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidLifecycleState);
            _beforeRecovery = _status;
            var id = Guid.NewGuid();
            snapshot = _recovery = new(id, ++_recoveryGeneration, RecoveryPhase.Freezing, reason,
                new(RecoveryStatus.NeedsHuman, id, reason));
            _status = AutomationTaskStatus.Recovering; _pauseRequested = _resumeRequested = false;
            _resume.TrySetResult(); _revision++;
        }
        Notify(); return snapshot;
    }
    internal void CheckRecovery(Guid id) { lock (_gate) CheckRecoveryLocked(id); }
    private void CheckRecoveryLocked(Guid id)
    {
        if (IsTerminalLocked()) throw new OperationCanceledException("Host stopped recovery.");
        Cancellation.Token.ThrowIfCancellationRequested();
        if (_recovery?.RecoveryId != id || _status is not (AutomationTaskStatus.Recovering or AutomationTaskStatus.AwaitingHuman))
            throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidLifecycleState);
    }
    private bool IsTerminalLocked() => _status is AutomationTaskStatus.Stopping or AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted;
    internal void UpdateRecovery(Guid id, RecoveryPhase phase, RecoveryResult result, long validation = 0)
    {
        lock (_gate)
        {
            CheckRecoveryLocked(id);
            _recovery = _recovery! with { Phase = phase, Result = result, ValidationGeneration = validation == 0 ? _recovery.ValidationGeneration : validation };
            _status = phase == RecoveryPhase.AwaitingHuman ? AutomationTaskStatus.AwaitingHuman : AutomationTaskStatus.Recovering;
            _revision++;
        }
        Notify();
    }
    internal void FailRecovery(Guid id, RecoveryError error)
    {
        lock (_gate)
        {
            if (_recovery?.RecoveryId != id || _status is AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted) return;
            _recovery = _recovery with { Phase = RecoveryPhase.Failed,
                Result = new(RecoveryStatus.Failed, id, _recovery.Reason, error) };
            _revision++;
        }
        Notify(); // Never reopen Stopping merely to describe a late real resource failure.
    }
    /// <summary>Commit linearizes at the in-lock token/deadline check; Stop shares this lock.
    /// Only immutable identity checks and asynchronous TCS publication run here, never services/observers.</summary>
    internal bool ResolveRecovery(RecoveryRequest request, RecoveryResult result, long validationGeneration,
        CancellationToken token, DateTimeOffset expiresAt, TimeProvider clock, TaskCompletionSource<RecoveryResult> completion)
    {
        lock (_gate)
        {
            if (_status is not (AutomationTaskStatus.Recovering or AutomationTaskStatus.AwaitingHuman) ||
                Cancellation.IsCancellationRequested || LastError is not null ||
                request.PluginId != PluginId || request.InstanceId != InstanceId || request.RunGeneration != RunGeneration ||
                request.TargetBindingId != TargetBindingId || RecoveryAuthorization?.Installation != request.Installation ||
                _recovery?.RecoveryId != request.RecoveryId ||
                _recovery.Generation != request.RecoveryGeneration || _recovery.ValidationGeneration != validationGeneration ||
                _recovery.Phase != RecoveryPhase.Validating) return false;
            token.ThrowIfCancellationRequested();
            if (clock.GetUtcNow() >= expiresAt) throw new OperationCanceledException("Recovery verification deadline exceeded.");
            _recovery = _recovery with { Phase = RecoveryPhase.Resolved, Result = result };
            _status = _beforeRecovery; _preservedForHuman = false; _revision++; PulseActivation();
            completion.TrySetResult(result); // RunContinuationsAsynchronously; inseparable from state commitment.
        }
        Notify(); return true;
    }
    internal Task DrainRecoveryBoundaryAsync() { lock (_gate) return Task.WhenAll(_inputDrained.Task, _segmentsDrained.Task); }
    internal IDisposable EnterRecoveryWork(Guid id)
    {
        lock (_gate)
        {
            CheckRecoveryLocked(id);
            if (_work++ == 0) _workDrained = Signal();
            _recoveryWork++;
            return new WorkLease(this, false, false, recovery: true);
        }
    }
    // Pure mutation, called under the arbiter admission lock; no services or notifications.
    internal void PreserveAndRetireInput(Guid id)
    {
        lock (_gate)
        {
            CheckRecoveryLocked(id);
            if (_inputs != 0 || _segments != 0 || LastError is not null)
                throw new InputUnavailableException("Recovery input boundary is not safe.");
            _preservedForHuman = true; _inputVersion++; InputEpochToken = default;
        }
    }
    internal void AttachInput(CancellationToken token)
    {
        long version;
        lock (_gate)
        {
            if (IsTerminalLocked() || Cancellation.IsCancellationRequested) throw new OperationCanceledException();
            _preservedForHuman = false; InputEpochToken = token; version = ++_inputVersion;
        }
        InputCancellation = token.Register(() =>
        {
            lock (_gate)
            {
                if (version != _inputVersion || _preservedForHuman || IsTerminalLocked()) return;
                // Close lifetime admission atomically with the epoch/version test.
                _status = AutomationTaskStatus.Stopping; _stopping.TrySetResult();
                _pauseRequested = _resumeRequested = false; _resume.TrySetResult(); _pauseSignal.TrySetResult(); PulseActivation(); _revision++;
            }
            Notify(); Cancel();
        });
    }
    internal Task DrainOperationsAsync() { lock (_gate) return Task.WhenAll(_workDrained.Task, _segmentsDrained.Task); }
    private sealed class WorkLease(TaskExecutionContext context, bool segment, bool input, bool recovery = false, bool checkpointObservation = false) : IDisposable
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
                    if (recovery) context._recoveryWork--;
                    if (checkpointObservation && --context._checkpointObservations == 0) context._checkpointObservationsDrained.TrySetResult();
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
            SwitchGroup?.Interrupt(); // Signal only, before external lifecycle observers or cancellation callbacks.
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
