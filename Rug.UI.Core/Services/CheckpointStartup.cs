#nullable enable
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

public sealed partial class TaskScheduler
{
    private readonly ICheckpointStore? _checkpointStore;
    private readonly ICheckpointRestoreRegistry? _checkpointRestores;
    private readonly object _restoreGate = new();
    private readonly Dictionary<Guid, CheckpointStartPlan> _selections = [];
    private readonly HashSet<(string Installation, Guid Checkpoint)> _restoring = [];
    private int _restoreValidators;
    internal TimeSpan CheckpointStartValidationTimeout { get; set; } = TimeSpan.FromSeconds(10);
    internal Func<Task>? BeforeCheckpointStartDecision { get; set; }
    private static AutomationGateway.GatewayRejectedException RejectRestore(OperationError error) => new(error);
    private static string RecordHash(CheckpointRecord record) => CheckpointJson.Hash(CheckpointJson.Canonical(JsonSerializer.SerializeToElement(record, CheckpointJson.Options)));
    private static string RestoreJson(CheckpointRecord record) => JsonSerializer.Serialize(new
    {
        nodeId = record.NodeId, state = record.State, completedActionIds = record.CompletedActionIds,
        sourceInstanceId = record.SourceInstanceId, sourceRunGeneration = record.SourceRunGeneration,
        checkpointId = record.CheckpointId, submissionSequence = record.SubmissionSequence
    }, CheckpointJson.Options);
    private (long Revision, IReadOnlyDictionary<string, object?> Values) RestoreConfiguration(PluginManifest manifest)
    {
        var values = manifest.Configuration?.ToDictionary(p => p.Key, p => StateStore.CloneValue(p.Value)) ?? new();
        var configured = _commandBus?.SnapshotWithRevision(StateScope.Plugin(manifest.Id));
        if (configured is { } snapshot) foreach (var pair in snapshot.Values) values[pair.Key] = StateStore.CloneValue(pair.Value);
        return (configured?.Revision ?? 0, values);
    }
    /// <summary>@shape return: at most 128 detached summaries with bounded, expiring host selection IDs.
    /// @source candidates: Abstractions/ICheckpointService.cs#interface:ICheckpointStore</summary>
    public async Task<IReadOnlyList<CheckpointChoice>> QueryCheckpointsAsync(string pluginId, CancellationToken token = default)
    {
        if (_checkpointStore is null || _checkpointRestores is null) throw RejectRestore(OperationError.RestoreUnavailable);
        await RefreshPluginsAsync(token).ConfigureAwait(false); // Select current manifest/defaults, not stale catalog metadata.
        var source = await GetPluginAsync(pluginId, token).ConfigureAwait(false);
        var configuration = RestoreConfiguration(source);
        var code = CheckpointJson.Loaded(source.Entry, await File.ReadAllTextAsync(Path.Combine(source.PluginDirectory, source.Entry), token).ConfigureAwait(false));
        var installation = PluginInstallation.From(source);
        var metadata = new CheckpointRunMetadata(installation, CheckpointJson.InstallationId(installation), source.Version, code, configuration.Revision, CheckpointJson.ConfigHash(configuration.Values));
        var manifest = source with { Permissions = source.Permissions.ToList() };
        string manifestHash = CheckpointJson.Hash(await File.ReadAllBytesAsync(Path.Combine(source.PluginDirectory, "manifest.json"), token).ConfigureAwait(false));
        var candidates = await _checkpointStore.QueryAsync(installation, token).ConfigureAwait(false);
        var result = new List<CheckpointChoice>();
        foreach (var candidate in candidates.Take(128))
        {
            var record = candidate.Record; string status = candidate.Status.ToString(); Guid selection = Guid.Empty;
            if (record is not null)
            {
                var binding = await FindRestoreAsync(metadata, record.NodeId, token).ConfigureAwait(false);
                var expected = new CheckpointCompatibility(metadata.InstallationId, manifest.Id, metadata.PluginVersion, metadata.Code.ContentHash,
                    record.NodeId, binding?.Node.StateVersion ?? record.NodeStateVersion, metadata.ConfigHash);
                var current = await _checkpointStore.ReadAsync(new(record.InstallationId, record.PluginId, record.SourceInstanceId, record.SourceRunGeneration), expected, token).ConfigureAwait(false);
                status = current.Status.ToString();
                if (current.Status == CheckpointReadStatus.Valid && binding is { } node)
                {
                    selection = Guid.NewGuid();
                    var plan = new CheckpointStartPlan(selection, manifest, metadata, configuration.Values, record, RecordHash(record), manifestHash,
                        node.Node, node.Validator, DateTimeOffset.UtcNow.AddMinutes(5));
                    lock (_restoreGate)
                    {
                        if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(nameof(TaskScheduler));
                        foreach (var key in _selections.Where(p => p.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) _selections.Remove(key);
                        if (_selections.Count >= 256) _selections.Remove(_selections.Keys.First());
                        _selections.Add(selection, plan);
                    }
                    status = SourceActive(record) ? "SourceStillActive" : "Ready";
                }
                else if (current.Status == CheckpointReadStatus.Valid) status = "RestoreUnavailable";
            }
            result.Add(new(selection, manifest.Id, record?.NodeId, record?.SourceInstanceId, record?.TimestampUtc, record?.SubmissionSequence, status));
        }
        return result.AsReadOnly();
    }
    public async Task<AutomationCallContext> CreateCheckpointRestoreContextAsync(string pluginId, InputTarget target, Action<RunIdentity>? onReady = null, CancellationToken token = default)
    {
        _inputArbiter.ValidateTarget(target, requireControl: false);
        var caller = await Gateway.CreateContextAsync(pluginId, "checkpoint-host-ui", AutomationSource.PluginUI, startHwnd: target.Hwnd, onReady: onReady, cancellationToken: token).ConfigureAwait(false);
        caller.RestoreTarget = target;
        return caller;
    }
    private bool SourceActive(CheckpointRecord record) => _instances.TryGetValue(record.SourceInstanceId, out var source) &&
        source.TaskStatus is not (AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted);
    private async Task CheckStartPlanAsync(CheckpointStartPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (SourceActive(plan.Record)) throw RejectRestore(OperationError.SourceStillActive);
        if (plan.ExpiresAt <= DateTimeOffset.UtcNow) throw RejectRestore(OperationError.CheckpointChanged);
        var current = RestoreConfiguration(plan.Manifest);
        var code = CheckpointJson.Loaded(plan.Manifest.Entry, await File.ReadAllTextAsync(Path.Combine(plan.Manifest.PluginDirectory, plan.Manifest.Entry), token).ConfigureAwait(false));
        if (code != plan.Metadata.Code || CheckpointJson.ConfigHash(current.Values) != plan.Metadata.ConfigHash ||
            CheckpointJson.Hash(await File.ReadAllBytesAsync(Path.Combine(plan.Manifest.PluginDirectory, "manifest.json"), token).ConfigureAwait(false)) != plan.ManifestHash)
            throw RejectRestore(OperationError.CheckpointIncompatible);
        var node = await FindRestoreAsync(plan.Metadata, plan.Record.NodeId, token).ConfigureAwait(false);
        if (node is null || !ReferenceEquals(node.Value.Validator, plan.Validator) || node.Value.Node != plan.Node) throw RejectRestore(OperationError.RestoreUnavailable);
        var record = plan.Record;
        var read = await _checkpointStore!.ReadAsync(new(record.InstallationId, record.PluginId, record.SourceInstanceId, record.SourceRunGeneration),
            new(record.InstallationId, record.PluginId, record.PluginVersion, record.ContentHash, record.NodeId, record.NodeStateVersion, record.ConfigHash), token).ConfigureAwait(false);
        if (read.Record is null || RecordHash(read.Record) != plan.RecordHash) throw RejectRestore(OperationError.CheckpointChanged);
    }
    /// <summary>@shape selection: opaque host ID; return: newly committed run identity, never the source run.
    /// @source caller: Services/AutomationGateway.cs#class:AutomationGateway</summary>
    private async Task<RunIdentity> RestoreCheckpointAsync(AutomationCallContext caller, Guid selection, DateTimeOffset deadline,
        CancellationToken execution, CancellationToken original, CheckpointStartBoundary boundary)
    {
        CheckpointStartPlan plan;
        lock (_restoreGate)
        {
            if (!_selections.TryGetValue(selection, out plan!) || plan.Metadata.Installation != caller.Authorization.Installation) throw RejectRestore(OperationError.CheckpointChanged);
            var reservation = (plan.Metadata.InstallationId, plan.Record.CheckpointId);
            if (_restoring.Contains(reservation)) throw RejectRestore(OperationError.Busy);
            if (_restoring.Count >= 128) throw RejectRestore(OperationError.CapacityExceeded);
            _restoring.Add(reservation);
        }
        TaskExecutionContext? started = null;
        var reservationKey = (plan.Metadata.InstallationId, plan.Record.CheckpointId);
        lock (_lifecycleGate) { if (_disposed) { lock (_restoreGate) _restoring.Remove(reservationKey); throw new ObjectDisposedException(nameof(TaskScheduler)); } if (++_starting == 1) _startsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(execution, _shutdown.Token);
        var gate = _pluginStartGates.GetOrAdd(caller.PluginId, _ => new(1, 1)); bool entered = false;
        try
        {
            await gate.WaitAsync(lifetime.Token).ConfigureAwait(false); entered = true;
            await CheckStartPlanAsync(plan, lifetime.Token).ConfigureAwait(false);
            _inputArbiter.ValidateTarget(caller.RestoreTarget!, plan.Manifest.Permissions.Contains(Permission.ControlInput));
            var id = await StartCoreAsync(caller.PluginId, caller.StartHwnd, new Dictionary<string, object?>(), null, lifetime.Token,
                plan, caller.RestoreTarget, original, deadline, boundary).ConfigureAwait(false);
            if (!_instances.TryGetValue(id, out started)) return boundary.Identity!;
            _ = started.Completion.ContinueWith(_ => { lock (_restoreGate) _restoring.Remove(reservationKey); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
            try { caller.OnReady?.Invoke(started.Identity); } catch (Exception error) { _logger.LogWarning(error, "Checkpoint host observer failed"); }
            return started.Identity;
        }
        finally
        {
            if (entered) gate.Release();
            if (started is null) lock (_restoreGate) _restoring.Remove(reservationKey);
            lock (_lifecycleGate) { if (--_starting == 0) _startsDrained.TrySetResult(true); }
        }
    }
    /// <summary>@shape plan: frozen checkpoint/code/config/node; no business input before final startup commit.
    /// @source validator: Abstractions/ICheckpointService.cs#interface:ICheckpointRestoreValidator</summary>
    private async Task ValidateCheckpointStartAsync(TaskExecutionContext context, CheckpointStartPlan plan, CancellationToken execution, CancellationToken original, DateTimeOffset deadline)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.LifetimeToken, execution);
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new OperationCanceledException();
        timeout.CancelAfter(remaining < CheckpointStartValidationTimeout ? remaining : CheckpointStartValidationTimeout);
        var (work, _) = context.EnterCheckpoint();
        using (work)
        {
            using var observation = new CheckpointObservation(context, Gateway.Authorization, timeout.Token);
            if (Interlocked.Increment(ref _restoreValidators) > 32) { Interlocked.Decrement(ref _restoreValidators); throw RejectRestore(OperationError.CapacityExceeded); }
            var validation = Task.Run(async () => { try { return await plan.Validator.ValidateAsync(observation, plan.Record, timeout.Token).ConfigureAwait(false); } finally { Interlocked.Decrement(ref _restoreValidators); } });
            _ = validation.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
            try
            {
                var wait = validation.WaitAsync(timeout.Token);
                _ = wait.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
                if (await Task.WhenAny(wait, context.StopSignal).ConfigureAwait(false) != wait) throw new TaskExecutionContext.HostStopWakeCancellation();
                if (!await wait.ConfigureAwait(false)) throw RejectRestore(OperationError.SceneRejected);
                original.ThrowIfCancellationRequested(); context.CheckCheckpoint();
                await CheckStartPlanAsync(plan, timeout.Token).ConfigureAwait(false);
                context.ValidateResume?.Invoke();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !execution.IsCancellationRequested && !original.IsCancellationRequested && !context.IsBusinessClosed)
            { throw new CheckpointStartTimeoutException(); }
            finally { observation.Dispose(); await context.DrainCheckpointObservationsAsync().ConfigureAwait(false); }
        }
    }
    private async Task<(CheckpointNode Node, ICheckpointRestoreValidator Validator)?> FindRestoreAsync(CheckpointRunMetadata metadata, string node, CancellationToken token)
    {
        if (Interlocked.Increment(ref _restoreValidators) > 32) { Interlocked.Decrement(ref _restoreValidators); throw RejectRestore(OperationError.CapacityExceeded); }
        var actual = Task.Run(() => { try { return _checkpointRestores!.FindRestore(metadata, node); } finally { Interlocked.Decrement(ref _restoreValidators); } });
        _ = actual.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
        try { return await actual.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch (TimeoutException) { throw new CheckpointStartTimeoutException(); }
    }
}
