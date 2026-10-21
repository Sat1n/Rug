#nullable enable
namespace Rug.UI.Core.Models;

public enum TraceEventKind
{
    Request, RepeatedRequest, Rejected, Authorized, WorkStarted, Dispatch, Observation, RunLinked,
    Validation, InputSideEffect, CallerReturned, WorkCompleted, ReleaseFailed,
    RunCreated, Lifecycle, StopRequested, CleanupFailed, CleanupCompleted, HostClosing
}
public enum TraceDataState { NotApplicable, NotCollected, Absent, Present }
// Deliberately excludes HWND, paths, config/state, pixels, OCR text and request JSON.
public sealed record TraceRun(string PluginId, Guid InstanceId, long RunGeneration, Guid? TargetBindingId);
public sealed record TraceTarget(string PluginId, Guid? InstanceId, long? RunGeneration, Guid? TargetBindingId);
public sealed record TraceOrigin(Guid ParentInstanceId, Guid CheckpointId, long Sequence, string NodeId,
    Guid? UpdateId = null, string? CompatibilityRuleId = null);
public sealed record TraceAuthorization(string Decision, IReadOnlyList<string> Required, long? Revision,
    IReadOnlyList<string>? Missing = null);
public sealed record TraceObservation(Guid ObservationId, Guid FrameId, string? PolicyId = null,
    PointInt? ClientPoint = null, IReadOnlyList<Guid>? ValidationFrames = null);
public sealed record ExecutionTraceEvent(int SchemaVersion, Guid HostSessionId, Guid TraceId, long Sequence,
    TraceRun? Run, TraceOrigin? Origin, Guid? OperationId, string? CorrelationId, Guid? ExecutionId,
    string? Caller, AutomationSource? Source, string? Operation, TraceTarget? RequestedTarget,
    TraceEventKind Kind, DateTimeOffset Utc, long MonotonicTicks, long MonotonicFrequency, string ClockDomain,
    string? Status, string? Error, string? Reason, bool? OutcomeUncertain,
    TraceAuthorization? Authorization, TraceObservation? Observation,
    TraceDataState ObservationState, TraceDataState ValidationState);
public sealed record TraceLimits(int MaxEventBytes = 8192, int MemoryEvents = 1024, int MemoryBytes = 2*1024*1024,
    int QueueEvents = 512, int QueueBytes = 1024*1024, int MaxTraces = 128,
    long MaxFileBytes = 1024*1024, long MaxDiskBytes = 64*1024*1024, int MaxFiles = 256,
    int RetentionDays = 7, int FlushMilliseconds = 250);
public sealed record TraceSummary(int SchemaVersion, Guid HostSessionId, Guid TraceId, TraceRun? Run,
    long FirstPersisted, long LastPersisted, long PersistedEvents, long DroppedEvents,
    bool RunCleanupCompleted, bool RecordingClosed, bool Incomplete, int ActualWorkOutstanding,
    long RecordedEvents, long LastRecorded, bool? CleanupSucceeded);
public sealed record TraceDiagnostics(long DroppedEvents, long WriteFailures, string? LastWriterError,
    int QueuedEvents, int QueuedBytes, int RetainedEvents, int RetainedBytes, int Traces,
    bool Closing, bool FlushIncomplete, bool WriterBusy);
public enum TraceReadStatus { Valid, Incomplete, Corrupt, UnsupportedFormat, NotFound, CapacityExceeded }
public sealed record TracePage(TraceReadStatus Status, IReadOnlyList<ExecutionTraceEvent> Events,
    long? NextSequence, TraceSummary? Summary, bool FromDisk, string? Diagnostic = null,long? SessionDroppedEvents=null);
public sealed record TraceCatalogEntry(Guid HostSessionId,Guid TraceId,TraceRun? Run,TraceReadStatus Status,TraceSummary? Summary);
public sealed record TraceCatalogPage(IReadOnlyList<TraceCatalogEntry> Entries,int? NextOffset);
