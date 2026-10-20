#nullable enable
using System.Text.Json;
using Rug.UI.Core.Services;

namespace Rug.UI.Core.Models;

public enum AutomationOperation { Start, Stop, Pause, Resume, Sleep, Capture, Ocr, FindImage, Click, PressKey, VerifyRecovery, RestoreCheckpoint, PrepareRuntimeUpdate, QueryRuntimeUpdate, CancelRuntimeUpdate }
public enum OperationStatus { Success, Rejected, Cancelled, TimedOut, Failed }
public enum OperationError { None, InvalidParameters, PermissionDenied, InstanceNotFound, StaleRun,
    BindingMismatch, UnsupportedOperation, DeduplicationConflict, DeadlineExceeded, Cancelled,
    ExecutionFailed, CapacityExceeded, InvalidCaller, InputUnavailable, InvalidLifecycleState,
    CheckpointChanged, CheckpointIncompatible, RestoreUnavailable, SourceStillActive, Busy, SceneRejected,
    NoPendingUpdate, UpdateUnavailable, UpdatePreparing, UpdateSuperseded }
public enum AutomationSource { PluginUI, Lua, Agent, Mcp }
public sealed record CapturedFrameMetadata(int Width, int Height, int Stride);

/// <summary>Untrusted operation data. Target carries no HWND, principal or permission grants.</summary>
public sealed record OperationTarget(string PluginId, Guid? InstanceId = null,
    long? RunGeneration = null, Guid? TargetBindingId = null)
{
    public static OperationTarget ForRun(RunIdentity run) =>
        new(run.PluginId, run.InstanceId, run.RunGeneration, run.TargetBindingId);
}
public sealed record OperationRequest(AutomationOperation Operation, JsonElement Parameters,
    Guid OperationId, string CorrelationId, OperationTarget Target, DateTimeOffset Deadline);
public sealed record OperationResult(OperationStatus Status, OperationError Error = OperationError.None,
    object? Value = null, bool OutcomeUncertain = false, string? DeniedPermission = null, IReadOnlyList<string>? MissingPermissions = null)
{
    public object? RequireSuccess()
    {
        if (Status == OperationStatus.Success) return Value;
        if (Status == OperationStatus.Cancelled) throw new OperationCanceledException();
        if (Status == OperationStatus.TimedOut) throw new TimeoutException(Error.ToString());
        throw new InvalidOperationException(Error.ToString());
    }
}

/// <summary>Host-created capability; adapters cannot deserialize or construct a caller context.</summary>
public sealed class AutomationCallContext
{
    internal AutomationCallContext(AutomationGateway owner, string principal, AutomationSource source,
        string pluginId, string[] permissions, AuthorizationTicket authorization, RunIdentity? run, nint startHwnd,
        Action<RunIdentity>? onReady)
    {
        Authorization = authorization; Owner = owner; Principal = principal; Source = source; PluginId = pluginId;
        Permissions = Array.AsReadOnly(permissions); BoundRun = run; StartHwnd = startHwnd; OnReady = onReady;
    }
    internal AutomationGateway Owner { get; }
    public AuthorizationTicket Authorization { get; }
    internal nint StartHwnd { get; }
    internal Action<RunIdentity>? OnReady { get; }
    internal InputTarget? RestoreTarget { get; set; }
    internal bool RuntimeUpdateHost { get; set; }
    public string Principal { get; }
    public AutomationSource Source { get; }
    public string PluginId { get; }
    public IReadOnlyList<string> Permissions { get; }
    public RunIdentity? BoundRun { get; }
}

public sealed record OperationRecord(Guid OperationId, string CorrelationId, string Principal,
    AutomationSource Source, OperationTarget Target, AutomationOperation Operation,
    OperationStatus Status, OperationError Error, bool OutcomeUncertain);
