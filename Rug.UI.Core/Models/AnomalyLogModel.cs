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

    [JsonPropertyName("agent_resolution")]
    public object? AgentResolution { get; init; }
}
