#nullable enable
using System.Text.Json.Serialization;

namespace Rug.UI.Core.Models;

public sealed record AnomalyLogModel
{
    public int? SchemaVersion { get; init; }
    public Guid? IncidentId { get; init; }
    public IncidentTraceLink? TraceLink { get; init; }
    public string? ScreenshotCaptureKind { get; init; }
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

public sealed record IncidentTraceLink(string Status, Guid? HostSessionId=null, Guid? TraceId=null,
    TraceRun? Run=null, long? SequenceUpperBound=null, Guid? OperationId=null, Guid? ExecutionId=null,
    Guid? ObservationId=null, Guid? FrameId=null, DateTimeOffset? BoundaryUtc=null,
    string Persistence="NotConfirmed");
public enum IncidentReadStatus { Valid, Legacy, UnsupportedFormat, Corrupt, NotFound, CapacityExceeded, ReadFailed }
public sealed record IncidentDetail(string FileKey,IncidentReadStatus Status,AnomalyLogModel? Record,
    string? Diagnostic=null,string? ScreenshotReference=null);
public sealed record IncidentEntry(string FileKey,IncidentReadStatus Status,Guid? IncidentId,string? PluginId,
    Guid? InstanceId,DateTime? Timestamp,string? Reason,bool HasTrace);
public sealed record IncidentPage(IReadOnlyList<IncidentEntry> Entries,int? NextOffset,bool ScanLimited);
