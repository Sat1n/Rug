#nullable enable
using System.Text.Json;

namespace Rug.UI.Core.Models;

public enum CheckpointError
{
    None, InvalidParameters, NoValidator, ValidationRejected, ValidationTimedOut,
    Cancelled, StaleSubmission, CapacityExceeded, InvalidLifecycleState, Busy
}
public sealed record CheckpointResult(string Status, CheckpointError Error = CheckpointError.None,
    Guid? CheckpointId = null, long? SubmissionSequence = null)
{
    // Host-only attribution for every outcome; never serialized to disk or returned to Lua.
    internal CheckpointUpdateAttempt? UpdateAttempt { get; init; }
}
internal sealed record CheckpointUpdateAttempt(Guid AttemptId, RunIdentity Source, Guid? UpdateId);
public enum CheckpointReadStatus
{
    Absent, Valid, Corrupt, UnsupportedFormat, Oversized,
    InstallationMismatch, PluginMismatch, ContentMismatch, NodeVersionMismatch, ConfigIncompatible
}
public sealed record CheckpointAddress(string InstallationId, string PluginId, Guid SourceInstanceId, long SourceRunGeneration);
public sealed record CheckpointCompatibility(string InstallationId, string PluginId, string PluginVersion,
    string ContentHash, string NodeId, int NodeStateVersion, string ConfigHash);
public sealed record CheckpointRecord(int SchemaVersion, Guid CheckpointId, long SubmissionSequence, DateTime TimestampUtc,
    string InstallationId, string PluginId, string PluginVersion, string ContentHash,
    Guid SourceInstanceId, long SourceRunGeneration, string NodeId, int NodeStateVersion,
    long ConfigRevision, string ConfigHash, JsonElement State, IReadOnlyList<string> CompletedActionIds);
public sealed record CheckpointReadResult(CheckpointReadStatus Status, CheckpointRecord? Record = null);
public sealed record LoadedScriptContent(string Entry, string Source, string ContentHash);
public sealed record CheckpointRunMetadata(PluginInstallation Installation, string InstallationId,
    string PluginVersion, LoadedScriptContent Code, long ConfigRevision, string ConfigHash);
public sealed record CheckpointNode(PluginInstallation Installation, string PluginVersion, string ContentHash,
    string NodeId, int StateVersion, IReadOnlyList<string> ActionIds);
public sealed record CheckpointSubmission(string NodeId, JsonElement State, IReadOnlyList<string> CompletedActionIds);
public sealed record CheckpointStoreLimits(int MaxRecords = 256, long MaxBytes = 32 * 1024 * 1024, int MaxTemps = 64);
