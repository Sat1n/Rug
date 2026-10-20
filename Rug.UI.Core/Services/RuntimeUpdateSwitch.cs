#nullable enable
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

public sealed partial class TaskScheduler
{
    private readonly IRuntimeUpdateCompatibilityRegistry _updateCompatibility;
    internal Func<Task>? BeforeUpdateSourceStop { get; set; }
    internal Func<Task>? BeforeUpdateGroupCommit { get; set; }
    private async Task<object?> SwitchRuntimeUpdateAsync(AutomationCallContext caller, OperationRequest request,
        CancellationToken execution, CancellationToken original, CheckpointStartBoundary boundary)
    {
        var work = RuntimeUpdates.BeginSwitch(caller.Authorization.Installation, request.Parameters.GetProperty("updateId").GetGuid(),
            request.Parameters.GetProperty("candidateVersion").GetString()!, request.Parameters.GetProperty("revision").GetInt64(), out bool start);
        if (start)
        {
            lock (_lifecycleGate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(TaskScheduler));
                if (++_starting == 1) _startsDrained = CompletedSignalForStart();
            }
            _ = ExecuteRuntimeSwitchAsync(work, request.Deadline, original, execution, boundary);
        }
        var result = await work.Result.Task.ConfigureAwait(false);
        if (work.Committed) boundary.CommitResult(result); // Replays retain the committed outcome after request cancellation.
        else if (result.Error == OperationError.ExecutionFailed) throw new InvalidOperationException("Runtime update execution or cleanup failed; inspect the update plan and host log.");
        else if (result.Error == OperationError.DeadlineExceeded) throw new CheckpointStartTimeoutException();
        else if (result.Error is OperationError.None or OperationError.Cancelled) throw new OperationCanceledException();
        else throw RejectRestore(result.Error);
        return result;
    }
    private static TaskCompletionSource<bool> CompletedSignalForStart() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>@shape work: one immutable group, <=32 one-to-one sources/replacements.
    /// @source publication: RuntimeUpdateSwitchPlan.cs#function:CommitSwitch
    /// Candidates retain actual observation work until cleanup; the old stop decision is irreversible.</summary>
    private async Task ExecuteRuntimeSwitchAsync(RuntimeSwitchWork work, DateTimeOffset deadline, CancellationToken original,
        CancellationToken execution, CheckpointStartBoundary boundary)
    {
        var candidates = new List<(TaskExecutionContext Source, TaskExecutionContext New, CheckpointStartPlan Plan, RuntimeUpdateCompatibility Rule)>();
        var uiStages = new List<IPluginUiUpdate>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(execution, _shutdown.Token);
        _ = work.Aborted.Task.ContinueWith(_ => { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } },
            CancellationToken.None, TaskContinuationOptions.None, System.Threading.Tasks.TaskScheduler.Default);
        try
        {
            if (_checkpointStore is null) throw RejectRestore(OperationError.RestoreUnavailable);
            uiStages = await RuntimeUpdates.PrepareSwitchUiAsync(work, lifetime.Token).ConfigureAwait(false);
            foreach (var source in work.Sources)
            {
                RuntimeUpdates.CheckSwitch(work, original, lifetime.Token, deadline);
                var prepared = await PrepareUpdateRestoreAsync(work, source, lifetime.Token).ConfigureAwait(false);
                var origin = new RuntimeUpdateOrigin(work.Id, prepared.Rule.RuleId, prepared.Plan.RecordHash,
                    work.Effective.ContentVersion, work.Candidate.ContentVersion);
                var target = source.VerifiedTarget;
                if (target is not null)
                {
                    _inputArbiter.ValidateTarget(target, requireControl: false);
                    var rebound = _inputArbiter.BindTarget(target.Hwnd);
                    if (rebound.WindowStamp != target.WindowStamp) throw RejectRestore(OperationError.BindingMismatch);
                    target = rebound;
                }
                var id = await StartCoreAsync(source.PluginId, source.TargetHwnd, new Dictionary<string, object?>(), null,
                    lifetime.Token, prepared.Plan, target, original, deadline,
                    switchCandidate: new(work.Group, origin)).ConfigureAwait(false);
                var candidate = _instances[id];
                candidates.Add((source, candidate, prepared.Plan, prepared.Rule));
                RuntimeUpdates.RegisterReplacement(work, source, candidate.Identity);
                await ValidateUpdateSceneAsync(candidate, prepared.Plan, lifetime.Token, original, deadline).ConfigureAwait(false);
            }
            await CheckUpdateGroupAsync(work, candidates, original, lifetime.Token, deadline).ConfigureAwait(false);
            if (BeforeUpdateSourceStop is not null) await BeforeUpdateSourceStop().ConfigureAwait(false);
            await CheckUpdateGroupAsync(work, candidates, original, lifetime.Token, deadline).ConfigureAwait(false);
            RuntimeUpdates.BeginSourceStop(work, original, lifetime.Token, deadline);
            InputExecutionBoundary.MarkStarted(); // Request cancellation after this decision cannot promise the old coroutine back.
            // Capture Pause atomically with source business closure, before normal Stop clears the flag.
            foreach (var pair in candidates) if (pair.Source.CloseForUpdate()) pair.New.Pause();
            await Task.WhenAll(work.Sources.Select(s => StopTaskAsync(s.InstanceId))).ConfigureAwait(false);
            if (work.Sources.Any(s => s.LastError is not null)) throw new InvalidOperationException("Source cleanup failed.");
            RuntimeUpdates.StoppedSources(work);
            foreach (var pair in candidates)
            {
                RuntimeUpdates.CheckSwitch(work, original, lifetime.Token, deadline);
                await ValidateUpdateSceneAsync(pair.New, pair.Plan, lifetime.Token, original, deadline).ConfigureAwait(false);
            }
            foreach (var pair in candidates.Where(p => p.New.DeclaresInput && _intervention is not null))
            {
                pair.New.InputSession = await _intervention!.BeginSessionAsync(InputOwner.ForRun(pair.New.Identity), lifetime.Token).ConfigureAwait(false);
                pair.New.AttachInput(_inputArbiter.AutomationToken);
            }
            await CheckUpdateGroupAsync(work, candidates, original, lifetime.Token, deadline).ConfigureAwait(false);
            if (BeforeUpdateGroupCommit is not null) await BeforeUpdateGroupCommit().ConfigureAwait(false);
            // Final external checks happen outside all plan/lifecycle locks.
            await CheckUpdateGroupAsync(work, candidates, original, lifetime.Token, deadline).ConfigureAwait(false);
            foreach (var pair in candidates) Gateway.CompleteStartingRun(pair.New.InstanceId);
            RuntimeUpdateSnapshot? committed = null;
            await using (await AcquirePluginReloadAsync(work.Candidate.PluginId, lifetime.Token).ConfigureAwait(false))
            {
                await CheckUpdateGroupAsync(work, candidates, original, lifetime.Token, deadline).ConfigureAwait(false);
                foreach (var stage in uiStages) await stage.CommitAsync(lifetime.Token).ConfigureAwait(false);
                await CheckUpdateGroupAsync(work, candidates, original, lifetime.Token, deadline).ConfigureAwait(false);
                await _catalogGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    var snapshots = new Dictionary<string, PluginSnapshot>(_snapshots!, StringComparer.OrdinalIgnoreCase) { [work.Candidate.PluginId] = work.Candidate };
                    var catalog = new Dictionary<string, PluginManifest>(_catalog!, StringComparer.OrdinalIgnoreCase) { [work.Candidate.PluginId] = work.Candidate.Manifest };
                    TaskExecutionContext.CommitSwitchCandidates(candidates.Select(p => p.New).ToArray(), () =>
                    {
                        foreach (var pair in candidates) pair.Rule.RegistrationToken.ThrowIfCancellationRequested();
                        committed = RuntimeUpdates.CommitSwitch(work, original, lifetime.Token, deadline,
                            () => { _snapshots = snapshots; _catalog = catalog; foreach (var pair in candidates) pair.New.Prepared = true; },
                            boundary.CommitResult);
                    });
                    work.Result.TrySetResult(committed!);
                }
                finally { _catalogGate.Release(); }
            }
            RuntimeUpdates.PublishSwitchSnapshot(committed!);
            foreach (var stage in uiStages) await stage.DisposeAsync().ConfigureAwait(false);
            uiStages.Clear();
            NotifyInstancesChanged();
            // Success means committed, not completed. Paused inherited runs remain pending here until explicit Resume.
            await ObserveRestoredGroupAsync(work, candidates.Select(p => p.New).ToArray()).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (error is not (OperationCanceledException or TimeoutException or AutomationGateway.GatewayRejectedException or InputUnavailableException) &&
                !work.Sources.Any(s => s.LastError is not null) && candidates.All(p => p.New.LastError is null) &&
                candidates.LastOrDefault().New is { LastError: null, IsBusinessClosed: false } failedCandidate)
                failedCandidate.RecordFailure(error);
            // Never release a peer's old hold after the irreversible decision. Stop the complete source set first.
            if (work.Stopping)
            {
                foreach (var source in work.Sources) source.Cancel();
                await Task.WhenAll(work.Sources.Select(s => s.Completion)).ConfigureAwait(false);
            }
            foreach (var pair in candidates) pair.New.Cancel();
            await Task.WhenAll(candidates.Select(p => p.New.Completion)).ConfigureAwait(false);
            var code = error switch
            {
                AutomationGateway.GatewayRejectedException rejection => rejection.Error,
                Exceptions.PermissionDeniedException => OperationError.PermissionDenied,
                InputUnavailableException => OperationError.InputUnavailable,
                OperationCanceledException => OperationError.Cancelled,
                TimeoutException => OperationError.DeadlineExceeded,
                _ => OperationError.ExecutionFailed
            };
            if (candidates.Any(p => p.New.LastError is not null) || work.Sources.Any(s => s.LastError is not null)) code = OperationError.ExecutionFailed;
            var result = RuntimeUpdates.FinishSwitch(work, false, code,
                work.Stopping ? "Switch failed after source stop; manual handling required" : "Replacement preparation failed; old holds released");
            work.Result.TrySetResult(result);
        }
        finally
        {
            foreach (var stage in uiStages.AsEnumerable().Reverse())
            {
                try { if (!work.Committed) await stage.RollbackAsync().ConfigureAwait(false); await stage.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { _logger.LogError(error, "Runtime switch UI cleanup failed"); }
            }
            lock (_lifecycleGate) if (--_starting == 0) _startsDrained.TrySetResult(true);
        }
    }
    private async Task ObserveRestoredGroupAsync(RuntimeSwitchWork work, TaskExecutionContext[] replacements)
    {
        var pending = replacements.Select(c => c.FirstHookCompleted.Task).ToList();
        var ended = Task.WhenAny(replacements.Select(c => c.Completion));
        while (pending.Count > 0)
        {
            var next = await Task.WhenAny(pending.Cast<Task>().Append(ended)).ConfigureAwait(false);
            if (next == ended)
            {
                if (replacements.Any(c => c.LastError is not null)) throw new InvalidOperationException("Replacement failed before group restoration completed.");
                throw new OperationCanceledException("Replacement stopped before group restoration completed.");
            }
            var hook = (Task<bool>)next; pending.Remove(hook);
            if (!await hook.ConfigureAwait(false))
            {
                if (replacements.Any(c => c.LastError is not null)) throw new InvalidOperationException("Replacement restore failed.");
                throw new OperationCanceledException("Replacement restore stopped.");
            }
        }
        RuntimeUpdates.FinishSwitch(work, true, OperationError.None, "All restore hooks completed; sources remain stopped");
    }
    private async Task<(CheckpointStartPlan Plan, RuntimeUpdateCompatibility Rule)> PrepareUpdateRestoreAsync(
        RuntimeSwitchWork work, TaskExecutionContext source, CancellationToken token)
    {
        var metadata = source.CheckpointMetadata!;
        var receipt = work.Receipts[source.InstanceId];
        var selected = (await _checkpointStore!.QueryAsync(metadata.Installation, token).ConfigureAwait(false))
            .FirstOrDefault(c => c.Record?.CheckpointId == receipt.CheckpointId)?.Record;
        if (selected is null) throw RejectRestore(OperationError.CheckpointChanged);
        var record = (await _checkpointStore.ReadAsync(new(metadata.InstallationId, source.PluginId, source.InstanceId, source.RunGeneration),
            new(metadata.InstallationId, source.PluginId, metadata.PluginVersion, metadata.Code.ContentHash, receipt.NodeId,
                selected.NodeStateVersion, metadata.ConfigHash), token).ConfigureAwait(false)).Record;
        if (record is null || RecordHash(record) != receipt.Fingerprint || record.CheckpointId != receipt.CheckpointId || record.SubmissionSequence != receipt.SubmissionSequence)
            throw RejectRestore(OperationError.CheckpointChanged);
        var registered = await FindUpdateRuleAsync(metadata.Installation, work.Effective.ContentVersion, work.Candidate.ContentVersion, record.NodeId, token).ConfigureAwait(false)
            ?? throw RejectRestore(OperationError.UpdateIncompatible);
        var rule = registered.Rule;
        rule.RegistrationToken.ThrowIfCancellationRequested();
        var manifest = work.Candidate.Manifest;
        var config = RestoreConfiguration(manifest);
        if (rule.Installation != metadata.Installation || rule.SourceSnapshot != work.Effective.ContentVersion || rule.TargetSnapshot != work.Candidate.ContentVersion ||
            rule.NodeId != record.NodeId || rule.SourceVersion != metadata.PluginVersion || rule.TargetVersion != manifest.Version ||
            rule.SourceHash != record.ContentHash || rule.TargetHash != work.Candidate.EntryCode.ContentHash || rule.StateVersion != record.NodeStateVersion ||
            rule.ConfigHash != record.ConfigHash || CheckpointJson.ConfigHash(config.Values) != rule.ConfigHash ||
            !rule.CompletedActionIds.Order(StringComparer.Ordinal).SequenceEqual(record.CompletedActionIds.Order(StringComparer.Ordinal)) ||
            !manifest.Permissions.ToHashSet(StringComparer.Ordinal).SetEquals(work.Effective.Manifest.Permissions))
            throw RejectRestore(OperationError.UpdateIncompatible);
        var targetMetadata = new CheckpointRunMetadata(metadata.Installation, metadata.InstallationId, manifest.Version,
            work.Candidate.EntryCode, config.Revision, rule.ConfigHash);
        var node = new CheckpointNode(metadata.Installation, manifest.Version, targetMetadata.Code.ContentHash, rule.NodeId, rule.StateVersion, rule.CompletedActionIds);
        var plan = new CheckpointStartPlan(Guid.Empty, manifest, targetMetadata, config.Values, record, RecordHash(record),
            work.Candidate.Candidate.Files["manifest.json"].ContentHash!, work.Candidate.Candidate.Files["config.json"].ContentHash,
            node, registered.Validator, DateTimeOffset.UtcNow.AddMinutes(2));
        return (plan, rule);
    }
    private async Task CheckUpdateGroupAsync(RuntimeSwitchWork work,
        List<(TaskExecutionContext Source, TaskExecutionContext New, CheckpointStartPlan Plan, RuntimeUpdateCompatibility Rule)> candidates,
        CancellationToken original, CancellationToken execution, DateTimeOffset deadline)
    {
        RuntimeUpdates.CheckSwitch(work, original, execution, deadline);
        if (!_snapshots!.TryGetValue(work.Effective.PluginId, out var effective) || effective.ContentVersion != work.Effective.ContentVersion)
            throw RejectRestore(OperationError.UpdateSuperseded);
        // Disk is used only to invalidate the selection, never to load or publish replacement content.
        var disk = await new PluginUpdatePipeline().PrepareAsync(work.Candidate.Directory, 0, execution).ConfigureAwait(false);
        if (disk.Files.Count != work.Candidate.Candidate.Files.Count || disk.Files.Any(p =>
            !work.Candidate.Candidate.Files.TryGetValue(p.Key, out var file) || file.ContentHash != p.Value.ContentHash || file.Exists != p.Value.Exists))
            throw RejectRestore(OperationError.UpdateSuperseded);
        foreach (var pair in candidates)
        {
            var binding = await FindUpdateRuleAsync(pair.Rule.Installation, work.Effective.ContentVersion, work.Candidate.ContentVersion, pair.Rule.NodeId, execution).ConfigureAwait(false);
            if (binding is null || binding.Value.Rule != pair.Rule || !ReferenceEquals(binding.Value.Validator, pair.Plan.Validator)) throw RejectRestore(OperationError.UpdateIncompatible);
            if (CheckpointJson.ConfigHash(RestoreConfiguration(work.Candidate.Manifest).Values) != pair.Rule.ConfigHash) throw RejectRestore(OperationError.UpdateIncompatible);
            var read = await _checkpointStore!.ReadAsync(new(pair.Plan.Record.InstallationId, pair.Source.PluginId, pair.Source.InstanceId, pair.Source.RunGeneration),
                new(pair.Plan.Record.InstallationId, pair.Source.PluginId, pair.Plan.Record.PluginVersion, pair.Plan.Record.ContentHash,
                    pair.Plan.Record.NodeId, pair.Plan.Record.NodeStateVersion, pair.Plan.Record.ConfigHash), execution).ConfigureAwait(false);
            if (read.Record is null || RecordHash(read.Record) != pair.Plan.RecordHash) throw RejectRestore(OperationError.CheckpointChanged);
            pair.New.ValidateResume?.Invoke();
            if (pair.New.IsBusinessClosed || pair.New.LastError is not null) throw RejectRestore(OperationError.InvalidLifecycleState);
        }
        RuntimeUpdates.CheckSwitch(work, original, execution, deadline);
    }
    private async Task ValidateUpdateSceneAsync(TaskExecutionContext candidate, CheckpointStartPlan plan,
        CancellationToken execution, CancellationToken original, DateTimeOffset deadline)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(candidate.LifetimeToken, execution);
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw RejectRestore(OperationError.DeadlineExceeded);
        timeout.CancelAfter(remaining < CheckpointStartValidationTimeout ? remaining : CheckpointStartValidationTimeout);
        using var work = candidate.EnterCheckpoint(preparation: true).Work;
        using var observation = new CheckpointObservation(candidate, Gateway.Authorization, timeout.Token);
        if (Interlocked.Increment(ref _restoreValidators) > 32)
        { Interlocked.Decrement(ref _restoreValidators); throw RejectRestore(OperationError.CapacityExceeded); }
        var validation = Task.Run(async () =>
        { try { return await plan.Validator.ValidateAsync(observation, plan.Record, timeout.Token).ConfigureAwait(false); } finally { Interlocked.Decrement(ref _restoreValidators); } });
        _ = validation.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, System.Threading.Tasks.TaskScheduler.Default);
        try
        {
            if (!await validation.WaitAsync(timeout.Token).ConfigureAwait(false)) throw RejectRestore(OperationError.SceneRejected);
            original.ThrowIfCancellationRequested(); timeout.Token.ThrowIfCancellationRequested();
            candidate.CheckCheckpoint(); candidate.ValidateResume?.Invoke();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !execution.IsCancellationRequested && !original.IsCancellationRequested && !candidate.IsBusinessClosed)
        { throw new CheckpointStartTimeoutException(); }
        finally { observation.Dispose(); await candidate.DrainCheckpointObservationsAsync().ConfigureAwait(false); }
    }
    private async Task<(RuntimeUpdateCompatibility Rule, ICheckpointRestoreValidator Validator)?> FindUpdateRuleAsync(
        PluginInstallation installation, string source, string target, string node, CancellationToken token)
    {
        if (Interlocked.Increment(ref _restoreValidators) > 32)
        { Interlocked.Decrement(ref _restoreValidators); throw RejectRestore(OperationError.CapacityExceeded); }
        var lookup = Task.Run(() =>
        { try { return _updateCompatibility.Find(installation, source, target, node); } finally { Interlocked.Decrement(ref _restoreValidators); } });
        _ = lookup.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, System.Threading.Tasks.TaskScheduler.Default);
        return await lookup.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
    }
}
