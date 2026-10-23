using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;
using Scheduler = Rug.UI.Core.Services.TaskScheduler;

/// <summary>08C-02: persistent Lua sample, real WGC/matching/PostMessage/archives; host-only barriers.
/// @shape output: bounded metadata and normal trace/incident files, never VM stacks or frame pixels.
/// @source sample: samples/controlled-visual/recovery-plugin/main.lua</summary>
internal static class ControlledRecoverySample
{
    internal const string Plugin = "controlled_visual_recovery";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed class SampleLog : ILogger<LuaRuntime>
    {
        private sealed class NoopScope : IDisposable
        { internal static readonly NoopScope Instance = new(); public void Dispose() { } }
        internal int Stops;
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable BeginScope<TState>(TState state) => NoopScope.Instance;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        { if (format(state, error).Contains("[controlled-visual-recovery] stopped", StringComparison.Ordinal)) Interlocked.Increment(ref Stops); }
    }
    private sealed class CaptureBarrier : ICaptureService
    {
        private readonly CaptureService _real = new(exactBoundWindow: true);
        internal readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        internal bool Armed;
        public bool IsCapturing => _real.IsCapturing;
        public Task StartAsync(nint hwnd, CancellationToken cancellationToken = default) => _real.StartAsync(hwnd, cancellationToken);
        public async Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken = default)
        {
            var frame = await _real.GrabFrameAsync(cancellationToken);
            if (Armed) { Entered.TrySetResult(); await Release.Task; } // Deliberately non-cooperative actual service task.
            return frame;
        }
        public Task StopAsync() => _real.StopAsync();
        public ValueTask DisposeAsync() => _real.DisposeAsync();
    }
    private sealed class ValidationBarrier : IRecoveryValidator
    {
        private readonly FreshGreenFrameValidator _real = new();
        internal readonly TaskCompletionSource Entered = Signal(), Release = Signal(), Completed = Signal();
        internal bool Armed;
        internal int Calls;
        public async Task<bool> ValidateAsync(RecoveryValidationContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            try
            {
                bool result = await _real.ValidateAsync(context, cancellationToken);
                if (Armed) { Entered.TrySetResult(); await Release.Task; } // Late real observation, no fake approval.
                return result;
            }
            finally { Completed.TrySetResult(); }
        }
    }
    internal static string Repo()
    { DirectoryInfo? d = new(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "Rug.slnx"))) d = d.Parent; return d?.FullName ?? throw new DirectoryNotFoundException(); }
    internal static string Config => AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
    internal static string Install(string repo, string root)
    {
        string install = Path.Combine(root, "plugins", Plugin); Directory.CreateDirectory(install);
        foreach (string file in Directory.GetFiles(Path.Combine(repo, "samples", "controlled-visual", "recovery-plugin")))
            File.Copy(file, Path.Combine(install, Path.GetFileName(file)));
        return install;
    }
    internal static void LoadNative(string repo)
    {
        var assembly = typeof(InputService).Assembly;
        nint module = NativeLibrary.Load(Path.Combine(repo, "x64", Config, "Rug.Core.dll"), assembly, DllImportSearchPath.UseDllDirectoryForDependencies);
        NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name is "Rug.Core" or "Rug.Core.dll" ? module : 0);
    }
    internal static async Task Until(Func<bool> condition)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Controlled recovery state/barrier"); await Task.Delay(1); } }
    internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static async Task RunAsync(string output)
    {
        string repo = Repo(), root = Path.Combine(Path.GetFullPath(output), "recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); LoadNative(repo);
        var reports = new List<object>(); bool passed = true;
        foreach (string scene in new[] { "success", "stop-wait", "revoke-wait", "stop-validation", "revoke-validation", "cancel-late", "stop-late", "lua-error" })
        {
            try { reports.Add(await Scene(repo, root, scene)); }
            catch (Exception error)
            {
                passed = false;
                var store=new TraceFileStore(Path.Combine(root,scene,"traces"));
                var timeRejections=store.Discover(limit:32).Entries.SelectMany(entry=>store.Read(entry.HostSessionId,entry.TraceId,limit:256).Events)
                    .Where(e=>e.Observation?.TimeRejection is not null).Take(16)
                    .Select(e=>new{e.HostSessionId,e.TraceId,e.Sequence,e.Run,e.OperationId,e.ExecutionId,e.Error,e.Observation}).ToArray();
                reports.Add(new { scene, passed = false, error = error.ToString(),timeRejections });
                Console.WriteLine(scene + " FAILED: " + error);
            }
        }
        ControlledRecoveryUi.Result? ui=null;string? uiLaunchError=null;
        try { ui=await ControlledRecoveryUi.RunAsync(repo, Path.Combine(root, "ui")); }
        catch(Exception error) { uiLaunchError=error.ToString();Console.Error.WriteLine("RECOVERY UI launch/run failed: "+error); }
        await File.WriteAllTextAsync(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new
        { schemaVersion = 1, configuration = Config, functionalPassed = passed, scenes = reports, ui, uiLaunchError, humanAcceptance = "NOT RUN", sendInput = "NOT RUN" }, Json));
        Console.WriteLine("RECOVERY REPORT " + Path.Combine(root, "report.json"));
        if (!passed || ui is null || !ui.InteractionsPassed || ui.ExitCode != 0)
            throw new InvalidOperationException("Independent functional/UI/exit results retained; abnormal CLR shutdown remains a command failure.");
    }

    /// <summary>@shape scene: eight fixed cases; each owns target, installation, archive and trace root.
    /// @source recovery: Rug.UI.Core/Services/RecoveryCoordinator.cs#class:RecoveryCoordinator</summary>
    private static async Task<object> Scene(string repo, string root, string scene)
    {
        string area = Path.Combine(root, scene), install = Install(repo, area), incidents = Path.Combine(area, "incidents");
        if (scene == "lua-error")
        {
            string path = Path.Combine(install, "main.lua");
            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("local recovery = rug.agent", "error('real Lua failure'); local recovery = rug.agent"));
        }
        using var target = new CaptureGeometryDiagnosticWindow(install, 170, 150, recovery: true); nint hwnd = await target.Ready;
        var authorization = new PluginAuthorization(); TestAuthorizationHost.ApproveCatalog(authorization, Path.Combine(area, "plugins"));
        var arbiter = new InputArbiter(); var state = new StateStore(); using var bus = new CommandBus(state);
        var pool = new ObservationFramePool(); var capture = new CaptureBarrier(); var validator = new ValidationBarrier();
        var runtimeLog = new SampleLog();
        var registry = new RecoveryValidatorRegistry(); using var registration = registry.Register(new(Plugin, install), "controlled-visual-ready", validator);
        var logger = new AnomalyLogger(NullLogger<AnomalyLogger>.Instance, incidents);
        var recovery = new RecoveryCoordinator(logger, authorization, registry);
        await using var traces = new ExecutionTraceRecorder(Path.Combine(area, "traces"), new(FlushMilliseconds: 2000));
        TaskExecutionContext run;
        AnomalyLogModel? before = null, after = null; Guid? verifyOperation = null; int validations = 0;
        bool continued = false; string terminal = "";
        string? beforeConfirmation = null, afterSource = null, afterConfirmation = null;
        await using (var host = new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance), Path.Combine(area, "plugins"),
            () => capture, () => new InputService(),
            (c, i) => new LuaRuntime(c, new OcrService(), i, runtimeLog, templateMatcher: new TemplateMatchService(), commandBus: bus, observationFrames: pool),
            NullLogger<Scheduler>.Instance, logger, commandBus: bus, authorization: authorization, inputArbiter: arbiter, recovery: recovery, traces: traces))
        {
            try
            {
                Guid instance = await host.StartTaskAsync(Plugin, hwnd); run = host.Instances.Single(r => r.InstanceId == instance);
                int Read(string key) => state.GetState(run.InstanceScope, key, 0);
                if (scene == "lua-error")
                {
                    await run.Completion.WaitAsync(TimeSpan.FromSeconds(15));
                    Check(run.TaskStatus == AutomationTaskStatus.Faulted && Read("after") == 0 && target.ClickCounts == (1, 1), "real Lua error swallowed");
                }
                else
                {
                    await Until(() => run.TaskStatus == AutomationTaskStatus.AwaitingHuman || run.IsBusinessClosed);
                    Check(run.TaskStatus == AutomationTaskStatus.AwaitingHuman && target.ClickCounts == (1, 1) && Read("before") == 1 && Read("init_count") == 1 && Read("after") == 0, "BEFORE/retained wait failed: " + run.LastError);
                    Check(arbiter.Snapshot.State == InputControlState.ManualControl, "planned input handoff missing");
                    string recordPath = Directory.GetFiles(incidents, "*.json").Single();
                    AnomalyLogModel ReadIncident() => new IncidentStore(incidents).Read(Path.GetFileName(recordPath)).Record ?? throw new InvalidDataException("incident invalid");
                    before = ReadIncident();
                    beforeConfirmation = state.GetState(run.InstanceScope, "before_confirmation", "");
                    Check(before.SchemaVersion == 1 && before.IncidentId is not null && before.TraceLink?.TraceId == run.Trace!.TraceId &&
                        before.RecoveryId == run.Snapshot.Recovery!.RecoveryId && File.Exists(Path.Combine(incidents, before.ScreenshotPath)), "real PNG/v1 incident/link absent");
                    var caller = await host.Gateway.CreateContextAsync(Plugin, "controlled-recovery-host", AutomationSource.PluginUI, run.Identity);
                    OperationRequest Request() => new(AutomationOperation.VerifyRecovery, JsonSerializer.SerializeToElement(new { recoveryId = run.Snapshot.Recovery!.RecoveryId }), Guid.NewGuid(), "controlled-recovery", OperationTarget.ForRun(run.Identity), DateTimeOffset.UtcNow.AddSeconds(15));
                    arbiter.ReturnControl();
                    // An unrelated round-trip is a deterministic driver boundary, not a sleeping oracle.
                    var resume = new OperationRequest(AutomationOperation.Resume, JsonSerializer.SerializeToElement(new { }), Guid.NewGuid(), "must-not-bypass", OperationTarget.ForRun(run.Identity), DateTimeOffset.UtcNow.AddSeconds(15));
                    Check((await host.Gateway.ExecuteAsync(caller, resume)).Status == OperationStatus.Rejected && run.TaskStatus == AutomationTaskStatus.AwaitingHuman && target.ClickCounts == (1, 1), "return/ordinary Resume bypassed verification");
                    if (scene == "success")
                    {
                        var red = await host.Gateway.ExecuteAsync(caller, Request());
                        Check(red.Value is RecoveryResult { Status: RecoveryStatus.NeedsHuman, Error: RecoveryError.ValidationRejected } && target.ClickCounts == (1, 1), "unrepaired scene continued");
                    }
                    if (scene is "stop-wait" or "revoke-wait")
                    {
                        if (scene == "revoke-wait")
                        {
                            authorization.Revoke(new(Plugin, install), [Permission.Agent]);
                            await Until(() => run.IsBusinessClosed); // Revoke itself, not the following Stop, must close admission.
                        }
                        await host.StopTaskAsync(instance);
                    }
                    else
                    {
                        target.Repair(); arbiter.ReturnControl();
                        capture.Armed = scene is "stop-validation" or "revoke-validation";
                        validator.Armed = scene is "success" or "cancel-late" or "stop-late";
                        using var cancellation = new CancellationTokenSource(); var request = Request(); verifyOperation = request.OperationId;
                        var first = host.Gateway.ExecuteAsync(caller, request, cancellation.Token);
                        if (capture.Armed)
                        {
                            await capture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                            if (scene == "revoke-validation")
                            {
                                authorization.Revoke(new(Plugin, install), [Permission.Agent]);
                                await Until(() => run.IsBusinessClosed);
                            }
                            var stop = host.StopTaskAsync(instance); await Until(() => run.IsBusinessClosed);
                            Check(!stop.IsCompleted && capture.IsCapturing, "actual capture resources released before service task completed");
                            capture.Release.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(12)); await first;
                        }
                        else
                        {
                            await validator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                            if (scene == "cancel-late")
                            {
                                cancellation.Cancel(); Check((await first).Status == OperationStatus.Cancelled, "caller cancellation not reported");
                                validator.Release.TrySetResult(); await validator.Completed.Task;
                                await Until(() => run.TaskStatus == AutomationTaskStatus.AwaitingHuman);
                            }
                            else if (scene == "stop-late")
                            {
                                await host.StopTaskAsync(instance).WaitAsync(TimeSpan.FromSeconds(10));
                                validator.Release.TrySetResult(); await validator.Completed.Task; await first;
                            }
                            else
                            {
                                int started = validator.Calls;
                                var duplicate = host.Gateway.ExecuteAsync(caller, request);
                                var joinedRequest = Request(); var joined = host.Gateway.ExecuteAsync(caller, joinedRequest);
                                validator.Release.TrySetResult();
                                var results = await Task.WhenAll(first, duplicate, joined);
                                Check(results.All(r => r.Value is RecoveryResult { Status: RecoveryStatus.Resolved }) && validator.Calls == started, "verification dedup repeated actual validation");
                                await Until(() => state.GetState(run.InstanceScope, "done", false) || run.IsBusinessClosed);
                                continued = state.GetState(run.InstanceScope, "coroutine_preserved", false);
                                afterSource = state.GetState(run.InstanceScope, "after_source", "");
                                afterConfirmation = state.GetState(run.InstanceScope, "after_confirmation", "");
                                Check(!run.IsBusinessClosed && continued && Read("init_count") == 1 && Read("before") == 1 && Read("after") == 1 &&
                                    target.ClickCounts == (2, 2) && state.GetState(run.InstanceScope, "business", "") == "confirmed", "original coroutine/AFTER/new-frame confirmation failed: " + run.LastError);
                                Check(state.GetState(run.InstanceScope, "before_source", "") != state.GetState(run.InstanceScope, "after_source", ""), "AFTER reused BEFORE frame");
                                var replay = await host.Gateway.ExecuteAsync(caller, request);
                                Check(replay.Value is RecoveryResult { Status: RecoveryStatus.Resolved } && validator.Calls == started && target.ClickCounts == (2, 2), "replay repeated input");
                            }
                            if (!run.IsBusinessClosed) await host.StopTaskAsync(instance);
                        }
                    }
                    await run.Completion.WaitAsync(TimeSpan.FromSeconds(12));
                    after = ReadIncident();
                    Check(after.IncidentId == before.IncidentId && after.TraceLink == before.TraceLink && after.ScreenshotPath == before.ScreenshotPath, "recovery update replaced archive identity/link");
                    // InstanceState is deliberately removed by real Stop cleanup. Retention was
                    // checked while live above; use the independent receiver after cleanup.
                    Check(target.ClickCounts == (scene == "success" ? (2, 2) : (1, 1)), "BEFORE replayed or stopped/cancelled run continued");
                    if (scene == "success") Check(after.Verification?.Status == "resolved", "resolved verification not archived");
                }
                terminal = run.TaskStatus.ToString(); validations = validator.Calls;
                await run.DrainOperationsAsync(); Check(host.Instances.Count == 0, "run cleanup incomplete");
            }
            finally { capture.Release.TrySetResult(); validator.Release.TrySetResult(); }
        }
        await Until(() => recovery.ActiveRecoveries == 0 && recovery.AdviceInFlight == 0);
        Check(runtimeLog.Stops == 1, "on_stop did not execute exactly once");
        Check(pool.Usage == new ObservationUsage(0, 0, 0, 0, 0) && pool.ActionUsage.ActiveInputs == 0 && !capture.IsCapturing && arbiter.Snapshot.Active == 0 && arbiter.Snapshot.Waiting == 0, "resource drain failed");
        await traces.DisposeAsync();
        var store = new TraceFileStore(Path.Combine(area, "traces")); var events = new List<ExecutionTraceEvent>(); TracePage page; long next = 0;
        do { page = store.Read(traces.HostSessionId, run.Trace!.TraceId, afterSequence: next, limit: 256); events.AddRange(page.Events); next = page.NextSequence ?? 0; Check(events.Count <= 2048, "event capacity exceeded"); } while (page.NextSequence is not null);
        Check(page.Status == TraceReadStatus.Valid && page.Summary is { CleanupSucceeded: true, RunCleanupCompleted: true, ActualWorkOutstanding: 0 }, "trace/real cleanup incomplete");
        var clicks = events.Where(e => e.Operation == "ClickObserved" && e.Kind == TraceEventKind.CallerReturned).ToArray();
        Check(clicks.Length == (scene == "success" ? 2 : 1) && clicks.All(e => e.Observation?.ValidationFrames?.Count > 0), "click provenance missing");
        var confirmations = events.Where(e => e.Kind == TraceEventKind.Observation && e.Reason == "template-match:result" && e.Status == "Matched").ToArray();
        Check(beforeConfirmation is null || confirmations.Any(e => e.Observation?.FrameId.ToString() == beforeConfirmation), "BEFORE confirmation absent from structured trace");
        Check(afterConfirmation is null || confirmations.Any(e => e.Observation?.FrameId.ToString() == afterConfirmation), "AFTER confirmation absent from structured trace");
        target.Dispose();
        string plugins = Path.GetFullPath(Path.Combine(area, "plugins"));
        Check(plugins.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "unsafe installation cleanup");
        Directory.Delete(plugins, true); // Only this invocation's isolated installation; retain evidence.
        Console.WriteLine($"RECOVERY {scene}: BEFORE=1/1 AFTER={(scene == "success" ? "1/1" : "0/0")}; preserved={continued}; terminal={terminal}; incident={after?.IncidentId}; trace={traces.HostSessionId}/{run.Trace!.TraceId} Valid; resources=drained");
        return new { scene, passed = true, hostSessionId = traces.HostSessionId, traceId = run.Trace!.TraceId, instanceId = run.InstanceId, runGeneration = run.RunGeneration, targetBindingId = run.TargetBindingId,
            recoveryId = before?.RecoveryId, incidentId = after?.IncidentId, incidentLinkPreserved = before is null ? (bool?)null : after?.TraceLink == before.TraceLink, verification = after?.Verification,
            beforeDown = 1, beforeUp = 1, afterDown = target.ClickCounts.Down - 1, afterUp = target.ClickCounts.Up - 1,
            originalCoroutineContinued = scene == "success" ? (bool?)continued : null, finalBusinessConfirmation = scene == "success" ? "confirmed" : "NOT RUN", onStopCalls = runtimeLog.Stops,
            terminal, validations, verifyOperation, beforeConfirmationFrameId = beforeConfirmation, afterSourceFrameId = afterSource, afterConfirmationFrameId = afterConfirmation,
            clicks = clicks.Select(e => new { e.OperationId, e.ExecutionId, e.Status, e.Error, e.OutcomeUncertain, e.Observation }).ToArray(),
            traceIntegrity = page.Status.ToString(), cleanupSucceeded = page.Summary!.CleanupSucceeded, actualWorkOutstanding = page.Summary.ActualWorkOutstanding, resourcesDrained = true };
    }
}
