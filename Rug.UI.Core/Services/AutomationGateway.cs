#nullable enable
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Exceptions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

public sealed class ManifestAutomationPermissionChecker : IAutomationPermissionChecker
{
    public static string? RequiredPermission(AutomationOperation operation) => operation switch
    {
        AutomationOperation.Sleep => Permission.Timer,
        AutomationOperation.Capture => Permission.Capture,
        AutomationOperation.Ocr => Permission.VisionOcr,
        AutomationOperation.FindImage => Permission.VisionMatch,
        AutomationOperation.Click or AutomationOperation.PressKey => Permission.ControlInput,
        _ => null
    };
    public bool IsAllowed(AutomationCallContext caller, IReadOnlyList<string> permissions,
        AutomationOperation operation) => RequiredPermission(operation) is not { } required ||
        (caller.Permissions.Contains(required) && permissions.Contains(required));
}

/// <summary>Host capability validation, bounded deduplication and cancellation for all plugin operations.</summary>
public sealed class AutomationGateway : IAutomationGateway
{
    private sealed record Run(RunIdentity Identity, PluginInstallation Installation, string[] Permissions, CancellationToken Token,
        Func<AutomationOperation, JsonElement, CancellationToken, Task<object?>> Execute, TaskExecutionContext? Lifecycle, bool Preparing = false);
    private sealed class Entry(string fingerprint)
    {
        public string Fingerprint { get; } = fingerprint;
        public DateTimeOffset CompletedAt { get; set; }
        public bool WorkInFlight { get; set; }
        public TaskCompletionSource<OperationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly record struct Key(string Principal, AutomationSource Source, PluginInstallation Installation, string Plugin,
        Guid? BoundInstance, Guid? Instance, long? Generation, Guid? Binding, Guid OperationId);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Run> _runs = [];
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly Queue<OperationRecord> _records = [];
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly IAutomationPermissionChecker _permissions;
    private readonly Func<string, CancellationToken, Task<PluginManifest>>? _getPlugin;
    private readonly Func<string, nint, IReadOnlyDictionary<string, object?>, Action<RunIdentity>?, CancellationToken, Task<RunIdentity>>? _start;
    private readonly Func<Guid, Task>? _stop;
    private readonly Func<Guid, bool>? _pause, _resume;
    private readonly Func<Guid, Guid, DateTimeOffset, CancellationToken, Task<RecoveryResult>>? _verifyRecovery;
    internal Func<AutomationCallContext, Guid, DateTimeOffset, CancellationToken, CancellationToken, CheckpointStartBoundary, Task<RunIdentity>>? RestoreCheckpoint { get; set; }
    internal Func<AutomationCallContext, OperationRequest, CancellationToken, CancellationToken, Task<object?>>? RuntimeUpdateControl { get; set; }
    private static bool UpdateControl(AutomationOperation op) => op is AutomationOperation.PrepareRuntimeUpdate or AutomationOperation.QueryRuntimeUpdate or AutomationOperation.CancelRuntimeUpdate;
    private int _activeWork;
    private readonly Func<Task>? _waitForDispatch;
    public const int Capacity = 4096;
    public const int ActiveCapacity = 256;
    public const int RecordCapacity = 256;
    public static TimeSpan Retention => TimeSpan.FromMinutes(2);

    public AutomationGateway(ILogger logger, IAutomationPermissionChecker? permissions = null,
        Func<string, CancellationToken, Task<PluginManifest>>? getPlugin = null,
        Func<string, nint, IReadOnlyDictionary<string, object?>, Action<RunIdentity>?, CancellationToken, Task<RunIdentity>>? start = null,
        Func<Guid, Task>? stop = null, Func<Guid, bool>? pause = null, Func<Guid, bool>? resume = null,
        TimeProvider? clock = null, IPluginAuthorization? authorization = null, Func<Task>? waitForDispatch = null, Func<Guid, Guid, DateTimeOffset, CancellationToken, Task<RecoveryResult>>? verifyRecovery = null)
    {
        _logger = logger; _permissions = permissions ?? new ManifestAutomationPermissionChecker();
        _getPlugin = getPlugin; _start = start; _stop = stop; _pause = pause; _resume = resume;
        _clock = clock ?? TimeProvider.System;
        Authorization = authorization ?? new PluginAuthorization();
        _waitForDispatch = waitForDispatch; _verifyRecovery = verifyRecovery;
    }
    public IPluginAuthorization Authorization { get; }
    public int RetainedOperationCount { get { lock (_gate) { Prune(); return _entries.Count; } } }
    public int RegisteredRunCount { get { lock (_gate) return _runs.Count; } }
    public int ActiveWorkCount { get { lock (_gate) return _activeWork; } }
    public IReadOnlyList<OperationRecord> Records { get { lock (_gate) return _records.ToArray(); } }

    /// <summary>Trusted composition-root API, never exposed as an external adapter request.
    /// @shape pluginId: verified catalog ID; run: optional complete host identity.
    /// @source plugin: Services/TaskScheduler.cs#function:GetPluginAsync</summary>
    public async Task<AutomationCallContext> CreateContextAsync(string pluginId, string principal,
        AutomationSource source, RunIdentity? boundRun = null, nint startHwnd = 0,
        Action<RunIdentity>? onReady = null, CancellationToken cancellationToken = default,
        IEnumerable<string>? capabilities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        if (_getPlugin is null) throw new InvalidOperationException("No plugin catalog is bound.");
        PluginManifest manifest = await _getPlugin(pluginId, cancellationToken).ConfigureAwait(false);
        if (boundRun is not null && !SamePlugin(manifest.Id, boundRun.PluginId))
            throw new ArgumentException("Caller run does not belong to this plugin.");
        return BindContext(manifest, principal, source, boundRun, startHwnd, onReady, capabilities);
    }
    internal AutomationCallContext BindContext(PluginManifest manifest, string principal, AutomationSource source,
        RunIdentity? run = null, nint hwnd = 0, Action<RunIdentity>? onReady = null, IEnumerable<string>? capabilities = null) =>
        new(this, principal, source, manifest.Id, manifest.Permissions.ToArray(), Authorization.Bind(manifest, capabilities), run, hwnd, onReady);
    internal void RegisterRun(RunIdentity identity, PluginManifest manifest, CancellationToken token,
        Func<AutomationOperation, JsonElement, CancellationToken, Task<object?>> execute, TaskExecutionContext? lifecycle = null)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            var run = new Run(identity, PluginInstallation.From(manifest), manifest.Permissions.ToArray(), token, execute, lifecycle);
            if (_runs.TryGetValue(identity.InstanceId, out var preparing) && preparing.Preparing &&
                preparing.Identity == identity && ReferenceEquals(preparing.Lifecycle, lifecycle)) _runs[identity.InstanceId] = run with { Preparing = true };
            else _runs.Add(identity.InstanceId, run);
        }
    }
    internal void RegisterStartingRun(TaskExecutionContext context, PluginManifest manifest)
    {
        lock (_gate)
        {
            context.Cancellation.Token.ThrowIfCancellationRequested();
            if (context.TaskStatus == AutomationTaskStatus.Stopping) throw new OperationCanceledException();
            _runs.Add(context.InstanceId, new(context.Identity, PluginInstallation.From(manifest), manifest.Permissions.ToArray(),
                context.Cancellation.Token, (_, _, _) => Task.FromException<object?>(new GatewayRejectedException(OperationError.UnsupportedOperation)),
                context, Preparing: true));
        }
    }
    internal void CompleteStartingRun(Guid instanceId)
    {
        lock (_gate) if (_runs.TryGetValue(instanceId, out var run) && run.Preparing)
            _runs[instanceId] = run with { Preparing = false };
    }
    internal void EndRun(Guid instanceId)
    {
        lock (_gate)
        {
            _runs.Remove(instanceId);
            foreach (var key in _entries.Where(pair => pair.Key.Instance == instanceId &&
                pair.Value.Completion.Task.IsCompleted).Select(pair => pair.Key).ToArray()) _entries.Remove(key);
        }
    }
    private static bool SamePlugin(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private IEnumerable<string> Required(AutomationCallContext caller, OperationRequest request)
    {
        if (request.Operation == AutomationOperation.PrepareRuntimeUpdate)
            return Authorization.Query(caller.Authorization.Installation).Requested.Concat([Permission.Checkpoint]).Distinct(StringComparer.Ordinal);
        if (request.Operation == AutomationOperation.RestoreCheckpoint)
            return Authorization.Query(caller.Authorization.Installation).Requested.Concat([Permission.Checkpoint, Permission.Capture]).Distinct(StringComparer.Ordinal);
        if (request.Operation is AutomationOperation.Start or AutomationOperation.Resume or AutomationOperation.VerifyRecovery or AutomationOperation.RestoreCheckpoint)
            return Authorization.Query(caller.Authorization.Installation).Requested;
        if (ManifestAutomationPermissionChecker.RequiredPermission(request.Operation) is not { } required) return [];
        if (request.Operation == AutomationOperation.FindImage && request.Parameters.TryGetProperty("path", out var path) &&
            path.ValueKind == JsonValueKind.String)
        {
            try
            {
                string root = caller.Authorization.Installation.Directory;
                string full = Path.GetFullPath(Path.Combine(root, path.GetString()!));
                if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return [required, Permission.FileSystem];
            }
            catch (ArgumentException) { /* Static parameter/bridge validation reports malformed paths. */ }
        }
        return [required];
    }
    private IReadOnlyList<string> Missing(AutomationCallContext caller, OperationRequest request, Run? run)
    {
        var required = Required(caller, request).ToArray();
        return Authorization.Missing(caller.Authorization, required).Concat(
            required.Where(p => run is not null && !run.Permissions.Contains(p))).Concat(
            _permissions.IsAllowed(caller, run?.Permissions ?? caller.Permissions, request.Operation) ? [] : required)
            .Distinct(StringComparer.Ordinal).ToArray();
    }
    /// <summary>@shape caller: host capability; request: untrusted JSON/target; run: registered executor or null.
    /// @source identity: Models/RunIdentity.cs#class:RunIdentity</summary>
    private OperationError Validate(AutomationCallContext caller, OperationRequest request, out Run? run, out string[] missing)
    {
        run = null; missing = [];
        if (!ReferenceEquals(caller.Owner, this) || !SamePlugin(caller.PluginId, request.Target.PluginId))
            return OperationError.InvalidCaller;
        if (!Enum.IsDefined(request.Operation)) return OperationError.UnsupportedOperation;
        if (request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.CorrelationId) ||
            request.CorrelationId.Length > 128 || request.Parameters.ValueKind != JsonValueKind.Object ||
            System.Text.Encoding.UTF8.GetByteCount(request.Parameters.GetRawText()) > StateStore.MaxValueBytes)
            return OperationError.InvalidParameters;
        if (UpdateControl(request.Operation))
        {
            if (!caller.RuntimeUpdateHost || caller.Source != AutomationSource.PluginUI || caller.BoundRun is not null ||
                request.Target.InstanceId is not null || request.Target.RunGeneration is not null || request.Target.TargetBindingId is not null)
                return OperationError.InvalidCaller;
            if (RuntimeUpdateControl is null) return OperationError.UnsupportedOperation;
        }
        else if (request.Operation is AutomationOperation.Start or AutomationOperation.RestoreCheckpoint)
        {
            if (caller.Source == AutomationSource.Lua || caller.BoundRun is not null ||
                request.Target.InstanceId is not null || request.Target.RunGeneration is not null ||
                request.Target.TargetBindingId is not null) return OperationError.InvalidCaller;
            if (_start is null) return OperationError.UnsupportedOperation;
            if (request.Operation == AutomationOperation.RestoreCheckpoint &&
                (caller.Source != AutomationSource.PluginUI || caller.RestoreTarget is null || RestoreCheckpoint is null)) return OperationError.InvalidCaller;
        }
        else
        {
            if (request.Target.InstanceId is not { } id || !_runs.TryGetValue(id, out run))
                return OperationError.InstanceNotFound;
            if (caller.Authorization.Installation != run.Installation) return OperationError.InvalidCaller;
            if (!SamePlugin(run.Identity.PluginId, request.Target.PluginId)) return OperationError.InvalidCaller;
            if (request.Target.RunGeneration != run.Identity.RunGeneration) return OperationError.StaleRun;
            if (request.Target.TargetBindingId != run.Identity.TargetBindingId) return OperationError.BindingMismatch;
            if (caller.BoundRun is { } bound && bound != run.Identity) return OperationError.InvalidCaller;
            if (caller.Source == AutomationSource.Lua && request.Operation is AutomationOperation.Stop or AutomationOperation.Pause or AutomationOperation.Resume)
                return OperationError.UnsupportedOperation;
            if (request.Operation == AutomationOperation.VerifyRecovery && caller.Source != AutomationSource.PluginUI) return OperationError.InvalidCaller;
            if (request.Operation is not (AutomationOperation.Stop or AutomationOperation.VerifyRecovery) && run.Lifecycle?.TaskStatus is AutomationTaskStatus.Recovering or AutomationTaskStatus.AwaitingHuman) return OperationError.InvalidLifecycleState;
            // Stop remains available for the shared cleanup. Every other request
            // rejects terminal phases even before cancellation callbacks propagate.
            if (request.Operation != AutomationOperation.Stop && run.Lifecycle?.IsBusinessClosed == true)
                return OperationError.InvalidLifecycleState;
            if (request.Operation != AutomationOperation.Stop && run.Token.IsCancellationRequested) return OperationError.InstanceNotFound;
        }
        missing = Missing(caller, request, run).ToArray();
        if (missing.Length > 0) return OperationError.PermissionDenied;
        if (!_permissions.IsAllowed(caller, run?.Permissions ?? caller.Permissions, request.Operation))
            return OperationError.PermissionDenied;
        return OperationError.None;
    }

    /// <summary>@shape request: immutable JSON object and structural target; result: sanitized terminal outcome.
    /// @source caller: AutomationGateway.CreateContextAsync</summary>
    public Task<OperationResult> ExecuteAsync(AutomationCallContext caller, OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller); ArgumentNullException.ThrowIfNull(request);
        if (request.Target is null) return Task.FromResult(new OperationResult(OperationStatus.Rejected, OperationError.InvalidParameters));
        Entry entry; Run? run; Key key;
        lock (_gate)
        {
            Prune();
            OperationError error = Validate(caller, request, out run, out var missing);
            if (error != OperationError.None) return FinishImmediate(caller, request, OperationStatus.Rejected, error, missing);
            key = new(caller.Principal, caller.Source, caller.Authorization.Installation, caller.PluginId.ToUpperInvariant(), caller.BoundRun?.InstanceId,
                request.Target.InstanceId, request.Target.RunGeneration, request.Target.TargetBindingId, request.OperationId);
            string fingerprint = request.Operation + ":" + request.Parameters.GetRawText();
            if (request.Operation == AutomationOperation.RestoreCheckpoint)
                fingerprint += ":" + caller.RestoreTarget!.Hwnd + ":" + caller.RestoreTarget.WindowStamp;
            if (_entries.TryGetValue(key, out entry!))
                return entry.Fingerprint == fingerprint ? entry.Completion.Task :
                    FinishImmediate(caller, request, OperationStatus.Rejected, OperationError.DeduplicationConflict);
            if (!OperationParameterValidator.IsValid(request.Operation, request.Parameters))
                return FinishImmediate(caller, request, OperationStatus.Rejected, OperationError.InvalidParameters);
            if (request.Deadline <= _clock.GetUtcNow())
                return FinishImmediate(caller, request, OperationStatus.TimedOut, OperationError.DeadlineExceeded);
            if (cancellationToken.IsCancellationRequested)
                return FinishImmediate(caller, request, OperationStatus.Cancelled, OperationError.Cancelled);
            if (_entries.Count >= Capacity || _activeWork >= ActiveCapacity)
                return FinishImmediate(caller, request, OperationStatus.Rejected, OperationError.CapacityExceeded);
            entry = new(fingerprint); _entries.Add(key, entry);
            request = request with { Parameters = request.Parameters.Clone() };
        }
        _ = ExecuteCoreAsync(caller, request, run, key, entry, cancellationToken);
        return entry.Completion.Task;
    }
    private Task<OperationResult> FinishImmediate(AutomationCallContext caller, OperationRequest request,
        OperationStatus status, OperationError error, string[]? missing = null)
    {
        missing ??= [];
        var result = new OperationResult(status, error, DeniedPermission: missing.FirstOrDefault(),
            MissingPermissions: Array.AsReadOnly(missing)); Record(caller, request, result); return Task.FromResult(result);
    }
    /// <summary>@shape entry: one deduplication completion; work: cancellable or noncooperative service Task.
    /// @source request: AutomationGateway.ExecuteAsync</summary>
    private async Task ExecuteCoreAsync(AutomationCallContext caller, OperationRequest request, Run? run,
        Key key, Entry entry, CancellationToken callerToken)
    {
        OperationResult result;
        using var deadline = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token,
            request.Operation == AutomationOperation.Stop ? CancellationToken.None : run?.Token ?? CancellationToken.None);
        int started = 0;
        var restoreBoundary = new CheckpointStartBoundary();
        CancellationToken executionToken = linked.Token;
        try
        {
            TimeSpan remaining = request.Deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) { deadline.Cancel(); linked.Token.ThrowIfCancellationRequested(); }
            // Bound deadlines also protect timer implementation limits.
            if (remaining > TimeSpan.FromHours(24)) throw new ArgumentException("Deadline is too distant.");
            deadline.CancelAfter(remaining);
            linked.Token.ThrowIfCancellationRequested();
            Task<object?> work;
            lock (_gate)
            {
                OperationError error = Validate(caller, request, out _, out var missing);
                if (error != OperationError.None) throw new GatewayRejectedException(error, missing);
                linked.Token.ThrowIfCancellationRequested();
                if (request.Deadline <= _clock.GetUtcNow())
                {
                    deadline.Cancel();
                    linked.Token.ThrowIfCancellationRequested();
                }
                if (_activeWork >= ActiveCapacity) throw new GatewayRejectedException(OperationError.CapacityExceeded);
                bool business = !UpdateControl(request.Operation) && request.Operation is not (AutomationOperation.Start or AutomationOperation.Stop or AutomationOperation.Pause or AutomationOperation.Resume or AutomationOperation.VerifyRecovery or AutomationOperation.RestoreCheckpoint);
                if (business && run?.Preparing == true) throw new GatewayRejectedException(OperationError.InvalidLifecycleState);
                var lifetime = business ? run?.Lifecycle?.EnterOperation(
                    request.Operation is AutomationOperation.Click or AutomationOperation.PressKey) : null;
                // Admission is atomic with EndRun; service code never runs while holding the registry gate.
                work = Task.Run(async () =>
                {
                    using var actualLifetime = lifetime;
                    executionToken.ThrowIfCancellationRequested();
                    try { return await AuthorizedDispatchAsync().ConfigureAwait(false); }
                    catch (InputCleanupException cleanup)
                    {
                        // Record before releasing the actual-work permit: pause/stop
                        // must not publish success ahead of failed mandatory releases.
                        run?.Lifecycle?.RecordFailure(cleanup);
                        throw;
                    }
                    async Task<object?> AuthorizedDispatchAsync()
                    {
                        if (_waitForDispatch is not null) await _waitForDispatch().ConfigureAwait(false);
                        executionToken.ThrowIfCancellationRequested();
                        lock (_gate)
                        {
                            OperationError error = Validate(caller, request, out _, out var missing);
                            if (error != OperationError.None) throw new GatewayRejectedException(error, missing);
                        }
                        // Acquisition is the actual admission boundary; no handler runs under the authorization lock.
                        using var admission = Authorization.Admit(caller.Authorization, Required(caller, request));
                        using var dispatchToken = CancellationTokenSource.CreateLinkedTokenSource(executionToken, admission.Token);
                        dispatchToken.Token.ThrowIfCancellationRequested();
                        if (business) run?.Lifecycle?.ValidateBusiness();
                        bool inputOperation = request.Operation is AutomationOperation.Click or AutomationOperation.PressKey;
                        if (!inputOperation && request.Operation != AutomationOperation.RestoreCheckpoint) Volatile.Write(ref started, 1);
                        var previousBoundary = InputExecutionBoundary.Started.Value;
                        var previousCaller = InputExecutionBoundary.Caller.Value;
                        InputExecutionBoundary.Started.Value = () => Volatile.Write(ref started, 1);
                        InputExecutionBoundary.Caller.Value = caller;
                        object? value;
                        try { value = await DispatchAsync(caller, request, run, dispatchToken.Token, callerToken, restoreBoundary).ConfigureAwait(false); }
                        finally { InputExecutionBoundary.Started.Value = previousBoundary; InputExecutionBoundary.Caller.Value = previousCaller; }
                        if (restoreBoundary.Identity is null) dispatchToken.Token.ThrowIfCancellationRequested();
                        return value;
                    }
                });
                entry.WorkInFlight = true;
                _activeWork++;
            }
            // Always observe failures even when a noncooperative service outlives cancellation.
            _ = work.ContinueWith(task =>
            {
                _ = task.Exception;
                if (task.IsFaulted && entry.Completion.Task.IsCompleted)
                    _logger.LogError(task.Exception, "Late automation work failed operation={OperationId} type={Type}",
                        request.OperationId, request.Operation);
                lock (_gate) { entry.WorkInFlight = false; _activeWork--; }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
            object? value = await work.WaitAsync(linked.Token).ConfigureAwait(false);
            if (restoreBoundary.Identity is null)
            {
                if (request.Deadline <= _clock.GetUtcNow()) deadline.Cancel();
                linked.Token.ThrowIfCancellationRequested();
            }
            result = new(OperationStatus.Success, Value: value);
        }
        catch (OperationCanceledException)
        {
            bool timedOut = deadline.IsCancellationRequested && !callerToken.IsCancellationRequested &&
                (request.Operation == AutomationOperation.Stop || !(run?.Token.IsCancellationRequested ?? false));
            result = new(timedOut ? OperationStatus.TimedOut : OperationStatus.Cancelled,
                timedOut ? OperationError.DeadlineExceeded : OperationError.Cancelled, OutcomeUncertain: Volatile.Read(ref started) != 0);
            if (restoreBoundary.Identity is { } committed) result = new(OperationStatus.Success, Value: committed);
        }
        catch (CheckpointStartTimeoutException) { result = new(OperationStatus.TimedOut, OperationError.DeadlineExceeded); }
        catch (GatewayRejectedException error) { result = new(OperationStatus.Rejected, error.Error,
            DeniedPermission: error.Missing.FirstOrDefault(),
            MissingPermissions: error.Missing); }
        catch (PermissionDeniedException error) { result = new(OperationStatus.Rejected, OperationError.PermissionDenied, DeniedPermission: error.Permission, MissingPermissions: error.MissingPermissions); }
        catch (InputUnavailableException) { result = new(OperationStatus.Rejected, OperationError.InputUnavailable); }
        catch (KeyNotFoundException) { result = new(OperationStatus.Rejected, OperationError.InvalidParameters); }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidCastException or FormatException or OverflowException)
        { result = new(OperationStatus.Rejected, OperationError.InvalidParameters); }
        catch (Exception error)
        {
            // Preserve the root cause in trusted host diagnostics, never in adapter results.
            _logger.LogError(error, "Automation execution failed operation={OperationId} correlation={CorrelationId} source={Source} target={Target} type={Type}",
                request.OperationId, request.CorrelationId, caller.Source, request.Target, request.Operation);
            result = new(OperationStatus.Failed, OperationError.ExecutionFailed, OutcomeUncertain: Volatile.Read(ref started) != 0);
        }
        if (request.Operation == AutomationOperation.VerifyRecovery && result.Status != OperationStatus.Success &&
            run?.Lifecycle?.Snapshot.Recovery is { Phase: RecoveryPhase.Failed, Result.Status: RecoveryStatus.Failed } failedRecovery)
            result = new(OperationStatus.Failed, OperationError.ExecutionFailed, Value: failedRecovery.Result);
        // Rejection describes why the action ended, not whether earlier input had effects.
        // Keep every input failure mapping aligned with the same execution boundary.
        if (request.Operation is AutomationOperation.Click or AutomationOperation.PressKey && result.Status != OperationStatus.Success)
            result = result with { OutcomeUncertain = Volatile.Read(ref started) != 0 };
        lock (_gate)
        {
            Record(caller, request, result);
            entry.CompletedAt = _clock.GetUtcNow();
            entry.Completion.TrySetResult(result);
            if (key.Instance is { } id && !_runs.ContainsKey(id)) _entries.Remove(key);
        }
    }
    private async Task<object?> DispatchAsync(AutomationCallContext caller, OperationRequest request, Run? run, CancellationToken token,
        CancellationToken original, CheckpointStartBoundary restoreBoundary)
    {
        if (UpdateControl(request.Operation)) return await RuntimeUpdateControl!(caller, request, token, original).ConfigureAwait(false);
        switch (request.Operation)
        {
            case AutomationOperation.RestoreCheckpoint:
                return await RestoreCheckpoint!(caller, request.Parameters.GetProperty("selectionId").GetGuid(), request.Deadline,
                    token, original, restoreBoundary).ConfigureAwait(false);
            case AutomationOperation.Start:
                var inputs = request.Parameters.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                return await _start!(caller.PluginId, caller.StartHwnd, inputs, caller.OnReady, token).ConfigureAwait(false);
            case AutomationOperation.VerifyRecovery:
                if (_verifyRecovery is null) throw new GatewayRejectedException(OperationError.UnsupportedOperation);
                return await _verifyRecovery(run!.Identity.InstanceId, request.Parameters.GetProperty("recoveryId").GetGuid(), request.Deadline, token).ConfigureAwait(false);
            case AutomationOperation.Stop:
                if (_stop is null) throw new GatewayRejectedException(OperationError.UnsupportedOperation);
                await _stop(run!.Identity.InstanceId).ConfigureAwait(false);
                // Keep the captured host completion authoritative even if cleanup
                // already removed the instance from the scheduler's live picker.
                if (run.Lifecycle is { } lifecycle) await lifecycle.Completion.ConfigureAwait(false);
                return true;
            case AutomationOperation.Pause:
                if (!(_pause ?? throw new GatewayRejectedException(OperationError.UnsupportedOperation))(run!.Identity.InstanceId))
                    throw new GatewayRejectedException(OperationError.InvalidLifecycleState);
                return true; // Request accepted, not a promise of confirmed Paused.
            case AutomationOperation.Resume:
                if (!(_resume ?? throw new GatewayRejectedException(OperationError.UnsupportedOperation))(run!.Identity.InstanceId))
                    throw new GatewayRejectedException(OperationError.InvalidLifecycleState);
                return true;
            default: return await run!.Execute(request.Operation, request.Parameters, token).ConfigureAwait(false);
        }
    }
    private void Prune()
    {
        DateTimeOffset cutoff = _clock.GetUtcNow() - Retention;
        foreach (var key in _entries.Where(p => !p.Value.WorkInFlight && p.Value.Completion.Task.IsCompleted && p.Value.CompletedAt < cutoff)
            .Select(p => p.Key).ToArray()) _entries.Remove(key);
    }
    private void Record(AutomationCallContext caller, OperationRequest request, OperationResult result)
    {
        OperationTarget target = result.Value is RunIdentity created ? OperationTarget.ForRun(created) : request.Target;
        lock (_gate)
        {
            if (_records.Count == RecordCapacity) _records.Dequeue();
            _records.Enqueue(new(request.OperationId, request.CorrelationId, caller.Principal, caller.Source,
                target,
                request.Operation, result.Status, result.Error, result.OutcomeUncertain));
        }
        _logger.LogInformation("Automation operation={OperationId} correlation={CorrelationId} source={Source} target={Target} type={Type} result={Status} code={Code} uncertain={Uncertain}",
            request.OperationId, request.CorrelationId, caller.Source, target, request.Operation, result.Status, result.Error, result.OutcomeUncertain);
        if (result.Error == OperationError.PermissionDenied)
            _logger.LogWarning("Permission denied: plugin={PluginId}, operation={OperationId}, type={Type}", caller.PluginId, request.OperationId, request.Operation);
    }
    internal sealed class GatewayRejectedException(OperationError error, string[]? missing = null) : Exception
    {
        public OperationError Error { get; } = error;
        public IReadOnlyList<string> Missing { get; } = Array.AsReadOnly(missing ?? []);
    }
}
