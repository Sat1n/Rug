#nullable enable
namespace Rug.UI.Core.Models;

public enum RecoveryStatus { Resolved, NeedsHuman, Failed }
public enum RecoveryPhase { Freezing, Archiving, Attempting, HandingOff, AwaitingHuman, Validating, Resolved, Failed }
public enum RecoveryError { None, NoExecutor, NoValidator, ValidationRejected, AttemptTimedOut, ValidationTimedOut,
    ExecutorFailed, ResourceUnavailable, StaleRecovery, Busy, Cancelled, InvalidParameters }
public sealed record RecoveryResult(RecoveryStatus Status, Guid RecoveryId, string Reason, RecoveryError Error = RecoveryError.None);
public sealed record RecoverySnapshot(Guid RecoveryId, long Generation, RecoveryPhase Phase, string Reason,
    RecoveryResult Result, long ValidationGeneration = 0);
public sealed record RecoveryVerificationSummary(Guid RecoveryId, long Generation, string Status, string Error,
    string Reason, DateTime Timestamp);
/// <summary>Detached host identity. Contains neither services nor HWND/native objects.</summary>
public sealed record RecoveryRequest(Guid RecoveryId, string PluginId, Guid InstanceId, long RunGeneration,
    Guid? TargetBindingId, PluginInstallation Installation, long RecoveryGeneration, long AuthorizationRevision,
    string Scenario, string Context);
public sealed record RecoveryOptions(TimeSpan AttemptTimeout, TimeSpan ValidationTimeout)
{
    public static RecoveryOptions Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
}
public sealed class RecoveryFailedException(RecoveryError error) : Exception("Recovery failed: " + error)
{ public RecoveryError Error { get; } = error; }
