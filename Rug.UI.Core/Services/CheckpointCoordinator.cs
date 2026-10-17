#nullable enable
using System.Collections.Concurrent;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Trusted exact-code nodes, not plugin-generated validation claims.</summary>
public sealed class CheckpointNodeRegistry : ICheckpointNodeRegistry
{
    private readonly ConcurrentDictionary<(PluginInstallation, string, string, string), (CheckpointNode, ICheckpointValidator)> _nodes = new();
    public IDisposable Register(CheckpointNode node, ICheckpointValidator validator)
    {
        if (!CheckpointJson.ValidId(node.NodeId) || node.StateVersion <= 0) throw new ArgumentException("Invalid node.");
        var actions = CheckpointJson.Actions(System.Text.Json.JsonSerializer.Serialize(node.ActionIds));
        var key = (node.Installation, node.PluginVersion, node.ContentHash, node.NodeId);
        var value = (node with { ActionIds = actions }, validator);
        if (!_nodes.TryAdd(key, value)) throw new InvalidOperationException("Node already registered.");
        return new Registration(() => _nodes.TryRemove(new(key, value)));
    }
    public (CheckpointNode Node, ICheckpointValidator Validator)? Find(CheckpointRunMetadata metadata, string nodeId) =>
        _nodes.TryGetValue((metadata.Installation, metadata.PluginVersion, metadata.Code.ContentHash, nodeId), out var node) ? node : null;
    private sealed class Registration(Action remove) : IDisposable
    {
        private int _closed; public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) remove();
        }
    }
}

public sealed class CheckpointCoordinator(ICheckpointStore store, ICheckpointNodeRegistry nodes,
    IPluginAuthorization authorization, TimeSpan? validationTimeout = null) : ICheckpointCoordinator
{
    private readonly TimeSpan _timeout = validationTimeout ?? TimeSpan.FromSeconds(5);
    private int _validators;
    private sealed class CapacityException : Exception;
    public int ValidatorsInFlight => Volatile.Read(ref _validators);
    /// <summary>@shape input: bounded explicit JSON strings; identity/config/code: owning host context.
    /// @source publication: FileCheckpointStore.cs#class:FileCheckpointStore
    /// The actual worker retains its permit until IO finishes even when the yielded caller stops waiting.</summary>
    public async Task<CheckpointResult> CommitAsync(TaskExecutionContext context, string nodeId, string stateJson, string completedActionIdsJson, CancellationToken token = default)
    {
        if (context.IsBusinessClosed) throw new TaskExecutionContext.HostStopWakeCancellation();
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(validationTimeout));
        CheckpointSubmission submission;
        try
        {
            if (!CheckpointJson.ValidId(nodeId)) throw new ArgumentException("Invalid node ID.");
            submission = new(nodeId, CheckpointJson.Parse(stateJson), CheckpointJson.Actions(completedActionIdsJson));
        }
        catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException)
        {
            return new("rejected", CheckpointError.InvalidParameters);
        }
        var metadata = context.CheckpointMetadata ?? throw new InvalidOperationException("No immutable loaded-code identity.");
        var ticket = context.RecoveryAuthorization ?? throw new InvalidOperationException("No host authorization identity.");
        if (Interlocked.Increment(ref context.CheckpointWaiters) > 16)
        {
            Interlocked.Decrement(ref context.CheckpointWaiters); return new("rejected", CheckpointError.CapacityExceeded);
        }
        var actual = Task.Run(async () =>
        {
            bool entered = false;
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token, context.LifetimeToken);
            try
            {
                await context.CheckpointGate.WaitAsync(waiting.Token).ConfigureAwait(false); entered = true;
                if (context.CheckpointObservationInFlight) return new CheckpointResult("rejected", CheckpointError.Busy);
                var (work, sequence) = context.EnterCheckpoint();
                using (work)
                {
                    try
                    {
                        using (var grant = authorization.Admit(ticket, [Permission.Checkpoint]))
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.LifetimeToken, grant.Token, token))
                        {
                            deadline.CancelAfter(_timeout);
                            DateTimeOffset expiresAt = DateTimeOffset.UtcNow + _timeout;
                            try
                            {
                                await context.DrainCheckpointBoundaryAsync().WaitAsync(deadline.Token).ConfigureAwait(false);
                                context.CheckCheckpoint();
                                var registered = await FindAsync(metadata, nodeId, deadline.Token).ConfigureAwait(false);
                                if (registered is not { } binding) return new CheckpointResult("rejected", CheckpointError.NoValidator);
                                if (!binding.Node.ActionIds.Order(StringComparer.Ordinal).SequenceEqual(submission.CompletedActionIds.Order(StringComparer.Ordinal)))
                                    return new CheckpointResult("rejected", CheckpointError.InvalidParameters);
                                using var observation = new CheckpointObservation(context, authorization, deadline.Token);
                                CancellationToken validationToken = deadline.Token;
                                if (Interlocked.Increment(ref _validators) > 32)
                                {
                                    Interlocked.Decrement(ref _validators); return new CheckpointResult("rejected", CheckpointError.CapacityExceeded);
                                }
                                var validation = Task.Run(async () =>
                                {
                                    try { return await binding.Validator.ValidateAsync(observation, submission, validationToken).ConfigureAwait(false); } finally { Interlocked.Decrement(ref _validators); }
                                });
                                Observe(validation);
                                bool valid = await validation.WaitAsync(deadline.Token).ConfigureAwait(false);
                                context.CheckCheckpoint(); deadline.Token.ThrowIfCancellationRequested();
                                if (await FindAsync(metadata, nodeId, deadline.Token).ConfigureAwait(false) is not { } latest || !ReferenceEquals(latest.Validator, binding.Validator) || latest.Node != binding.Node)
                                    return new CheckpointResult("rejected", CheckpointError.NoValidator);
                                if (!valid) return new CheckpointResult("rejected", CheckpointError.ValidationRejected);
                                var record = new CheckpointRecord(1, Guid.NewGuid(), sequence, DateTime.UtcNow,
                                    metadata.InstallationId, context.PluginId, metadata.PluginVersion, metadata.Code.ContentHash,
                                    context.InstanceId, context.RunGeneration, nodeId, binding.Node.StateVersion,
                                    metadata.ConfigRevision, metadata.ConfigHash, submission.State, submission.CompletedActionIds);
                                return await store.PublishAsync(record, () => context.BeginCheckpointPublish(metadata, deadline.Token, token, grant.Token, expiresAt), deadline.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (context.IsBusinessClosed || context.Cancellation.IsCancellationRequested) { throw; }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            {
                                return new CheckpointResult("cancelled", CheckpointError.Cancelled);
                            }
                            catch (OperationCanceledException) when (deadline.IsCancellationRequested || DateTimeOffset.UtcNow >= expiresAt)
                            {
                                return new CheckpointResult("timed_out", CheckpointError.ValidationTimedOut);
                            }
                            catch (CapacityException) { return new CheckpointResult("rejected", CheckpointError.CapacityExceeded); }
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        context.RecordFailure(error); context.Cancel(); throw;
                    } // Before the actual-work permit can wake cleanup.
                    finally { await context.DrainCheckpointObservationsAsync().ConfigureAwait(false); }
                }
            }
            catch (AutomationGateway.GatewayRejectedException error) when (error.Error == OperationError.InvalidLifecycleState && context.IsBusinessClosed)
            {
                throw new TaskExecutionContext.HostStopWakeCancellation();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !context.IsBusinessClosed)
            {
                return new CheckpointResult("cancelled", CheckpointError.Cancelled);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                context.RecordFailure(error); context.Cancel(); throw;
            }
            finally
            {
                if (entered) context.CheckpointGate.Release(); Interlocked.Decrement(ref context.CheckpointWaiters);
            }
        });
        Observe(actual);
        if (await Task.WhenAny(actual, context.StopSignal).ConfigureAwait(false) != actual || context.IsBusinessClosed)
            throw new TaskExecutionContext.HostStopWakeCancellation();
        return await actual.ConfigureAwait(false);
    }
    private static void Observe(Task task) => _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
    private async Task<(CheckpointNode Node, ICheckpointValidator Validator)?> FindAsync(CheckpointRunMetadata metadata, string nodeId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _validators) > 32) { Interlocked.Decrement(ref _validators); throw new CapacityException(); }
        var lookup = Task.Run(() => { try { return nodes.Find(metadata, nodeId); } finally { Interlocked.Decrement(ref _validators); } });
        Observe(lookup);
        return await lookup.WaitAsync(token).ConfigureAwait(false);
    }
}

/// <summary>Only detached fresh pixels are exposed, never native handles/input/config/internal context.</summary>
public sealed class CheckpointObservation : IDisposable
{
    private readonly TaskExecutionContext _context;
    private readonly IPluginAuthorization _authorization;
    private readonly CancellationToken _token;
    private int _disposed;
    internal CheckpointObservation(TaskExecutionContext context, IPluginAuthorization authorization, CancellationToken token)
    {
        _context = context; _authorization = authorization; _token = token;
    }
    public async Task<CapturedFrame> CaptureFreshAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _token.ThrowIfCancellationRequested(); _context.CheckCheckpoint(); _context.ValidateRecovery?.Invoke();
        using var grant = _authorization.Admit(_context.RecoveryAuthorization!, [Permission.Capture]);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, grant.Token);
        CancellationToken executionToken = linked.Token;
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested(); _context.CheckCheckpoint();
            if (!_context.Capture.IsCapturing) throw new IOException("Checkpoint capture session is unavailable.");
            var permit = _context.EnterCheckpointObservation();
            var actual = Task.Run(async () =>
            {
                using (permit)
                {
                    try { _context.CheckCheckpoint(); executionToken.ThrowIfCancellationRequested(); return await _context.Capture.GrabFrameAsync(executionToken).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        _context.RecordFailure(error); _context.Cancel(); throw;
                    }
                }
            });
            _ = actual.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
            var frame = await actual.WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested(); _context.CheckCheckpoint(); _context.ValidateRecovery?.Invoke();
            if (frame is not null) return frame;
            await Task.Delay(16, linked.Token).ConfigureAwait(false);
        }
    }
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
