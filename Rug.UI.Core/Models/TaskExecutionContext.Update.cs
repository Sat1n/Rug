#nullable enable
using Rug.UI.Core.Services;

namespace Rug.UI.Core.Models;

public sealed partial class TaskExecutionContext
{
    private Guid? _updateId;
    private bool _updateFrozen;
    private long _updateAfterSequence;
    private TaskCompletionSource _updateReleased = Completed();
    internal Action<CheckpointUpdateAttempt, CheckpointRecord>? CheckpointPublished { get; set; }
    public RuntimeUpdateOrigin? RuntimeUpdateOrigin { get; internal set; }
    internal InputTarget? VerifiedTarget { get; set; }
    internal RuntimeSwitchGroup? SwitchGroup { get; set; }
    internal TaskCompletionSource<bool> FirstHookCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Publication is pure pool mutation; Stop shares this boundary, without waiting for cancellation propagation.</summary>
    internal void PublishObservation(Action publish, CancellationToken token)
    {
        lock (_gate)
        {
            ValidateBusinessLocked(); token.ThrowIfCancellationRequested(); LifetimeToken.ThrowIfCancellationRequested();
            AuthorizationLease?.Token.ThrowIfCancellationRequested();
            publish();
        }
    }
    internal bool CloseForUpdate()
    {
        bool paused;
        lock (_gate)
        {
            paused = _pauseRequested;
            if (!IsTerminalLocked())
            {
                _status = AutomationTaskStatus.Stopping; _revision++;
                _stopping.TrySetResult(); _preservedForHuman = false;
                _pauseRequested = _resumeRequested = false;
                _resume.TrySetResult(); _pauseSignal.TrySetResult(); PulseActivation();
            }
        }
        Notify(); Cancel(); return paused;
    }
    internal static void CommitSwitchCandidates(TaskExecutionContext[] candidates, Action publish)
        => WithSwitchCandidatesLocked(candidates, () =>
        {
            foreach (var candidate in candidates)
            {
                if (candidate._status != AutomationTaskStatus.Starting || candidate.LastError is not null) throw new HostStopWakeCancellation();
                candidate.LifetimeToken.ThrowIfCancellationRequested();
                candidate.AuthorizationLease!.Token.ThrowIfCancellationRequested();
                candidate.InputEpochToken.ThrowIfCancellationRequested();
                candidate.InputSession?.Token.ThrowIfCancellationRequested();
            }
            publish();
        });
    /// <summary>Fault/Stop and completion share the complete fixed-order lifecycle lock set.
    /// @shape candidates: exact prepared replacement group; publish: pure plan mutation, no notifications.
    /// @source group: Models/RuntimeSwitchModels.cs#class:RuntimeSwitchGroup</summary>
    internal static void CompleteSwitchRestoration(TaskExecutionContext[] candidates, RuntimeSwitchGroup group, Action publish)
        => WithSwitchCandidatesLocked(candidates, () =>
        {
            // First-hook success is retained even if a later tick fails. Read actual lifecycle under the same locks as failure/Stop.
            if (candidates.Any(c => c.LastError is not null)) throw new InvalidOperationException("Replacement failed before group restoration completed.");
            if (candidates.Any(c => c.IsTerminalLocked() || c._status == AutomationTaskStatus.Stopping ||
                c.LifetimeToken.IsCancellationRequested || !c.FirstHookCompleted.Task.IsCompletedSuccessfully || !c.FirstHookCompleted.Task.Result) ||
                !group.CompleteRestoration()) throw new HostStopWakeCancellation();
            publish();
        });
    private static void WithSwitchCandidatesLocked(TaskExecutionContext[] candidates, Action publish)
    {
        // Fixed identity order; no IO/service/UI callbacks under the transition locks.
        var ordered = candidates.OrderBy(c => c.InstanceId).ToArray();
        int entered = 0;
        try
        {
            foreach (var candidate in ordered) { Monitor.Enter(candidate._gate); entered++; }
            publish();
        }
        finally { for (int i = entered - 1; i >= 0; i--) Monitor.Exit(ordered[i]._gate); }
    }
    internal CheckpointUpdateAttempt CaptureCheckpointAttempt()
    { lock (_gate) return new(Guid.NewGuid(), Identity, _updateId); }
    internal bool ArmUpdate(Guid id)
    {
        lock (_gate)
        {
            if (_updateId is not null || _status != AutomationTaskStatus.Running || _pauseRequested ||
                LastError is not null || LifetimeToken.IsCancellationRequested) return false;
            _updateId = id; _updateAfterSequence = _checkpointSequence;
            _updateReleased = Signal(); _revision++; return true;
        }
    }
    internal bool FreezeUpdate(Guid id, CheckpointRecord record)
    {
        lock (_gate)
        {
            if (_updateId != id || _updateFrozen || !_checkpointPending || record.SubmissionSequence <= _updateAfterSequence ||
                record.SourceInstanceId != InstanceId || record.SourceRunGeneration != RunGeneration ||
                _status != AutomationTaskStatus.Running || LastError is not null || LifetimeToken.IsCancellationRequested) return false;
            _updateFrozen = true; _revision++; return true;
        }
    }
    internal bool IsFrozenFor(Guid id) { lock (_gate) return _updateId == id && _updateFrozen && !IsTerminalLocked(); }
    internal bool UpdateFrozen { get { lock (_gate) return _updateFrozen; } }
    internal void ReleaseUpdate(Guid id)
    {
        lock (_gate)
        {
            if (_updateId != id) return;
            _updateId = null; _updateFrozen = false; _revision++;
            _updateReleased.TrySetResult(); PulseActivation();
        }
    }
    internal async Task WaitForUpdateBoundaryAsync()
    {
        Task release, pause;
        lock (_gate) { if (!_updateFrozen) return; release = _updateReleased.Task; pause = _pauseSignal.Task; }
        await Task.WhenAny(release, StopSignal, pause).ConfigureAwait(false);
        if (IsBusinessClosed) throw new HostStopWakeCancellation();
        LifetimeToken.ThrowIfCancellationRequested();
    }
}
