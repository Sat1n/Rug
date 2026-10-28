#nullable enable
namespace Rug.UI.Core.Models;

public sealed record CheckpointCandidate(CheckpointReadStatus Status, CheckpointRecord? Record);
public sealed record CheckpointChoice(Guid SelectionId, string PluginId, string? NodeId, Guid? SourceInstanceId,
    DateTime? TimestampUtc, long? Sequence, string Compatibility);
public sealed record CheckpointOrigin(Guid ParentInstanceId, Guid CheckpointId, long Sequence, string NodeId);
internal sealed record CheckpointStartPlan(Guid SelectionId, PluginManifest Manifest, CheckpointRunMetadata Metadata,
    IReadOnlyDictionary<string, object?> Configuration, CheckpointRecord Record, string RecordHash, string ManifestHash, string? ConfigFileHash,
    CheckpointNode Node, Abstractions.ICheckpointRestoreValidator Validator, DateTimeOffset ExpiresAt);
internal sealed class CheckpointStartBoundary
{
    private readonly object _gate = new();
    private RunIdentity? _identity;
    private bool _cancelled;
    private object? _result;
    public RunIdentity? Identity => Volatile.Read(ref _identity);
    public object? CommittedResult => Volatile.Read(ref _result) ?? Identity;
    public void CommitResult(object result) { lock (_gate) { if (_cancelled) throw new OperationCanceledException(); Volatile.Write(ref _result, result); } }
    public bool CancelPreparation() { lock (_gate) { if (_identity is not null || _result is not null) return false; _cancelled = true; return true; } }
    public void Commit(RunIdentity identity)
    { lock (_gate) { if (_cancelled) throw new OperationCanceledException(); Volatile.Write(ref _identity, identity); } }
}
internal sealed class CheckpointStartTimeoutException : TimeoutException;
