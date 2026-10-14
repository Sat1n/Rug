#nullable enable
namespace Rug.UI.Core.Models;

/// <summary>Immutable view of one run. Revisions are local to the complete identity.</summary>
public sealed record LifecycleSnapshot(RunIdentity Identity, AutomationTaskStatus Status,
    bool PauseRequested, bool ResumeRequested, long Revision, bool HasFailure, RecoverySnapshot? Recovery = null);

public sealed class LifecycleChangedEventArgs(LifecycleSnapshot snapshot) : EventArgs
{
    public LifecycleSnapshot Snapshot { get; } = snapshot;
}
