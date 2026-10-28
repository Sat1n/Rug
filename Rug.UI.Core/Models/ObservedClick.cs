#nullable enable
using System.Text.Json;
namespace Rug.UI.Core.Models;

public enum ObservedClickReason { InvalidReference, Stale, TimeUnavailable, GeometryChanged, WindowInvalid,
    SceneChanged, ValidationUnavailable, CoordinateInvalid, PolicyUnavailable }
public sealed record ObservedClickRequest(string FrameReference, Guid ObservationId, Guid FrameId,
    long RunGeneration, double X, double Y, string CoordinateSpace, MouseButton Button,
    InputDeliveryMode Mode, string PolicyId, int? MaxAgeMs = null)
{
    internal static ObservedClickRequest Parse(JsonElement json)
    {
        string[] fields = ["frameReference","observationId","frameId","runGeneration","x","y","coordinateSpace","button","mode","policyId","maxAgeMs"];
        var names = json.EnumerateObject().Select(p=>p.Name).ToArray();
        if (names.Length != names.Distinct().Count() || names.Any(n=>!fields.Contains(n)) || json.GetRawText().Length > 2048) throw new ArgumentException("Observed click shape invalid.");
        var r = new ObservedClickRequest(json.GetProperty("frameReference").GetString()!, json.GetProperty("observationId").GetGuid(),
            json.GetProperty("frameId").GetGuid(), json.GetProperty("runGeneration").GetInt64(), json.GetProperty("x").GetDouble(),
            json.GetProperty("y").GetDouble(), json.GetProperty("coordinateSpace").GetString()!,
            (MouseButton)json.GetProperty("button").GetInt32(), (InputDeliveryMode)json.GetProperty("mode").GetInt32(),
            json.GetProperty("policyId").GetString()!, json.TryGetProperty("maxAgeMs",out var age) ? age.GetInt32() : null);
        if (r.FrameReference is not {Length: >0 and <=128} || r.PolicyId is not {Length: >0 and <=64} ||
            r.ObservationId==Guid.Empty || r.FrameId==Guid.Empty || r.RunGeneration<=0 || !double.IsFinite(r.X) || !double.IsFinite(r.Y) ||
            !Enum.IsDefined(r.Button) || !Enum.IsDefined(r.Mode) || r.MaxAgeMs is <1 or >2000 ||
            r.CoordinateSpace is not ("capture-image-pixels" or "roi-local-pixels" or "client-physical-pixels")) throw new ArgumentException("Observed click parameters invalid.");
        return r;
    }
}
public sealed record ObservedClickPolicy(string Id = "roi-consistency-v1", int MaxAgeMs = 1000, int Radius = 8,
    int MaxChannelDifference = 8, double MaxMeanDifference = 2, int VerificationTimeoutMs = 750);
public sealed record ObservedValidationFrame(Guid FrameId, NativeFrameTime NativeTime, string Stage);
public sealed record ObservedClickResult(string Status, string Reason, Guid? ObservationId = null, Guid? FrameId = null,
    PointInt? ClientPoint = null, string? PolicyId = null, IReadOnlyList<ObservedValidationFrame>? ValidationFrames = null,
    bool OutcomeUncertain = false);
public sealed class ObservedClickException(ObservedClickReason reason, ObservedClickResult? result = null) : Exception(reason.ToString())
{
    public ObservedClickReason Reason { get; } = reason;
    public ObservedClickResult Result { get; } = result ?? new("rejected", reason.ToString());
}
public sealed record ObservationActionUsage(int RegisteredObservations, int ActiveInputs, long InputRevision);
