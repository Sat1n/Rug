#nullable enable
namespace Rug.UI.Core.Models;

public enum RuntimeUpdateStage { Pending, WaitingCheckpoint, Frozen, Cancelled, Failed,
    PreparingSwitch, StoppingSources, ValidatingStopped, Committed, RestoreCompleted, NeedsHuman }
public sealed record UpdateCheckpoint(Guid CheckpointId, long SubmissionSequence, string Fingerprint,
    RunIdentity Source, string NodeId);
public sealed record RuntimeUpdateProgress(RunIdentity Identity, bool Frozen, UpdateCheckpoint? Checkpoint,
    RunIdentity? Replacement = null);
/// <summary>Detached bounded host view. Frozen is not Paused and does not mean code was switched.</summary>
public sealed record RuntimeUpdateSnapshot(PluginInstallation Installation, Guid? UpdateId,
    string EffectiveVersion, string CandidateVersion, RuntimeUpdateStage Stage, OperationError Error,
    string Reason, long Revision, DateTimeOffset? Deadline, IReadOnlyList<RuntimeUpdateProgress> Instances);
public sealed class RuntimeUpdateChangedEventArgs(RuntimeUpdateSnapshot snapshot) : EventArgs
{
    public RuntimeUpdateSnapshot Snapshot { get; } = snapshot;
}
