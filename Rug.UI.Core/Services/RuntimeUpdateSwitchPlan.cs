#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

public sealed partial class RuntimeUpdateCoordinator
{
    private readonly Dictionary<long, (string Plugin, Func<PluginSnapshot, CancellationToken, Task<Abstractions.IPluginUiUpdate>> Handler)> _uiHandlers = [];
    private long _nextUiHandler;
    public IDisposable RegisterUiHandler(string pluginId, Func<PluginSnapshot, CancellationToken, Task<Abstractions.IPluginUiUpdate>> handler)
    {
        long id;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_uiHandlers.Count >= 64) throw new InvalidOperationException("Switch UI handler capacity exceeded.");
            _uiHandlers.Add(id = ++_nextUiHandler, (pluginId, handler));
        }
        return new UiRegistration(() => { lock (_gate) _uiHandlers.Remove(id); });
    }
    private sealed class UiRegistration(Action remove) : IDisposable
    { private int _closed; public void Dispose() { if (Interlocked.Exchange(ref _closed, 1) == 0) remove(); } }
    internal async Task<List<Abstractions.IPluginUiUpdate>> PrepareSwitchUiAsync(RuntimeSwitchWork work, CancellationToken token)
    {
        Func<PluginSnapshot, CancellationToken, Task<Abstractions.IPluginUiUpdate>>[] handlers;
        lock (_gate) handlers = _uiHandlers.Values.Where(p => p.Plugin.Equals(work.Candidate.PluginId, StringComparison.OrdinalIgnoreCase)).Select(p => p.Handler).ToArray();
        var stages = new List<Abstractions.IPluginUiUpdate>();
        try { foreach (var handler in handlers) stages.Add(await handler(work.Candidate, token).ConfigureAwait(false)); return stages; }
        catch { foreach (var stage in stages) await stage.DisposeAsync().ConfigureAwait(false); throw; }
    }
    internal RuntimeSwitchWork BeginSwitch(PluginInstallation installation, Guid id, string candidate, long revision, out bool start)
    {
        RuntimeSwitchWork work; RuntimeUpdateSnapshot snapshot;
        lock (_gate)
        {
            if (!_slots.TryGetValue(installation, out var slot) || slot.Candidate.ContentVersion != candidate)
                throw Reject(OperationError.UpdateSuperseded);
            if (slot.LastSwitch is { } replay && replay.Id == id) { start = false; return replay; }
            if (slot.Plan is not { } plan || plan.Id != id) throw Reject(OperationError.UpdateSuperseded);
            if (plan.Switch is { } existing) { start = false; return existing; }
            if (slot.Snapshot.Revision != revision || slot.Snapshot.Stage != RuntimeUpdateStage.Frozen ||
                plan.Drained.Count != plan.Runs.Length || plan.Runs.Any(r => !r.IsFrozenFor(id))) throw Reject(OperationError.UpdateUnavailable);
            plan.Original.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= plan.Deadline) throw Reject(OperationError.DeadlineExceeded);
            work = plan.Switch = new(id, slot.Effective, slot.Candidate, plan.Runs,
                new Dictionary<Guid, UpdateCheckpoint>(plan.Receipts));
            slot.LastSwitch = work;
            slot.Snapshot = snapshot = View(slot, RuntimeUpdateStage.PreparingSwitch, OperationError.None, "Preparing replacement group; sources retained");
            work.InitialSnapshot = snapshot;
            start = true;
        }
        Notify(snapshot); return work;
    }
    internal void CheckSwitch(RuntimeSwitchWork work, CancellationToken original, CancellationToken execution, DateTimeOffset deadline)
    {
        lock (_gate) CheckSwitchLocked(work, original, execution, deadline);
    }
    private Slot CheckSwitchLocked(RuntimeSwitchWork work, CancellationToken original, CancellationToken execution, DateTimeOffset deadline)
    {
        original.ThrowIfCancellationRequested(); execution.ThrowIfCancellationRequested();
        if (_disposed || work.Aborted.Task.IsCompleted) throw new OperationCanceledException();
        if (DateTimeOffset.UtcNow >= deadline) throw Reject(OperationError.DeadlineExceeded);
        var installation = PluginInstallation.From(work.Effective.Manifest);
        if (!_slots.TryGetValue(installation, out var slot) || slot.Plan?.Switch != work || slot.Plan.Id != work.Id ||
            slot.Candidate.ContentVersion != work.Candidate.ContentVersion || slot.Effective.ContentVersion != work.Effective.ContentVersion)
            throw Reject(OperationError.UpdateSuperseded);
        slot.Plan.Original.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= slot.Plan.Deadline) throw Reject(OperationError.DeadlineExceeded);
        return slot;
    }
    internal void RegisterReplacement(RuntimeSwitchWork work, TaskExecutionContext source, RunIdentity replacement)
    {
        RuntimeUpdateSnapshot snapshot;
        lock (_gate)
        {
            var slot = CheckSwitchLocked(work, default, default, DateTimeOffset.MaxValue);
            work.Replacements.Add(source.InstanceId, replacement);
            slot.Snapshot = snapshot = View(slot, RuntimeUpdateStage.PreparingSwitch, OperationError.None, "Replacement declaration ready");
        }
        Notify(snapshot);
    }
    /// <summary>Atomic irreversible decision. No callback/IO; normal source stops happen after releasing the lock.</summary>
    internal void BeginSourceStop(RuntimeSwitchWork work, CancellationToken original, CancellationToken execution, DateTimeOffset deadline)
    {
        RuntimeUpdateSnapshot snapshot;
        lock (_gate)
        {
            var slot = CheckSwitchLocked(work, original, execution, deadline);
            if (work.Sources.Any(r => !r.IsFrozenFor(work.Id) || r.LastError is not null)) throw Reject(OperationError.InvalidLifecycleState);
            work.Stopping = true;
            // The update's own Stop must not run ordinary cancellation/release handlers.
            foreach (var source in work.Sources) source.LifecycleChanged -= slot.Plan!.Handler;
            slot.Snapshot = snapshot = View(slot, RuntimeUpdateStage.StoppingSources, OperationError.None, "Stopping sources; original coroutines cannot resume");
        }
        Notify(snapshot);
    }
    internal void StoppedSources(RuntimeSwitchWork work)
    {
        RuntimeUpdateSnapshot? snapshot = null;
        lock (_gate)
            if (_slots.TryGetValue(PluginInstallation.From(work.Effective.Manifest), out var slot) && slot.Plan?.Switch == work)
                slot.Snapshot = snapshot = View(slot, RuntimeUpdateStage.ValidatingStopped, OperationError.None, "Sources stopped; validating fresh scene again");
        if (snapshot is not null) Notify(snapshot);
    }
    /// <summary>Publication callback contains only catalog assignments and group-gate release, never IO/notifications.</summary>
    internal RuntimeUpdateSnapshot CommitSwitch(RuntimeSwitchWork work, CancellationToken original, CancellationToken execution,
        DateTimeOffset deadline, Action publish, Action<RuntimeUpdateSnapshot> committed)
    {
        RuntimeUpdateSnapshot snapshot;
        lock (_gate)
        {
            var slot = CheckSwitchLocked(work, original, execution, deadline);
            if (!work.Stopping || work.Sources.Any(s => s.TaskStatus != AutomationTaskStatus.Stopped || s.LastError is not null))
                throw Reject(OperationError.InvalidLifecycleState);
            publish();
            work.Committed = true;
            slot.Snapshot = snapshot = View(slot, RuntimeUpdateStage.Committed, OperationError.None, "Switch committed; restore hooks not yet complete");
            committed(snapshot);
            work.Group.Opened.TrySetResult();
        }
        return snapshot; // Scheduler publishes after releasing candidate/catalog transition locks.
    }
    internal void PublishSwitchSnapshot(RuntimeUpdateSnapshot snapshot) => Notify(snapshot);
    internal RuntimeUpdateSnapshot FinishSwitch(RuntimeSwitchWork work, bool success, OperationError error, string reason)
    {
        RuntimeUpdateSnapshot snapshot;
        lock (_gate)
        {
            var installation = PluginInstallation.From(work.Effective.Manifest);
            if (!_slots.TryGetValue(installation, out var slot) || slot.Plan?.Switch != work)
                return work.InitialSnapshot with { Stage = RuntimeUpdateStage.Cancelled, Error = OperationError.UpdateSuperseded, Reason = reason };
            var plan = slot.Plan;
            slot.Snapshot = snapshot = View(slot, success ? RuntimeUpdateStage.RestoreCompleted : work.Stopping ? RuntimeUpdateStage.NeedsHuman : RuntimeUpdateStage.Failed,
                error, reason);
            // Irreversible failures get here only after the whole source group and candidates are cleaned.
            foreach (var source in plan.Runs) { source.LifecycleChanged -= plan.Handler; source.ReleaseUpdate(plan.Id); }
            slot.Plan = null; slot.Expires = DateTimeOffset.UtcNow.AddMinutes(10); plan.Ended.TrySetResult();
        }
        Notify(snapshot); return snapshot;
    }
}
