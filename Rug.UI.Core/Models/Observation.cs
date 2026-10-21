#nullable enable
namespace Rug.UI.Core.Models;

/// <summary>Host identity without native handles. Never constructed from script/request parameters.</summary>
public sealed record ObservationRun(string PluginId, Guid InstanceId, long RunGeneration, Guid? TargetBindingId)
{
    internal static ObservationRun From(RunIdentity run) => new(run.PluginId, run.InstanceId, run.RunGeneration, run.TargetBindingId);
}
public sealed record ObservationImage(int Width, int Height, int Stride, long ByteCount,
    string PixelFormat = "BGRA8", string CoordinateSpace = "capture-image-pixels");
/// <summary>Host times retain their TimeProvider domain. NativeTime is separately tagged Windows QPC.</summary>
public sealed record ObservationTime(Guid ClockId, long TimestampFrequency, string TimeSource,
    DateTimeOffset RequestedUtc, DateTimeOffset ReceivedUtc, DateTimeOffset PublishedUtc,
    long RequestedTimestamp, long ReceivedTimestamp, long PublishedTimestamp,
    DateTimeOffset? NativeAcquiredUtc = null, long? NativeAcquiredTimestamp = null, NativeFrameTime? NativeTime = null);
/// <summary>Legacy recognition coordinates are preserved, but image == client geometry has NOT been verified.</summary>
public sealed record ObservationGeometry(string ImageToClientRelation = "unknown; legacy identity assumption",
    int? ClientWidth = null, int? ClientHeight = null, bool ClientGeometryVerified = false,
    PixelTransform? ImageToClient = null, PixelTransform? ClientToScreen = null);
public sealed record ObservationProcessing(string Kind, Rect Roi, int InputWidth, int InputHeight, int InputStride,
    int OffsetX, int OffsetY, long StartedTimestamp, long CompletedTimestamp,
    string ResultCoordinateSpace = "legacy-physical-client-pixels", double ScaleX = 1, double ScaleY = 1,
    string InputCoordinateSpace = "roi-local-pixels", string ActualResultCoordinateSpace = "capture-image-pixels");
/// <summary>Each successful publication/recognition has its own observationId; only capture publication creates frameId.
/// Reference is opaque and owner/cache scoped, not a handle or an authorization grant.</summary>
public sealed record ObservationEnvelope(int SchemaVersion, Guid ObservationId, Guid FrameId, string FrameReference,
    ObservationRun Run, ObservationImage Image, ObservationTime Time, ObservationGeometry Geometry,
    ObservationProcessing? Processing = null, CaptureFacts? CaptureFacts = null);
public sealed record ObservationLimits(long MaxFrameBytes = 64L * 1024 * 1024,
    long MaxResidentBytes = 256L * 1024 * 1024, int MaxFrames = 64, int MaxOperations = 16, int MaxSessions = 256);
public sealed record ObservationUsage(long ResidentBytes, long ReservedBytes, int Frames, int Operations, int Sessions);
public sealed class ObservationCapacityException(string message) : InvalidOperationException(message);
public sealed class ObservationReferenceException() : InvalidOperationException("Observation reference is missing, expired or belongs to another run.");
