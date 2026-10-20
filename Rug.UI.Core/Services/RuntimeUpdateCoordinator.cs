#nullable enable
using System.Text.Json;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Bounded process-local checkpoint freezes. Never publishes candidates or restarts Lua.
/// @shape plans: at most one per installation, <=8 retained candidates, <=32 runs per plan.
/// @source candidates: PluginUpdates.cs#class:TaskScheduler</summary>
public sealed class RuntimeUpdateCoordinator : IRuntimeUpdateCoordinator
{
    public const int Capacity = 8, MaxInstances = 32;
    private sealed class Plan(Guid id, TaskExecutionContext[] runs, DateTimeOffset deadline, CancellationToken original)
    {
        public Guid Id { get; } = id;
        public TaskExecutionContext[] Runs { get; } = runs;
        public DateTimeOffset Deadline { get; } = deadline;
        public CancellationToken Original { get; } = original;
        public Dictionary<Guid, UpdateCheckpoint> Receipts { get; } = [];
        public HashSet<Guid> Drained { get; } = [];
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EventHandler<LifecycleChangedEventArgs>? Handler;
    }
    private sealed class Slot(PluginSnapshot effective, PluginSnapshot candidate, RuntimeUpdateSnapshot snapshot)
    {
        public PluginSnapshot Effective = effective, Candidate = candidate;
        public RuntimeUpdateSnapshot Snapshot = snapshot;
        public Plan? Plan;
        public DateTimeOffset Expires = DateTimeOffset.UtcNow.AddMinutes(10);
    }
    private readonly object _gate = new();
    private readonly Dictionary<PluginInstallation, Slot> _slots = [];
    private readonly HashSet<Task> _monitors = [];
    private readonly CancellationTokenSource _shutdown = new();
    private long _revision;
    private bool _disposed;
    public event EventHandler<RuntimeUpdateChangedEventArgs>? Changed;
    public int ActivePlanCount { get { lock (_gate) return _slots.Values.Count(s => s.Plan is not null); } }
    public int MonitorCount { get { lock (_gate) return _monitors.Count; } }
    public int SubscriptionCount { get { lock (_gate) return _slots.Values.Sum(s => s.Plan?.Runs.Length ?? 0); } }
    private void PruneLocked()
    {
        foreach (var key in _slots.Where(p => p.Value.Plan is null && p.Value.Expires <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) _slots.Remove(key);
    }
    public RuntimeUpdateSnapshot? Query(PluginInstallation installation)
    {
        lock (_gate) { PruneLocked(); return _slots.GetValueOrDefault(installation)?.Snapshot; }
    }
    internal void Pending(PluginSnapshot effective, PluginSnapshot candidate)
    {
        RuntimeUpdateSnapshot snapshot;
        var installation = PluginInstallation.From(effective.Manifest);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PruneLocked();
            if (_slots.TryGetValue(installation, out var old))
            {
                if (old.Candidate.ContentVersion == candidate.ContentVersion && old.Effective.ContentVersion == effective.ContentVersion) return;
                EndLocked(old, RuntimeUpdateStage.Cancelled, OperationError.UpdateSuperseded, "Candidate replaced");
            }
            else if (_slots.Count >= Capacity) throw new AutomationGateway.GatewayRejectedException(OperationError.CapacityExceeded);
            snapshot = new(installation, null, effective.ContentVersion, candidate.ContentVersion, RuntimeUpdateStage.Pending,
                OperationError.None, "Runtime update pending", ++_revision, null, Array.AsReadOnly(Array.Empty<RuntimeUpdateProgress>()));
            _slots[installation] = new(effective, candidate, snapshot);
        }
        Notify(snapshot);
    }
    internal void Applied(PluginSnapshot snapshot)
    {
        RuntimeUpdateSnapshot? changed = null;
        lock (_gate) if (_slots.Remove(PluginInstallation.From(snapshot.Manifest), out var slot))
        {
            EndLocked(slot, RuntimeUpdateStage.Cancelled, OperationError.UpdateSuperseded, "Effective snapshot changed");
            changed = slot.Snapshot;
        }
        if (changed is not null) Notify(changed);
    }
    internal bool BlocksStart(string pluginId)
    { lock (_gate) return _slots.Any(p => p.Key.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase) && p.Value.Plan is not null); }

    /// <summary>Caller owns the short per-plugin startup lease; only arming runs happens here.
    /// @shape runs: complete active set from scheduler; originals: unlinked caller cancellation.
    /// @source runs: TaskScheduler.cs#function:StartTaskWithReadyAsync</summary>
    internal RuntimeUpdateSnapshot Prepare(PluginInstallation installation, TaskExecutionContext[] runs,
        DateTimeOffset deadline, CancellationToken original, CancellationToken execution)
    {
        RuntimeUpdateSnapshot snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PruneLocked();
            if (!_slots.TryGetValue(installation, out var slot)) throw Reject(OperationError.NoPendingUpdate);
            if (slot.Plan is not null) throw Reject(OperationError.Busy);
            original.ThrowIfCancellationRequested(); execution.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline || deadline - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2)) throw Reject(OperationError.DeadlineExceeded);
            if (runs.Length is 0 or > MaxInstances) throw Reject(OperationError.UpdateUnavailable);
            if (runs.Any(r => r.CheckpointMetadata?.Code.ContentHash != slot.Effective.EntryCode.ContentHash ||
                r.CheckpointMetadata.PluginVersion != slot.Effective.Manifest.Version)) throw Reject(OperationError.UpdateUnavailable);
            var plan = new Plan(Guid.NewGuid(), runs, deadline, original);
            slot.Plan = plan;
            foreach (var run in runs)
            {
                if (!run.ArmUpdate(plan.Id))
                {
                    EndLocked(slot, RuntimeUpdateStage.Failed, OperationError.UpdateUnavailable, "All active runs must be Running at admission");
                    throw Reject(OperationError.UpdateUnavailable);
                }
            }
            plan.Handler = (_, args) =>
            {
                if (args.Snapshot.HasFailure || args.Snapshot.Status is AutomationTaskStatus.Stopping or AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted or AutomationTaskStatus.Recovering or AutomationTaskStatus.AwaitingHuman)
                    End(installation, plan.Id, RuntimeUpdateStage.Failed, OperationError.InvalidLifecycleState, "A source run stopped, failed or entered recovery");
            };
            foreach (var run in runs) run.LifecycleChanged += plan.Handler;
            slot.Snapshot = snapshot = View(slot, RuntimeUpdateStage.WaitingCheckpoint, OperationError.None, "Waiting for new validated checkpoints");
            var monitor = Task.Run(() => MonitorAsync(installation, plan));
            _monitors.Add(monitor);
            _ = monitor.ContinueWith(t => { _ = t.Exception; lock (_gate) _monitors.Remove(t); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
        }
        // Recheck missed transitions between arming and subscription, outside locks.
        foreach (var run in runs) if (run.IsBusinessClosed || run.LastError is not null)
            End(installation, snapshot.UpdateId!.Value, RuntimeUpdateStage.Failed, OperationError.InvalidLifecycleState, "Source run closed");
        Notify(Query(installation)!); return Query(installation)!;
    }
    private async Task MonitorAsync(PluginInstallation installation, Plan plan)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(plan.Original, _shutdown.Token);
        try
        {
            var remaining = plan.Deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            await plan.Ended.Task.WaitAsync(remaining, linked.Token).ConfigureAwait(false);
        }
        catch (TimeoutException) { End(installation, plan.Id, RuntimeUpdateStage.Failed, OperationError.DeadlineExceeded, "Update preparation expired"); }
        catch (OperationCanceledException) { End(installation, plan.Id, RuntimeUpdateStage.Cancelled, OperationError.Cancelled, "Preparation caller cancelled"); }
    }
    internal RuntimeUpdateSnapshot Cancel(PluginInstallation installation, Guid id)
    {
        RuntimeUpdateSnapshot snapshot;
        lock (_gate)
        {
            if (!_slots.TryGetValue(installation, out var slot) || slot.Snapshot.UpdateId != id) throw Reject(OperationError.UpdateSuperseded);
            EndLocked(slot, RuntimeUpdateStage.Cancelled, OperationError.None, "Preparation cancelled; original coroutines released");
            snapshot = slot.Snapshot;
        }
        Notify(snapshot); return snapshot;
    }
    private void End(PluginInstallation installation, Guid id, RuntimeUpdateStage stage, OperationError error, string reason)
    {
        RuntimeUpdateSnapshot? snapshot = null;
        lock (_gate) if (_slots.TryGetValue(installation, out var slot) && slot.Plan?.Id == id)
        { EndLocked(slot, stage, error, reason); snapshot = slot.Snapshot; }
        if (snapshot is not null) Notify(snapshot);
    }
    private void EndLocked(Slot slot, RuntimeUpdateStage stage, OperationError error, string reason)
    {
        if (slot.Plan is not { } plan) return;
        slot.Snapshot = View(slot, stage, error, reason);
        slot.Plan = null; // Decision precedes wake-up; no cancellation callback controls admission.
        slot.Expires = DateTimeOffset.UtcNow.AddMinutes(10);
        foreach (var run in plan.Runs)
        { run.LifecycleChanged -= plan.Handler; run.ReleaseUpdate(plan.Id); }
        plan.Ended.TrySetResult();
    }
    internal void Published(TaskExecutionContext context, CheckpointRecord record)
    {
        var installation = context.CheckpointMetadata!.Installation;
        // Real authorization/target checks never execute under the plan/transition lock.
        lock (_gate) if (!_slots.TryGetValue(installation, out var current) || current.Plan is null) return;
        context.ValidateRecovery?.Invoke();
        RuntimeUpdateSnapshot? changed = null;
        lock (_gate)
        {
            if (!_slots.TryGetValue(installation, out var slot) || slot.Plan is not { } plan || !plan.Runs.Contains(context)) return;
            if (plan.Original.IsCancellationRequested || DateTimeOffset.UtcNow >= plan.Deadline)
            { EndLocked(slot, RuntimeUpdateStage.Cancelled, OperationError.Cancelled, "Cancelled before freeze"); changed = slot.Snapshot; }
            else if (context.FreezeUpdate(plan.Id, record))
            {
                plan.Receipts[context.InstanceId] = new(record.CheckpointId, record.SubmissionSequence,
                    CheckpointJson.Hash(CheckpointJson.Canonical(JsonSerializer.SerializeToElement(record, CheckpointJson.Options))), context.Identity, record.NodeId);
                slot.Snapshot = changed = View(slot, RuntimeUpdateStage.WaitingCheckpoint, OperationError.None, "Checkpoint saved; draining admitted work");
            }
        }
        if (changed is not null) Notify(changed);
    }
    internal async Task ReturnedAsync(TaskExecutionContext context, CheckpointResult result)
    {
        var installation = context.CheckpointMetadata!.Installation;
        Guid id;
        RuntimeUpdateSnapshot? rejected = null;
        lock (_gate)
        {
            if (!_slots.TryGetValue(installation, out var slot) || slot.Plan is not { } plan || !plan.Runs.Contains(context)) return;
            id = plan.Id;
            if (result.Status != "committed")
            { EndLocked(slot, RuntimeUpdateStage.Failed, OperationError.CheckpointIncompatible, "Checkpoint rejected: " + result.Error); rejected = slot.Snapshot; }
            else if (!plan.Receipts.TryGetValue(context.InstanceId, out var receipt) ||
                receipt.CheckpointId != result.CheckpointId || receipt.SubmissionSequence != result.SubmissionSequence) return; // Never attach an old completion to a new plan.
        }
        if (rejected is not null) { Notify(rejected); return; }
        // CommitAsync has released the actual IO/observation permit and checkpoint semaphore.
        // This logical wait holds neither Native work permits nor the Lua VM lock.
        await context.DrainOperationsAsync().ConfigureAwait(false);
        RuntimeUpdateSnapshot? changed = null;
        lock (_gate)
        {
            if (_slots.TryGetValue(installation, out var slot) && slot.Plan is { } plan && plan.Id == id)
            {
                if (plan.Original.IsCancellationRequested || DateTimeOffset.UtcNow >= plan.Deadline || !context.IsFrozenFor(id))
                    EndLocked(slot, RuntimeUpdateStage.Failed, OperationError.Cancelled, "Freeze no longer admissible");
                else
                {
                    plan.Drained.Add(context.InstanceId);
                    slot.Snapshot = View(slot, plan.Drained.Count == plan.Runs.Length ? RuntimeUpdateStage.Frozen : RuntimeUpdateStage.WaitingCheckpoint,
                        OperationError.None, plan.Drained.Count == plan.Runs.Length ? "Frozen; not switched" : "Waiting for remaining instances");
                }
                changed = slot.Snapshot;
            }
        }
        if (changed is not null) Notify(changed);
        // The completed result stays in LuaRuntime's pending slot. Its next resume
        // waits outside both Lua serial gates, and consumes this result only after release.
    }
    private RuntimeUpdateSnapshot View(Slot slot, RuntimeUpdateStage stage, OperationError error, string reason)
    {
        var plan = slot.Plan!;
        return new(slot.Snapshot.Installation, plan.Id, slot.Effective.ContentVersion, slot.Candidate.ContentVersion, stage, error,
            reason, ++_revision, plan.Deadline, Array.AsReadOnly(plan.Runs.Select(r => new RuntimeUpdateProgress(r.Identity,
                stage is RuntimeUpdateStage.WaitingCheckpoint or RuntimeUpdateStage.Frozen && plan.Drained.Contains(r.InstanceId),
                plan.Receipts.GetValueOrDefault(r.InstanceId))).ToArray()));
    }
    private static AutomationGateway.GatewayRejectedException Reject(OperationError error) => new(error);
    private void Notify(RuntimeUpdateSnapshot snapshot)
    {
        foreach (EventHandler<RuntimeUpdateChangedEventArgs> handler in Changed?.GetInvocationList() ?? [])
            try { handler(this, new(snapshot)); } catch { /* Observers cannot change plan decisions. */ }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] monitors;
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            foreach (var slot in _slots.Values) EndLocked(slot, RuntimeUpdateStage.Cancelled, OperationError.Cancelled, "Host closing");
            _slots.Clear(); Changed = null; monitors = _monitors.ToArray();
        }
        _shutdown.Cancel(); await Task.WhenAll(monitors).ConfigureAwait(false); _shutdown.Dispose();
    }
}
