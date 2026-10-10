#nullable enable
namespace Rug.UI.Core.Models;

/// <summary>Aggregate host status only; no keys, input text or trajectories.</summary>
public sealed record InputInterventionSnapshot(long Revision, InputEventSourceState DetectorState,
    long Generation, int Sessions, bool Monitoring, string? Reason, string? DetectorError, string? CleanupError);
