#nullable enable
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Trusted registration keyed by catalog installation and exact scenario.</summary>
public sealed class RecoveryValidatorRegistry : IRecoveryValidatorRegistry
{
    private readonly ConcurrentDictionary<(PluginInstallation, string), IRecoveryValidator> _validators = new();
    public IDisposable Register(PluginInstallation installation, string scenario, IRecoveryValidator validator)
    {
        var key = (installation, scenario);
        if (!_validators.TryAdd(key, validator)) throw new InvalidOperationException("Validator already registered.");
        return new Registration(() => _validators.TryRemove(new KeyValuePair<(PluginInstallation, string), IRecoveryValidator>(key, validator)));
    }
    public IRecoveryValidator? Find(PluginInstallation installation, string scenario) => _validators.GetValueOrDefault((installation, scenario));
    private sealed class Registration(Action remove) : IDisposable
    { private int _disposed; public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) remove(); } }
}

/// <summary>One logical recovery per run. Advice has no services; actual native leaves retain work permits.</summary>
public sealed class RecoveryCoordinator : IRecoveryCoordinator
{
    private sealed class Session(TaskExecutionContext context, RecoveryRequest request)
    {
        public TaskExecutionContext Context { get; } = context;
        public RecoveryRequest Request { get; } = request;
        public TaskCompletionSource<RecoveryResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly object Gate = new();
        public Task<RecoveryResult>? Verification;
        public Task? ActualVerification;
        public long ValidationGeneration;
        public string? Record;
    }
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly IAnomalyLogger _logger;
    private readonly IPluginAuthorization _authorization;
    private readonly IRecoveryValidatorRegistry _validators;
    private readonly IRecoveryExecutor? _executor;
    private readonly RecoveryOptions _options;
    private readonly ILogger<RecoveryCoordinator> _diagnostics;
    private readonly TimeProvider _clock;
    private int _adviceInFlight;
    public const int AdviceCapacity = 32;
    public int ActiveRecoveries => _sessions.Count;
    public int AdviceInFlight => Volatile.Read(ref _adviceInFlight);

    public RecoveryCoordinator(IAnomalyLogger logger, IPluginAuthorization authorization,
        IRecoveryValidatorRegistry validators, IRecoveryExecutor? executor = null, RecoveryOptions? options = null, ILogger<RecoveryCoordinator>? diagnostics = null,
        TimeProvider? clock = null)
    {
        _logger = logger; _authorization = authorization; _validators = validators; _executor = executor;
        _options = options ?? RecoveryOptions.Default; _diagnostics = diagnostics ?? NullLogger<RecoveryCoordinator>.Instance;
        _clock = clock ?? TimeProvider.System;
        if (_options.AttemptTimeout <= TimeSpan.Zero || _options.ValidationTimeout <= TimeSpan.Zero ||
            _options.AttemptTimeout > TimeSpan.FromMinutes(1) || _options.ValidationTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(options));
    }
    /// <summary>@shape context: host run; reason/detail: bounded strings; result: verified table payload only.
    /// @source context: ../Models/TaskExecutionContext.cs#class:TaskExecutionContext</summary>
    public async Task<RecoveryResult> RequestAsync(TaskExecutionContext context, string reason, string detail)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 256 || detail.Length > 2048)
            throw new ArgumentException("Recovery requires bounded strings.");
        context.ValidateBusiness();
        var ticket = context.RecoveryAuthorization ?? throw new InvalidOperationException("No host recovery identity.");
        using (var admission = _authorization.Admit(ticket, [Permission.Agent])) admission.Token.ThrowIfCancellationRequested();
        var snapshot = context.BeginRecovery(reason);
        var identity = context.Identity;
        var request = new RecoveryRequest(snapshot.RecoveryId, identity.PluginId, identity.InstanceId,
            identity.RunGeneration, identity.TargetBindingId, ticket.Installation, snapshot.Generation,
            _authorization.Query(ticket.Installation).Revision, reason, detail);
        var session = new Session(context, request);
        if (!_sessions.TryAdd(identity.InstanceId, session)) throw new InvalidOperationException("Recovery already active.");
        _ = BeginAsync(session); // All failures are delivered/observed below, never fire-and-forget exceptions.
        try
        {
            if (await Task.WhenAny(session.Result.Task, context.StopSignal).ConfigureAwait(false) != session.Result.Task)
                throw new OperationCanceledException("Host stopped recovery.");
            if (context.IsBusinessClosed) throw new OperationCanceledException("Host stopped recovery.");
            context.Cancellation.Token.ThrowIfCancellationRequested();
            return await session.Result.Task.ConfigureAwait(false);
        }
        finally { _sessions.TryRemove(new KeyValuePair<Guid, Session>(identity.InstanceId, session)); }
    }
    private async Task BeginAsync(Session session)
    {
        try
        {
            var context = session.Context;
            await context.DrainRecoveryBoundaryAsync().WaitAsync(context.Cancellation.Token).ConfigureAwait(false);
            Check(session);
            Update(session, RecoveryPhase.Archiving);
            session.Record = await ActualAsync(session, () => _logger.LogAnomalyAsync(context,
                session.Request.Scenario, session.Request.Context)).ConfigureAwait(false);
            Check(session);
            RecoveryResult advice;
            if (_executor is null) advice = Result(session, RecoveryStatus.NeedsHuman, RecoveryError.NoExecutor);
            else
            {
                Update(session, RecoveryPhase.Attempting);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation.Token);
                deadline.CancelAfter(_options.AttemptTimeout);
                CancellationToken attemptToken = deadline.Token;
                Task<RecoveryResult> attempt = AdviceAsync(() => _executor.TryRecoverAsync(session.Request, attemptToken));
                Observe(attempt);
                try { advice = await attempt.WaitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.IsBusinessClosed && !context.Cancellation.IsCancellationRequested)
                { advice = Result(session, RecoveryStatus.NeedsHuman, RecoveryError.AttemptTimedOut); }
            }
            Check(session);
            // Executor responses are suggestions, never authority or identity.
            if (!Enum.IsDefined(advice.Status) || !Enum.IsDefined(advice.Error)) throw new RecoveryFailedException(RecoveryError.ExecutorFailed);
            if (advice.Status == RecoveryStatus.Failed) throw new RecoveryFailedException(RecoveryError.ExecutorFailed);
            if (advice.Status == RecoveryStatus.Resolved)
            {
                var verified = await StartVerification(session, automatic: true).ConfigureAwait(false);
                if (verified.Status == RecoveryStatus.Resolved) return;
            }
            else await WaitHumanAsync(session, advice.Error).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Context.IsBusinessClosed || session.Context.Cancellation.IsCancellationRequested)
        { session.Result.TrySetCanceled(); }
        catch (Exception error) { Fail(session, error); }
    }
    private RecoveryResult Result(Session session, RecoveryStatus status, RecoveryError error = RecoveryError.None) =>
        new(status, session.Request.RecoveryId, session.Request.Scenario, error);
    private void Update(Session session, RecoveryPhase phase, RecoveryError error = RecoveryError.None, long validation = 0) =>
        session.Context.UpdateRecovery(session.Request.RecoveryId, phase, Result(session, RecoveryStatus.NeedsHuman, error), validation);
    private void Check(Session session)
    {
        session.Context.CheckRecovery(session.Request.RecoveryId);
        if (session.Context.LastError is { } failure) throw failure;
        session.Context.ValidateRecovery?.Invoke();
        using var admission = _authorization.Admit(session.Context.RecoveryAuthorization!, [Permission.Agent]);
        admission.Token.ThrowIfCancellationRequested();
    }
    private async Task WaitHumanAsync(Session session, RecoveryError error)
    {
        Check(session);
        // A verified image may have been archived before final commitment was cancelled.
        // Keep the record aligned with the retained coroutine, not that provisional success.
        if (session.Record is { } record && session.Context.Snapshot.Recovery is { ValidationGeneration: > 0 } snapshot)
            await ActualAsync(session, async () =>
            {
                await _logger.UpdateRecoveryAsync(record, new(session.Request.RecoveryId, snapshot.ValidationGeneration,
                    "needs_human", error.ToString(), session.Request.Scenario, DateTime.UtcNow)).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        Check(session);
        Update(session, RecoveryPhase.HandingOff, error);
        if (session.Context.HandoffRecovery is { } handoff) await handoff().ConfigureAwait(false);
        Check(session);
        Update(session, RecoveryPhase.AwaitingHuman, error);
    }
    /// <summary>@shape recoveryId: host token bound to run; concurrent callers join one bounded verification.
    /// @source request: ../Models/RecoveryModels.cs#class:RecoveryRequest</summary>
    public Task<RecoveryResult> VerifyAndContinueAsync(TaskExecutionContext context, Guid recoveryId,
        CancellationToken cancellationToken = default, DateTimeOffset? requestDeadline = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.IsBusinessClosed) throw new OperationCanceledException();
        if (context.Snapshot.Recovery is { Phase: RecoveryPhase.Resolved } completed && completed.RecoveryId == recoveryId)
            return Task.FromResult(completed.Result);
        if (!_sessions.TryGetValue(context.InstanceId, out var session) || !ReferenceEquals(session.Context, context) ||
            session.Request.RecoveryId != recoveryId)
            throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidLifecycleState);
        context.CheckRecovery(recoveryId);
        lock (session.Gate)
        {
            if (session.Verification is { IsCompleted: false }) return session.Verification;
            if (context.TaskStatus != AutomationTaskStatus.AwaitingHuman)
                throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidLifecycleState);
            return StartVerificationLocked(session, automatic: false, cancellationToken, requestDeadline);
        }
    }
    private Task<RecoveryResult> StartVerification(Session session, bool automatic)
    { lock (session.Gate) return StartVerificationLocked(session, automatic, CancellationToken.None); }
    private Task<RecoveryResult> StartVerificationLocked(Session session, bool automatic, CancellationToken callerToken, DateTimeOffset? requestDeadline = null)
    {
        if (session.ActualVerification is { IsCompleted: false } || session.Context.RecoveryWorkInFlight) return Task.FromResult(Result(session, RecoveryStatus.NeedsHuman, RecoveryError.Busy));
        return session.Verification = Task.Run(() => VerifyAsync(session, automatic, callerToken, requestDeadline));
    }
    private async Task<RecoveryResult> VerifyAsync(Session session, bool automatic, CancellationToken callerToken, DateTimeOffset? requestDeadline)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Context.Cancellation.Token, callerToken);
        DateTimeOffset expiresAt = DateTimeOffset.MaxValue;
        try
        {
            callerToken.ThrowIfCancellationRequested();
            Check(session);
            if (!automatic && session.Context.AcquireRecoveryInput is { } acquire) await acquire().ConfigureAwait(false);
            var validator = _validators.Find(session.Request.Installation, session.Request.Scenario);
            if (validator is null)
            { await WaitHumanAsync(session, RecoveryError.NoValidator).ConfigureAwait(false); return Result(session, RecoveryStatus.NeedsHuman, RecoveryError.NoValidator); }
            long generation = Interlocked.Increment(ref session.ValidationGeneration);
            Update(session, RecoveryPhase.Validating, validation: generation);
            expiresAt = _clock.GetUtcNow() + _options.ValidationTimeout;
            if (requestDeadline is { } requested && requested < expiresAt) expiresAt = requested;
            deadline.CancelAfter(_options.ValidationTimeout);
            using var validation = new RecoveryValidationContext(session.Request, session.Context,
                _authorization, () => Check(session), deadline.Token);
            CancellationToken validationToken = deadline.Token;
            Task<bool> actual = AdviceAsync(() => validator.ValidateAsync(validation, validationToken));
            Observe(actual);
            lock (session.Gate) session.ActualVerification = actual;
            bool valid;
            try { valid = await actual.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !session.Context.IsBusinessClosed && !session.Context.Cancellation.IsCancellationRequested)
            { await WaitHumanAsync(session, RecoveryError.ValidationTimedOut).ConfigureAwait(false); return Result(session, RecoveryStatus.NeedsHuman, RecoveryError.ValidationTimedOut); }
            deadline.Token.ThrowIfCancellationRequested();
            Check(session);
            var result = Result(session, valid ? RecoveryStatus.Resolved : RecoveryStatus.NeedsHuman,
                valid ? RecoveryError.None : RecoveryError.ValidationRejected);
            if (session.Record is { } record)
                await ActualAsync(session, async () =>
                {
                    await _logger.UpdateRecoveryAsync(record, new(session.Request.RecoveryId, generation,
                        valid ? "resolved" : "needs_human", result.Error.ToString(), session.Request.Scenario, DateTime.UtcNow)).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            Check(session);
            if (!ReferenceEquals(_validators.Find(session.Request.Installation, session.Request.Scenario), validator))
            { await WaitHumanAsync(session, RecoveryError.NoValidator).ConfigureAwait(false); return Result(session, RecoveryStatus.NeedsHuman, RecoveryError.NoValidator); }
            if (!valid) { await WaitHumanAsync(session, result.Error).ConfigureAwait(false); return result; }
            // The last registry/service lookup may block or run user code. Commit checks must follow it
            // under the same lifecycle lock as Stop and include the original request deadline.
            if (!session.Context.ResolveRecovery(session.Request, result, generation, deadline.Token, expiresAt, _clock, session.Result))
                throw new OperationCanceledException();
            return result;
        }
        catch (OperationCanceledException) when (session.Context.IsBusinessClosed || session.Context.Cancellation.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            await WaitHumanAsync(session, RecoveryError.Cancelled).ConfigureAwait(false);
            return Result(session, RecoveryStatus.NeedsHuman, RecoveryError.Cancelled);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested || _clock.GetUtcNow() >= expiresAt)
        {
            await WaitHumanAsync(session, RecoveryError.ValidationTimedOut).ConfigureAwait(false);
            return Result(session, RecoveryStatus.NeedsHuman, RecoveryError.ValidationTimedOut);
        }
        catch (InputUnavailableException)
        {
            // Return-control is required, not implicit; detector/control failures never resume Lua.
            if (session.Context.IsBusinessClosed) throw new OperationCanceledException();
            return Result(session, RecoveryStatus.NeedsHuman, RecoveryError.ResourceUnavailable);
        }
        catch (Exception error) { Fail(session, error); throw; }
    }
    private async Task<T> AdviceAsync<T>(Func<Task<T>> execute)
    {
        if (Interlocked.Increment(ref _adviceInFlight) > AdviceCapacity)
        { Interlocked.Decrement(ref _adviceInFlight); throw new RecoveryFailedException(RecoveryError.Busy); }
        try { return await Task.Run(execute).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _adviceInFlight); }
    }
    private static async Task<T> ActualAsync<T>(Session session, Func<Task<T>> execute)
    {
        var permit = session.Context.EnterRecoveryWork(session.Request.RecoveryId);
        Task<T> actual = Task.Run(async () =>
        {
            using (permit)
            {
                try { session.Context.CheckRecovery(session.Request.RecoveryId); return await execute().ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException) { session.Context.FailRecovery(session.Request.RecoveryId, RecoveryError.ResourceUnavailable); session.Context.RecordFailure(error); session.Context.Cancel(); throw; }
            }
        });
        Observe(actual);
        return await actual.WaitAsync(session.Context.Cancellation.Token).ConfigureAwait(false);
    }
    private static void Observe(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        System.Threading.Tasks.TaskScheduler.Default);
    private void Fail(Session session, Exception error)
    {
        session.Context.FailRecovery(session.Request.RecoveryId,
            error is RecoveryFailedException failure ? failure.Error : RecoveryError.ResourceUnavailable);
        session.Context.RecordFailure(error);
        _diagnostics.LogError(error, "Recovery failed recovery={RecoveryId} instance={InstanceId}", session.Request.RecoveryId, session.Context.InstanceId);
        session.Result.TrySetException(error);
        Observe(session.Result.Task);
        session.Context.Cancel();
    }
}

/// <summary>A bounded read-only host capability. It exposes neither InputService nor HWND/runtime.</summary>
public sealed class RecoveryValidationContext : IDisposable
{
    private readonly TaskExecutionContext _context;
    private readonly IPluginAuthorization _authorization;
    private readonly Action _check;
    private readonly CancellationToken _token;
    private int _disposed;
    internal RecoveryValidationContext(RecoveryRequest request, TaskExecutionContext context,
        IPluginAuthorization authorization, Action check, CancellationToken token)
    { Request = request; _context = context; _authorization = authorization; _check = check; _token = token; }
    public RecoveryRequest Request { get; }
    /// <summary>@shape result: owned managed BGRA; no native pointer/cache; actual leaves drain before disposal.
    /// @source capture: ../Contracts/Services/ICaptureService.cs#interface:ICaptureService</summary>
    public async Task<CapturedFrame> CaptureFreshAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _token.ThrowIfCancellationRequested(); _check();
        using var admission = _authorization.Admit(_context.RecoveryAuthorization!, [Permission.Capture]);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, admission.Token);
        CancellationToken executionToken = linked.Token;
        while (true)
        {
            executionToken.ThrowIfCancellationRequested(); _check();
            if (!_context.Capture.IsCapturing) throw new RecoveryFailedException(RecoveryError.ResourceUnavailable);
            var permit = _context.EnterRecoveryWork(Request.RecoveryId);
            Task<CapturedFrame?> actual = Task.Run(async () =>
            {
                using (permit)
                {
                    try { _context.CheckRecovery(Request.RecoveryId); executionToken.ThrowIfCancellationRequested(); return await _context.Capture.GrabFrameAsync(executionToken).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { _context.FailRecovery(Request.RecoveryId, RecoveryError.ResourceUnavailable); _context.RecordFailure(error); _context.Cancel(); throw; }
                }
            });
            _ = actual.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
            var frame = await actual.WaitAsync(executionToken).ConfigureAwait(false);
            executionToken.ThrowIfCancellationRequested(); _check();
            if (frame is not null) return frame;
            // WGC TryGetNextFrame can return no newly available frame on a static window.
            // Wait within the validator deadline; never substitute the visual cache.
            await Task.Delay(16, executionToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

/// <summary>Controlled sample: observe fresh WGC images for a bounded green-panel transition.</summary>
public sealed class FreshGreenFrameValidator : IRecoveryValidator
{
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(32);
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public FreshGreenFrameValidator() : this(TimeProvider.System) { }
    internal FreshGreenFrameValidator(TimeProvider clock, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clock = clock;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, _clock, token));
    }

    /// <summary>@shape result: true only after an observed green frame; false after bounded red observation.
    /// @source capture: RecoveryCoordinator.cs#class:RecoveryValidationContext
    /// A new grab bypasses the managed cache but can still precede presentation of the user's change.
    /// Keep one verification request active while sampling; never retry input or suppress detection.
    /// The coordinator's shorter deadline/Stop can cancel every capture and asynchronous delay.</summary>
    public async Task<bool> ValidateAsync(RecoveryValidationContext context, CancellationToken cancellationToken)
    {
        long started = _clock.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await context.CaptureFreshAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsGreen(frame)) return true;
            TimeSpan remaining = ObservationWindow - _clock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return false;
            await _delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_clock.GetElapsedTime(started) >= ObservationWindow) return false;
        }
    }

    private static bool IsGreen(CapturedFrame frame)
    {
        int green = 0, total = 0;
        for (int y = frame.Height / 4; y < frame.Height * 3 / 4; y++)
            for (int x = frame.Width / 4; x < frame.Width * 3 / 4; x++)
            {
                int offset = y * frame.Stride + x * 4; total++;
                if (frame.Pixels[offset + 1] > 130 && frame.Pixels[offset] < 90 && frame.Pixels[offset + 2] < 90) green++;
            }
        return total > 0 && green > total * 0.7;
    }
}
