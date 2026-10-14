#nullable enable
using System.Text.Json.Serialization;

namespace Rug.UI.Core.Models;

public sealed record AnomalyLogModel
{
    public required DateTime Timestamp { get; init; }
    public required string PluginId { get; init; }
    public required Guid InstanceId { get; init; }
    public required string Reason { get; init; }
    public required string ScriptContext { get; init; }
    public required string ScreenshotPath { get; init; }

    public Guid? RecoveryId { get; init; }
    public long? RecoveryGeneration { get; init; }
    public long? RunGeneration { get; init; }
    public Guid? TargetBindingId { get; init; }
    public RecoveryVerificationSummary? Verification { get; init; }

    [JsonPropertyName("agent_resolution")]
    public object? AgentResolution { get; init; }
}
