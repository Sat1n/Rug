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
