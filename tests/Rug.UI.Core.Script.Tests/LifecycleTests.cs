using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using Scheduler = Rug.UI.Core.Services.TaskScheduler;

internal static class LifecycleTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }
    private static Task Bound(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));

    public static async Task RunAsync()
    {
        foreach (string hook in new[] { "on_init", "on_tick" })
        { await SegmentAsync(hook); await SleepAsync(hook); }
        foreach (string stage in new[] { "capture", "ocr" }) await VisualAsync(stage);
        await InputBoundaryAsync(); await QueuedInputAsync(); await TimeoutStopAsync();
        await StartingStopAsync(); await StartupPauseTransitionAsync(); await IndependentRunsAsync(); await LostAuthorityAsync();
        await CleanupFailureAsync(); await AnomalyDrainAsync();
        await StopAdmissionAsync(bridgeQueue: true); await StopAdmissionAsync(bridgeQueue: false);
        await LateResumeStopAsync(); await TerminalAdmissionAsync(); await DispatchStopAsync(); await CompositeStopAsync();
        foreach (bool timeout in new[] { false, true })
            foreach (bool cleanup in new[] { false, true }) await RepeatedGatewayStopAsync(timeout, cleanup);
        await TerminalStopCompletionAsync();
        Console.WriteLine("Repeated Gateway Stop passed: pending work/cleanup, timed-out replay, validated identity/policy, shared completion and registry cleanup");
        Console.WriteLine("Stop admission barriers passed with uncancelled tokens: adapters, bridge/arbiter/dispatch queues, composite steps, releases, late Resume and terminal states");
        Console.WriteLine("Lifecycle barriers passed: init/tick, sleep/vision, actual input/releases, queued input, late work, stop priority, resume validation and cleanup");
    }

    private static async Task StopAdmissionAsync(bool bridgeQueue)
    {
        await using var f = await Fixture.CreateAsync("function on_tick() rug.sleep(60000) end function on_stop() rug.log('stop_once') end",
            [Permission.ControlInput, Permission.Timer, Permission.Capture]);
        f.HoldPress = bridgeQueue;
        var c = await f.StartAsync();
        await Until(() => c.Runtime.State == ScriptState.Yielded);
        var caller = await f.CallerAsync(c);
        bool cancellationChecked = false;
        using var cancellationObserver = c.Cancellation.Token.Register(() =>
        {
            // A separate thread must be able to query/admit while Cancel runs.
            // Reentrancy on the cancelling thread alone would not detect a held lock.
            var result = Task.Run(async () =>
            {
                Check(c.Snapshot.Status == AutomationTaskStatus.Stopping, "cancellation preceded closed admission");
                return await f.Scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.PressKey, c.Identity, new { key = 69 }));
            }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Check(result.Error == OperationError.InvalidLifecycleState && !result.OutcomeUncertain, "cancel callback reopened input");
            cancellationChecked = true;
        });
        IInputLease? blocker = null;
        Task<OperationResult>? first = null;
        using var releaseNotification = new ManualResetEventSlim();
        var notification = Signal();
        c.LifecycleChanged += (_, e) =>
        {
            if (e.Snapshot.Status != AutomationTaskStatus.Stopping) return;
            notification.TrySetResult(); releaseNotification.Wait();
        };
        Task? stop = null;
        try
        {
            if (bridgeQueue)
            {
                first = f.Scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.PressKey, c.Identity, new { key = 66 }));
                await Bound(f.Input.PressEntered.Task);
            }
            else blocker = await f.Arbiter.AcquireAsync(InputOwner.ForDevTools("test-blocker"), f.Arbiter.BindTarget(123),
                InputDeliveryMode.Win32PostMessage, () => { });
            var queuedRequest = Request(AutomationOperation.PressKey, c.Identity, new { key = 67 });
            var queued = f.Scheduler.Gateway.ExecuteAsync(caller, queuedRequest);
            if (bridgeQueue) await Until(() => f.Scheduler.Gateway.ActiveWorkCount >= 3); // Lua sleep + two inputs
            else await Until(() => f.Arbiter.Snapshot.Waiting == 1);
            stop = Task.Run(() => f.Scheduler.StopTaskAsync(c.InstanceId));
            await Bound(notification.Task);
            Check(c.TaskStatus == AutomationTaskStatus.Stopping && !c.Cancellation.IsCancellationRequested,
                "barrier failed to isolate Stopping before cancellation propagation");
            foreach (var source in new[] { AutomationSource.PluginUI, AutomationSource.Lua, AutomationSource.Agent, AutomationSource.Mcp })
            {
                var context = await f.CallerAsync(c, source);
                foreach (var op in new[] { AutomationOperation.PressKey, AutomationOperation.Sleep, AutomationOperation.Capture })
                {
                    object parameters = op switch { AutomationOperation.PressKey => new { key = 68 }, AutomationOperation.Sleep => new { ms = 1 }, _ => new { } };
                    var result = await f.Scheduler.Gateway.ExecuteAsync(context, Request(op, c.Identity, parameters));
                    Check(result.Status == OperationStatus.Rejected && result.Error == OperationError.InvalidLifecycleState && !result.OutcomeUncertain,
                        $"Stopping admitted new work before token cancellation: {source}/{op}");
                }
            }
            Check(!f.Scheduler.PauseTask(c.InstanceId) && !f.Scheduler.ResumeTask(c.InstanceId), "Stopping accepted late controls");
            // Let previously queued work reach actual admission while cancellation is still blocked.
            f.Input.PressRelease.TrySetResult(); blocker?.Dispose(); blocker = null;
            var refused = await BoundResult(queued);
            Check(refused.Status == OperationStatus.Rejected && refused.Error == OperationError.InvalidLifecycleState && !refused.OutcomeUncertain,
                "previously queued input ran after Stop without token propagation");
            Check(await f.Scheduler.Gateway.ExecuteAsync(caller, queuedRequest) == refused, "duplicate queued rejection changed outcome");
            if (first is not null) Check((await BoundResult(first)).Status == OperationStatus.Success, "admitted physical work failed to finish/releases");
            Check(f.Input.PressCalls == (bridgeQueue ? 1 : 0) && f.Input.KeyUps == (bridgeQueue ? 1 : 0) && f.Capture.FrameCalls == 0,
                "stopped input escaped admission or necessary release was blocked");
        }
        finally
        {
            blocker?.Dispose(); f.Input.PressRelease.TrySetResult(); releaseNotification.Set();
            if (stop is not null) await Bound(stop);
        }
        Check(cancellationChecked && c.TaskStatus == AutomationTaskStatus.Stopped && c.LastError is null && f.Logger.StopCalls == 1 &&
            f.Arbiter.Snapshot.Active == 0 && f.Arbiter.Snapshot.Waiting == 0, "Stop barrier leaked/faulted cleanup");
    }

    private static async Task LateResumeStopAsync()
    {
        await using var f = await Fixture.CreateAsync("function on_tick() rug.sleep(60000) end", [Permission.Timer]);
        var c = await f.StartAsync(); f.Scheduler.PauseTask(c.InstanceId);
        await Until(() => c.TaskStatus == AutomationTaskStatus.Paused);
        using var releaseValidation = new ManualResetEventSlim();
        using var releaseNotification = new ManualResetEventSlim();
        var validation = Signal(); var notification = Signal();
        var original = c.ValidateResume;
        c.ValidateResume = () => { validation.TrySetResult(); releaseValidation.Wait(); original?.Invoke(); };
        c.LifecycleChanged += (_, e) => { if (e.Snapshot.Status == AutomationTaskStatus.Stopping) { notification.TrySetResult(); releaseNotification.Wait(); } };
        var resume = Task.Run(() => f.Scheduler.ResumeTask(c.InstanceId));
        await Bound(validation.Task);
        var stop = Task.Run(() => f.Scheduler.StopTaskAsync(c.InstanceId));
        try
        {
            await Bound(notification.Task); releaseValidation.Set();
            Check(!await resume.WaitAsync(TimeSpan.FromSeconds(5)) && c.TaskStatus == AutomationTaskStatus.Stopping && !c.Snapshot.ResumeRequested &&
                !c.Cancellation.IsCancellationRequested, "late Resume reopened stopped business before cancellation");
        }
        finally { releaseValidation.Set(); releaseNotification.Set(); await Bound(stop); }
        Check(c.TaskStatus == AutomationTaskStatus.Stopped && c.LastError is null, "late Resume corrupted terminal cleanup");
    }

    private static async Task TerminalAdmissionAsync()
    {
        foreach (var status in new[] { AutomationTaskStatus.Stopping, AutomationTaskStatus.Stopped, AutomationTaskStatus.Faulted })
        {
            using var cancellation = new CancellationTokenSource();
            var capture = new GateCapture(); var input = new InputArbitrationTests.ControlledInput();
            await using var runtime = new LuaRuntime(capture, new GateOcr(), input, NullLogger<LuaRuntime>.Instance);
            var c = new TaskExecutionContext(new("terminal", Guid.NewGuid(), 1, null, 0), cancellation, runtime, capture, input);
            c.SetStatus(AutomationTaskStatus.Stopping); c.SetStatus(status);
            var manifest = new PluginManifest([Permission.Timer]) { Id = "terminal", PluginDirectory = AppContext.BaseDirectory };
            var auth = new PluginAuthorization(); auth.Register(manifest); auth.Grant(PluginInstallation.From(manifest), manifest.Permissions);
            var gateway = new AutomationGateway(NullLogger.Instance, authorization: auth);
            int calls = 0;
            gateway.RegisterRun(c.Identity, manifest, cancellation.Token, (_, _, _) => { calls++; return Task.FromResult<object?>(true); }, c);
            var caller = gateway.BindContext(manifest, "trusted-test", AutomationSource.Agent, c.Identity);
            var result = await gateway.ExecuteAsync(caller, Request(AutomationOperation.Sleep, c.Identity, new { ms = 1 }));
            Check(result.Status == OperationStatus.Rejected && result.Error == OperationError.InvalidLifecycleState && !result.OutcomeUncertain && calls == 0,
                $"terminal admission depended on a cancellation token: {status}");
            try { using var work = c.EnterOperation(input: false); throw new Exception("closed operation admitted"); }
            catch (AutomationGateway.GatewayRejectedException error) { Check(error.Error == OperationError.InvalidLifecycleState, "wrong closed code"); }
            gateway.EndRun(c.InstanceId); c.Complete();
        }
    }

    private static async Task DispatchStopAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var capture = new GateCapture(); var input = new InputArbitrationTests.ControlledInput();
        await using var runtime = new LuaRuntime(capture, new GateOcr(), input, NullLogger<LuaRuntime>.Instance);
        var c = new TaskExecutionContext(new("dispatch", Guid.NewGuid(), 1, null, 0), cancellation, runtime, capture, input);
        var manifest = new PluginManifest([Permission.Timer]) { Id = "dispatch", PluginDirectory = AppContext.BaseDirectory };
        var auth = new PluginAuthorization(); auth.Register(manifest); auth.Grant(PluginInstallation.From(manifest), manifest.Permissions);
        var entered = Signal(); var release = Signal();
        var gateway = new AutomationGateway(NullLogger.Instance, authorization: auth,
            waitForDispatch: () => { entered.TrySetResult(); return release.Task; });
        int calls = 0;
        gateway.RegisterRun(c.Identity, manifest, cancellation.Token, (_, _, _) => { calls++; return Task.FromResult<object?>(true); }, c);
        var caller = gateway.BindContext(manifest, "trusted-test", AutomationSource.Mcp, c.Identity);
        var request = Request(AutomationOperation.Sleep, c.Identity, new { ms = 1 });
        var work = gateway.ExecuteAsync(caller, request);
        try
        {
            await Bound(entered.Task); c.SetStatus(AutomationTaskStatus.Stopping); release.TrySetResult();
            var result = await BoundResult(work);
            Check(result.Error == OperationError.InvalidLifecycleState && !result.OutcomeUncertain && calls == 0 &&
                await gateway.ExecuteAsync(caller, request) == result, "dispatch queue admitted stopped work/duplicate");
            await Bound(c.DrainOperationsAsync());
            Check(!cancellation.IsCancellationRequested, "dispatch regression relied on cancellation propagation");
        }
        finally { release.TrySetResult(); await Bound(c.DrainOperationsAsync()); gateway.EndRun(c.InstanceId); c.Complete(); }
    }

    private static async Task CompositeStopAsync()
    {
        await using var f = await Fixture.CreateAsync("function on_tick() rug.sleep(60000) end", [Permission.ControlInput, Permission.Timer]);
        var c = await f.StartAsync(); var caller = await f.CallerAsync(c);
        f.Input.HoldMove = true;
        using var releaseNotification = new ManualResetEventSlim(); var notification = Signal();
        c.LifecycleChanged += (_, e) => { if (e.Snapshot.Status == AutomationTaskStatus.Stopping) { notification.TrySetResult(); releaseNotification.Wait(); } };
        var request = Request(AutomationOperation.Click, c.Identity, new { x = 1, y = 1 });
        // Use physical client validation without an actual HWND or native input.
        // The fixture's mapper is injected for the real bridge path.
        var work = f.Scheduler.Gateway.ExecuteAsync(caller, request);
        Task? stop = null;
        try
        {
            await Bound(f.Input.MoveEntered.Task);
            stop = Task.Run(() => f.Scheduler.StopTaskAsync(c.InstanceId)); await Bound(notification.Task);
            f.Input.MoveRelease.TrySetResult(); var result = await BoundResult(work);
            Check(!c.Cancellation.IsCancellationRequested && result.Error == OperationError.InvalidLifecycleState && result.OutcomeUncertain &&
                f.Input.MoveCalls == 1 && f.Input.Clicks == 0,
                "Stop allowed the next composite input step or discarded movement uncertainty");
            Check((await f.Scheduler.Gateway.ExecuteAsync(caller, request)).Status == OperationStatus.Rejected && f.Input.MoveCalls == 1 && f.Input.Clicks == 0,
                "duplicate composite request repeated input");
        }
        finally { f.Input.MoveRelease.TrySetResult(); releaseNotification.Set(); if (stop is not null) await Bound(stop); }
        Check(f.Input.Clicks == 0 && c.LastError is null && c.TaskStatus == AutomationTaskStatus.Stopped,
            "composite stop faulted/replayed physical work");
    }

    private static async Task RepeatedGatewayStopAsync(bool timeout, bool cleanup)
    {
        var policy = new StopPolicy();
        await using var f = await Fixture.CreateAsync("function on_tick() rug.sleep(60000) end function on_stop() rug.log('stop_once') end",
            [Permission.ControlInput, Permission.Timer], policy);
        f.HoldPress = !cleanup;
        var c = await f.StartAsync(); var caller = await f.CallerAsync(c);
        f.Capture.HoldDispose = cleanup;
        Task<OperationResult>? input = null;
        if (!cleanup)
        {
            input = f.Scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.PressKey, c.Identity, new { key = 66 }));
            await Bound(f.Input.PressEntered.Task);
        }
        var request = Request(AutomationOperation.Stop, c.Identity);
        if (timeout) request = request with { Deadline = DateTimeOffset.UtcNow.AddMilliseconds(100) };
        var first = f.Scheduler.Gateway.ExecuteAsync(caller, request);
        try
        {
            if (cleanup) await Bound(f.Capture.DisposeEntered.Task);
            else await Until(() => c.Runtime.State == ScriptState.Stopped);
            Check(c.TaskStatus == AutomationTaskStatus.Stopping && f.Scheduler.Gateway.RegisteredRunCount == 1,
                "Lua Stop removed host lifecycle record before actual work/cleanup ended");
            OperationResult? expired = timeout ? await BoundResult(first) : null;
            Check(timeout ? expired!.Status == OperationStatus.TimedOut && expired.Error == OperationError.DeadlineExceeded : !first.IsCompleted,
                "first Stop failed to retain pending cleanup/its own timeout");
            var retry = f.Scheduler.Gateway.ExecuteAsync(caller, request with { Deadline = DateTimeOffset.UtcNow.AddSeconds(10), CorrelationId = "retry" });
            Check(ReferenceEquals(first, retry), "same operationId Stop did not share original request task");
            Check(ReferenceEquals(first, f.Scheduler.Gateway.ExecuteAsync(caller, request)), "expired original Stop policy bypassed retained request task");
            if (timeout) Check(await retry == expired, "retry replaced original timeout with a new policy/result");
            policy.RejectStop = true;
            Check((await f.Scheduler.Gateway.ExecuteAsync(caller, request)).Error == OperationError.PermissionDenied,
                "Stop replay skipped current host permission policy before deduplication");
            policy.RejectStop = false;
            var others = Enumerable.Range(0, 3).Select(_ => f.Scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.Stop, c.Identity))).ToArray();
            await Until(() => others.Any(t => t.IsCompleted) || f.Scheduler.Gateway.ActiveWorkCount >= 4);
            Check(others.All(t => !t.IsCompleted), "different Stop IDs did not await the same actual cleanup");
            var denied = await f.Scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.PressKey, c.Identity, new { key = 67 }));
            Check(denied.Error == OperationError.InvalidLifecycleState && !denied.OutcomeUncertain && f.Input.PressCalls == (cleanup ? 0 : 1),
                "retained lifecycle record reopened stopped input");
            foreach (var forged in new[]
            {
                request with { Target = request.Target with { RunGeneration = c.RunGeneration + 1 } },
                request with { Target = request.Target with { TargetBindingId = Guid.NewGuid() } },
                request with { Target = request.Target with { PluginId = "another-plugin" } },
                request with { Target = request.Target with { InstanceId = Guid.NewGuid() } }
            })
            {
                var rejected = await f.Scheduler.Gateway.ExecuteAsync(caller, forged);
                Check(rejected.Status == OperationStatus.Rejected && rejected.Error is OperationError.StaleRun or OperationError.BindingMismatch or OperationError.InvalidCaller or OperationError.InstanceNotFound,
                    "Stop deduplication bypassed full target validation");
            }
            var foreignManifest = new PluginManifest([Permission.ControlInput, Permission.Timer]) { Id = "lifecycle", PluginDirectory = Path.Combine(f.Root, "other-installation") };
            var foreign = f.Scheduler.Gateway.BindContext(foreignManifest, "test-host", AutomationSource.PluginUI, c.Identity);
            Check((await f.Scheduler.Gateway.ExecuteAsync(foreign, request)).Error == OperationError.InvalidCaller,
                "same ID Stop inherited a foreign installation's cached result");
            f.Input.PressRelease.TrySetResult(); f.Capture.DisposeRelease.TrySetResult();
            foreach (var other in others) Check((await BoundResult(other)).Status == OperationStatus.Success, "joined Stop failed after real cleanup");
            if (!timeout) Check((await BoundResult(first)).Status == OperationStatus.Success, "original Stop failed to complete shared cleanup");
            else Check(await retry == expired, "late cleanup changed original timeout result");
            if (input is not null) await BoundResult(input);
            await Until(() => f.Scheduler.Gateway.ActiveWorkCount == 0);
            Check(c.TaskStatus == AutomationTaskStatus.Stopped && c.LastError is null && f.Logger.StopCalls == 1 &&
                f.Capture.StopCalls == 1 && f.Capture.DisposeCalls == 1 && f.Input.KeyUps == (cleanup ? 0 : 1) &&
                f.Scheduler.Instances.Count == 0 && f.Scheduler.Gateway.RegisteredRunCount == 0 && f.Scheduler.Gateway.RetainedOperationCount == 0 &&
                f.Auth.ActiveLeaseCount == 0 && f.Arbiter.Snapshot.Active == 0,
                "repeated Stop reran cleanup or retained registry/dedup/resources");
        }
        finally { f.Input.PressRelease.TrySetResult(); f.Capture.DisposeRelease.TrySetResult(); }
    }

    private static async Task TerminalStopCompletionAsync()
    {
        await using var f = await Fixture.CreateAsync("function on_tick() rug.sleep(60000) end function on_stop() rug.log('stop_once') end", [Permission.Timer]);
        var c = await f.StartAsync(); var caller = await f.CallerAsync(c);
        using var release = new ManualResetEventSlim(); var entered = Signal();
        f.Scheduler.InstancesChanged += (_, _) =>
        {
            if (c.TaskStatus == AutomationTaskStatus.Stopped && f.Scheduler.Instances.Count == 0)
            { entered.TrySetResult(); release.Wait(); }
        };
        var original = f.Scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.Stop, c.Identity));
        try
        {
            await Bound(entered.Task);
            var joined = f.Scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.Stop, c.Identity));
            await Until(() => joined.IsCompleted || f.Scheduler.Gateway.ActiveWorkCount >= 2);
            Check(!original.IsCompleted && !joined.IsCompleted && f.Scheduler.Gateway.RegisteredRunCount == 1,
                "removed picker instance bypassed the authoritative host completion");
            release.Set();
            Check((await BoundResult(original)).Status == OperationStatus.Success && (await BoundResult(joined)).Status == OperationStatus.Success && f.Logger.StopCalls == 1,
                "terminal Stop did not share the original cleanup completion");
            await Until(() => f.Scheduler.Gateway.ActiveWorkCount == 0);
            Check(f.Scheduler.Gateway.RegisteredRunCount == 0 && f.Scheduler.Gateway.RetainedOperationCount == 0, "terminal completion retained lifecycle/Stop entries");
        }
        finally { release.Set(); }
    }

    private sealed class StopPolicy : IAutomationPermissionChecker
    {
        public volatile bool RejectStop;
        private readonly ManifestAutomationPermissionChecker _default = new();
        public bool IsAllowed(AutomationCallContext caller, IReadOnlyList<string> permissions, AutomationOperation operation) =>
            !(RejectStop && operation == AutomationOperation.Stop) && _default.IsAllowed(caller, permissions, operation);
    }

    private static async Task SegmentAsync(string hook)
    {
        using var release = new ManualResetEventSlim();
        var entered = Signal();
        await using var f = await Fixture.CreateAsync($"""
            function {hook}()
              rug.log('segment_barrier')
              rug.press_key(66)
              rug.set_state('consumed', true)
              rug.sleep(60000)
            end
            function on_stop() rug.log('stop_once') end
            """, [Permission.ControlInput, Permission.Timer]);
        f.Logger.Intercept = message => { if (message.Contains("segment_barrier")) { entered.TrySetResult(); release.Wait(); } };
        try
        {
            var context = await f.StartAsync(); await Bound(entered.Task);
            var before = context.Snapshot;
            Check(f.Scheduler.PauseTask(context.InstanceId), "segment pause was not accepted");
            Check(f.Scheduler.PauseTask(context.InstanceId), "duplicate pause not idempotent");
            var pending = context.Snapshot;
            Check(pending.PauseRequested && pending.Status == before.Status && pending.Revision > before.Revision,
                "live Lua segment falsely confirmed pause");
            Check(!f.Scheduler.ResumeTask(context.InstanceId), "resume accepted before pause confirmation");
            Check((await f.ControlAsync(context, AutomationOperation.Resume)).Error == OperationError.InvalidLifecycleState,
                "pending resume lacked explicit Gateway error");
            release.Set(); await Until(() => context.TaskStatus == AutomationTaskStatus.Paused);
            Check(f.Input.PressCalls == 0 && !f.Bus.GetState(context.InstanceScope, "consumed", false), "pause advanced into next input");
            Check(f.Scheduler.ResumeTask(context.InstanceId), "confirmed pause could not resume");
            await Until(() => f.Bus.GetState(context.InstanceScope, "consumed", false));
            Check(f.Input.PressCalls == 1 && f.Input.KeyUps == 1, "resume duplicated/deferred input incorrectly");
            await f.Scheduler.StopTaskAsync(context.InstanceId);
            Check(context.TaskStatus == AutomationTaskStatus.Stopped && context.LastError is null && f.Logger.StopCalls == 1,
                $"ordinary pause faulted/repeated stop: hook={hook} status={context.TaskStatus} stops={f.Logger.StopCalls} error={context.LastError}");
        }
        finally { release.Set(); }
    }

    private static async Task SleepAsync(string hook)
    {
        await using var f = await Fixture.CreateAsync($"""
            function {hook}() rug.set_state('before', true); rug.sleep(60000); rug.set_state('after', true) end
            """, [Permission.Timer]);
        var c = await f.StartAsync();
        await Until(() => c.Runtime.State == ScriptState.Yielded);
        Check(f.Scheduler.PauseTask(c.InstanceId), "sleep pause rejected");
        await Until(() => c.TaskStatus == AutomationTaskStatus.Paused);
        Check(!f.Bus.GetState(c.InstanceScope, "after", false), "sleep advanced Lua while paused");
        Check(f.Scheduler.ResumeTask(c.InstanceId), "sleep resume rejected");
        await Until(() => !c.PauseRequested);
        Check(!f.Bus.GetState(c.InstanceScope, "after", false), "resume replayed/finished pending sleep");
        await f.Scheduler.StopTaskAsync(c.InstanceId);
        Check(c.LastError is null, "sleep pause/stop became a fault");
    }

    private static async Task VisualAsync(string stage)
    {
        await using var f = await Fixture.CreateAsync("""
            function on_tick()
              local frame = rug.capture(); rug.set_state('width', frame.width)
              local result = rug.ocr(nil, 'zh-CN'); rug.set_state('text', result.text)
              rug.sleep(60000)
            end
            """, [Permission.Capture, Permission.VisionOcr, Permission.Timer]);
        f.HoldFrame = stage == "capture"; f.Ocr.Hold = stage == "ocr";
        var c = await f.StartAsync();
        await Bound(stage == "capture" ? f.Capture.FrameEntered.Task : f.Ocr.Entered.Task);
        f.Scheduler.PauseTask(c.InstanceId);
        await Until(() => c.TaskStatus == AutomationTaskStatus.Paused);
        f.Capture.FrameRelease.TrySetResult(); f.Ocr.Release.TrySetResult();
        await Until(() => f.Scheduler.Gateway.ActiveWorkCount == 0);
        Check(!f.Bus.GetState(c.InstanceScope, "text", "").Equals("vision-result"), "paused result advanced Lua");
        f.Scheduler.ResumeTask(c.InstanceId);
        await Until(() => f.Bus.GetState(c.InstanceScope, "text", "") == "vision-result");
        Check(f.Bus.GetState(c.InstanceScope, "width", 0) == 7 && f.Capture.FrameCalls == 1 && f.Ocr.Calls == 1,
            "resume did not consume cached vision result exactly once");
        await f.Scheduler.StopTaskAsync(c.InstanceId);

        // A second run is stopped with real capture work still pending: handles must stay alive.
        f.HoldFrame = true; f.Ocr.Hold = false;
        var late = await f.StartAsync(); var capture = f.Captures[^1];
        await Bound(capture.FrameEntered.Task);
        var stopping = f.Scheduler.StopTaskAsync(late.InstanceId);
        Check(late.TaskStatus == AutomationTaskStatus.Stopping && !stopping.IsCompleted && !capture.Disposed && capture.StopCalls == 0,
            "stop released a live capture service");
        capture.FrameRelease.TrySetResult(); await Bound(stopping);
        Check(late.TaskStatus == AutomationTaskStatus.Stopped && capture.Disposed && capture.StopCalls == 1,
            "late visual work did not finish true cleanup");
    }

    private static async Task InputBoundaryAsync()
    {
        await using var f = await Fixture.CreateAsync("""
            function on_tick()
              rug.press_key(66, 1); rug.set_state('first_consumed', true)
              rug.press_key(67); rug.set_state('second_consumed', true); rug.sleep(60000)
            end
            function on_stop() rug.log('stop_once') end
            """, [Permission.ControlInput, Permission.Timer]);
        f.HoldUp = true;
        var c = await f.StartAsync(); await Bound(f.Input.UpEntered.Task);
        f.Scheduler.PauseTask(c.InstanceId);
        Check(c.PauseRequested && c.TaskStatus != AutomationTaskStatus.Paused && f.Arbiter.Snapshot.Active == 1,
            "pause confirmed before mandatory KeyUp completed");
        var request = Request(AutomationOperation.PressKey, c.Identity, new { key = 68 });
        var caller = await f.CallerAsync(c, AutomationSource.Mcp);
        var refusal = await f.Scheduler.Gateway.ExecuteAsync(caller, request);
        Check(refusal.Error == OperationError.InvalidLifecycleState && !refusal.OutcomeUncertain && f.Input.PressCalls == 0,
            "new paused input was executed or mislabeled uncertain");
        f.Input.UpRelease.TrySetResult();
        await Until(() => c.TaskStatus == AutomationTaskStatus.Paused);
        Check(!f.Bus.GetState(c.InstanceScope, "first_consumed", false) && f.Input.PressCalls == 0,
            "next Lua/input action ran before Resume");
        f.Scheduler.ResumeTask(c.InstanceId);
        await Until(() => f.Bus.GetState(c.InstanceScope, "second_consumed", false));
        var duplicate = await f.Scheduler.Gateway.ExecuteAsync(caller, request);
        Check(duplicate == refusal && f.Input.PressCalls == 1 && f.Input.KeyUps == 2,
            "resumed coroutine or duplicate rejected ID repeated an action");
        await f.Scheduler.StopTaskAsync(c.InstanceId);
        Check(f.Logger.StopCalls == 1 && c.LastError is null, "input pause faulted");
    }

    private static async Task QueuedInputAsync()
    {
        await using var f = await Fixture.CreateAsync("function on_tick() rug.sleep(60000) end", [Permission.ControlInput, Permission.Timer]);
        f.HoldPress = true;
        var c = await f.StartAsync(); var caller = await f.CallerAsync(c);
        var first = f.Scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.PressKey, c.Identity, new { key = 66 }));
        await Bound(f.Input.PressEntered.Task);
        var second = f.Scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.PressKey, c.Identity, new { key = 67 }));
        await Until(() => f.Scheduler.Gateway.ActiveWorkCount >= 2);
        f.Scheduler.PauseTask(c.InstanceId);
        Check(c.TaskStatus != AutomationTaskStatus.Paused, "pause ignored previously admitted queued input");
        f.Input.PressRelease.TrySetResult();
        Check((await first).Status == OperationStatus.Success && (await second).Status == OperationStatus.Success, "admitted input did not settle");
        await Until(() => c.TaskStatus == AutomationTaskStatus.Paused);
        Check(f.Input.PressCalls == 2 && f.Input.KeyUps == 2 && f.Arbiter.Snapshot.Active == 0 && f.Arbiter.Snapshot.Waiting == 0,
            "paused instance retained input that can still fire");
        await f.Scheduler.StopTaskAsync(c.InstanceId);
    }

    private static async Task TimeoutStopAsync()
    {
        await using var f = await Fixture.CreateAsync("function on_tick() rug.sleep(60000) end function on_stop() rug.log('stop_once') end", [Permission.ControlInput, Permission.Timer]);
        f.HoldPress = true;
        var c = await f.StartAsync(); var caller = await f.CallerAsync(c);
        var request = Request(AutomationOperation.PressKey, c.Identity, new { key = 66 }) with { Deadline = DateTimeOffset.UtcNow.AddMilliseconds(100) };
        var work = f.Scheduler.Gateway.ExecuteAsync(caller, request);
        await Bound(f.Input.PressEntered.Task);
        Check((await BoundResult(work)).Status == OperationStatus.TimedOut, "Gateway did not expose timeout");
        f.Scheduler.PauseTask(c.InstanceId);
        Check(c.PauseRequested && c.TaskStatus != AutomationTaskStatus.Paused && f.Arbiter.Snapshot.Active == 1,
            "Gateway timeout prematurely confirmed pause/released native lease");
        var stopRequest = Request(AutomationOperation.Stop, c.Identity) with { Deadline = DateTimeOffset.UtcNow.AddMilliseconds(100) };
        Check((await BoundResult(f.Scheduler.Gateway.ExecuteAsync(caller, stopRequest))).Status == OperationStatus.TimedOut &&
              c.TaskStatus == AutomationTaskStatus.Stopping && !f.Input.Disposed && f.Arbiter.Snapshot.Active == 1,
            "Stop caller timeout released a live native handle or published completion");
        Task[] stops = Enumerable.Range(0, 8).Select(_ => f.Scheduler.StopTaskAsync(c.InstanceId)).ToArray();
        Check(c.TaskStatus == AutomationTaskStatus.Stopping && stops.All(t => !t.IsCompleted) && !f.Input.Disposed,
            "Stop did not dominate pending Pause or shared premature cleanup");
        Check(!f.Scheduler.PauseTask(c.InstanceId) && !f.Scheduler.ResumeTask(c.InstanceId), "Stopping returned to activity");
        Check((await f.Scheduler.Gateway.ExecuteAsync(caller, request)).Status != OperationStatus.Success && f.Input.PressCalls == 1,
            "late duplicate repeated input");
        f.Input.PressRelease.TrySetResult(); await Bound(Task.WhenAll(stops));
        Check(c.TaskStatus == AutomationTaskStatus.Stopped && f.Input.KeyUps == 1 && f.Input.Disposed && f.Logger.StopCalls == 1,
            "concurrent Stop did not share one real cleanup");
    }
    private static async Task<OperationResult> BoundResult(Task<OperationResult> work) => await work.WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task StartupPauseTransitionAsync()
    {
        // Exercise the transition authority without timing the driver: on_init
        // completed, then Pause won just before the Starting -> Running update.
        using var cancellation = new CancellationTokenSource();
        var capture = new GateCapture();
        var input = new InputArbitrationTests.ControlledInput();
        await using var runtime = new LuaRuntime(capture, new GateOcr(), input, NullLogger<LuaRuntime>.Instance);
        var context = new TaskExecutionContext(new("transition", Guid.NewGuid(), 1, null, 0), cancellation, runtime, capture, input);
        Check(context.Pause(), "startup boundary pause rejected");
        context.SetStatus(AutomationTaskStatus.Running);
        Check(context.TaskStatus == AutomationTaskStatus.Starting && context.PauseRequested, "startup boundary skipped pending pause");
        var boundary = context.WaitIfPausedAsync(cancellation.Token);
        Check(context.TaskStatus == AutomationTaskStatus.Paused && context.Resume(), "startup boundary could not confirm/resume");
        await Bound(boundary);
        Check(context.TaskStatus == AutomationTaskStatus.Running, "completed startup resumed into stale Starting");
        context.Cancel(); context.SetStatus(AutomationTaskStatus.Stopped); context.Complete();
    }

    private static async Task StartingStopAsync()
    {
        await using var f = await Fixture.CreateAsync("function on_tick() rug.sleep(60000) end", [Permission.Capture, Permission.Timer]);
        f.HoldStart = true;
        Task<Guid> starting = f.Scheduler.StartTaskAsync("lifecycle", 123);
        await Until(() => f.Captures.Count > 0 && f.Capture.StartEntered.Task.IsCompleted);
        var c = f.Scheduler.Instances.Single();
        Check(c.TaskStatus == AutomationTaskStatus.Starting, "Starting was not visible");
        var caller = await f.CallerAsync(c);
        var capture = await f.Scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.Capture, c.Identity));
        Check(capture.Error == OperationError.InvalidLifecycleState && f.Capture.FrameCalls == 0,
            "Gateway allowed business services during initialization");
        Check((await f.ControlAsync(c, AutomationOperation.Pause)).Status == OperationStatus.Success && c.PauseRequested,
            "Starting was not pausable through Gateway");
        var stop = f.ControlAsync(c, AutomationOperation.Stop);
        try { await starting; throw new Exception("cancelled initialization succeeded"); } catch (OperationCanceledException) { }
        Check((await BoundResult(stop)).Status == OperationStatus.Success, "Gateway could not stop initialization");
        Check(c.TaskStatus == AutomationTaskStatus.Stopped && f.Capture.Disposed && f.Scheduler.Instances.Count == 0,
            "startup stop leaked or regressed to Running");
    }

    private static async Task IndependentRunsAsync()
    {
        await using var f = await Fixture.CreateAsync("function on_tick() rug.sleep(60000) end function on_stop() rug.log('stop_once') end", [Permission.Timer]);
        var notifications = new ConcurrentQueue<LifecycleSnapshot>();
        f.Scheduler.LifecycleChanged += (_, e) => notifications.Enqueue(e.Snapshot);
        var a = await f.StartAsync(); var b = await f.StartAsync();
        await Until(() => a.Runtime.State == ScriptState.Yielded && b.Runtime.State == ScriptState.Yielded);
        f.Scheduler.PauseTask(a.InstanceId); await Until(() => a.TaskStatus == AutomationTaskStatus.Paused);
        Check(b.TaskStatus == AutomationTaskStatus.Running && !b.PauseRequested, "pause crossed instances");
        f.Scheduler.ResumeTask(a.InstanceId); await Until(() => !a.PauseRequested);
        f.Scheduler.PauseTask(a.InstanceId); await Until(() => a.TaskStatus == AutomationTaskStatus.Paused);
        using var go = new ManualResetEventSlim();
        var contenders = new[]
        {
            Task.Run(() => { go.Wait(); f.Scheduler.ResumeTask(a.InstanceId); }),
            Task.Run(() => { go.Wait(); f.Scheduler.PauseTask(a.InstanceId); }),
            Task.Run(async () => { go.Wait(); await f.Scheduler.StopTaskAsync(a.InstanceId); })
        };
        go.Set(); await Bound(Task.WhenAll(contenders));
        Check(a.TaskStatus == AutomationTaskStatus.Stopped && b.TaskStatus == AutomationTaskStatus.Running,
            "Stop race lost or affected sibling");
        var stale = notifications.First(s => s.Identity == a.Identity);
        Check(stale.Revision < a.Snapshot.Revision && f.Scheduler.GetLifecycle(a.InstanceId) is null,
            "old snapshot replaced/retained stopped run");
        for (int index = 0; index < 20; index++)
        {
            var c = await f.StartAsync(); f.Scheduler.PauseTask(c.InstanceId);
            await Until(() => c.TaskStatus == AutomationTaskStatus.Paused);
            await f.Scheduler.StopTaskAsync(c.InstanceId);
            Check(!f.Scheduler.ResumeTask(c.InstanceId) && !f.Scheduler.PauseTask(c.InstanceId), "terminal run revived");
        }
        await f.Scheduler.StopTaskAsync(b.InstanceId);
        foreach (var group in notifications.GroupBy(s => s.Identity.InstanceId))
        {
            long latest = 0;
            foreach (var snapshot in group) latest = Math.Max(latest, snapshot.Revision);
            Check(group.Last().Revision == latest && group.Last().Status == AutomationTaskStatus.Stopped,
                "terminal snapshot did not dominate delayed revisions");
        }
        Check(f.Scheduler.Instances.Count == 0 && f.Auth.ActiveLeaseCount == 0 && f.Store.ActivePartitionCount == 0,
            "repeat lifecycle retained instances/grants/state partitions");
    }

    private static async Task LostAuthorityAsync()
    {
        foreach (string loss in new[] { "target", "grant", "takeover" })
        {
            await using var f = await Fixture.CreateAsync("function on_tick() rug.sleep(60000) end", [Permission.ControlInput, Permission.Timer]);
            var c = await f.StartAsync(); f.Scheduler.PauseTask(c.InstanceId);
            await Until(() => c.TaskStatus == AutomationTaskStatus.Paused);
            var limited = await f.Scheduler.Gateway.CreateContextAsync("lifecycle", "limited-agent", AutomationSource.Agent,
                c.Identity, capabilities: []);
            Check((await f.Scheduler.Gateway.ExecuteAsync(limited, Request(AutomationOperation.Resume, c.Identity))).Error == OperationError.PermissionDenied,
                "Resume ignored caller capability ceiling");
            if (loss == "target")
            {
                f.Windows.Valid = false;
                Check((await f.ControlAsync(c, AutomationOperation.Resume)).Error == OperationError.InputUnavailable && c.TaskStatus == AutomationTaskStatus.Paused,
                    "resume rebound/ignored original target lifetime");
                await f.Scheduler.StopTaskAsync(c.InstanceId);
            }
            else if (loss == "grant")
            {
                f.Auth.Revoke(f.Installation, [Permission.ControlInput]);
                await Until(() => c.TaskStatus == AutomationTaskStatus.Stopped);
                f.Auth.Grant(f.Installation, [Permission.ControlInput]);
                Check(!f.Scheduler.ResumeTask(c.InstanceId), "regrant revived paused old run");
                var fresh = await f.StartAsync(); Check(fresh.Identity != c.Identity, "regrant reused run generation");
                await f.Scheduler.StopTaskAsync(fresh.InstanceId);
            }
            else
            {
                await Bound(f.Arbiter.TakeOverAsync());
                Check(c.TaskStatus == AutomationTaskStatus.Stopped, "takeover failed to stop Paused input run");
                f.Arbiter.ReturnControl(); Check(!f.Scheduler.ResumeTask(c.InstanceId), "Return control resumed old Paused run");
            }
        }
    }

    private static async Task CleanupFailureAsync()
    {
        foreach (string failure in new[] { "capture", "release", "on_stop" })
        {
            await using var f = await Fixture.CreateAsync(failure == "on_stop"
                ? "function on_tick() rug.sleep(60000) end function on_stop() rug.sleep(1) end"
                : "function on_tick() rug.sleep(60000) end function on_stop() rug.log('stop_once') end", [Permission.ControlInput, Permission.Timer]);
            var c = await f.StartAsync();
            await Until(() => c.Runtime.State == ScriptState.Yielded);
            if (failure == "capture") f.Capture.FailStop = true;
            if (failure == "release")
            {
                f.Input.HoldPress = true; f.Input.FailKeyUp = true;
                var caller = await f.CallerAsync(c);
                var work = f.Scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.PressKey, c.Identity, new { key = 66 }));
                await Bound(f.Input.PressEntered.Task);
                var stop = f.Scheduler.StopTaskAsync(c.InstanceId);
                f.Input.PressRelease.TrySetResult(); await Bound(stop); await work;
                Check(f.Arbiter.Snapshot.State == InputControlState.CleanupFailed, "release fault lost quarantine");
            }
            else await Bound(f.Scheduler.StopTaskAsync(c.InstanceId));
            Check(c.TaskStatus == AutomationTaskStatus.Faulted && c.LastError is not null && f.Capture.Disposed && f.Input.Disposed &&
                f.Capture.StopCalls == 1 && !f.Scheduler.ResumeTask(c.InstanceId),
                $"failure skipped cleanup/revived: stage={failure} status={c.TaskStatus} captureDisposed={f.Capture.Disposed} inputDisposed={f.Input.Disposed} stops={f.Capture.StopCalls} error={c.LastError}");
        }
    }

    private static async Task AnomalyDrainAsync()
    {
        await using var f = await Fixture.CreateAsync("function on_tick() rug.agent.resolve_anomaly('incident', 'detail') end",
            [Permission.Agent, Permission.Capture]);
        f.HoldFrame = true;
        var c = await f.StartAsync(); await Bound(f.Capture.FrameEntered.Task);
        var stop = f.Scheduler.StopTaskAsync(c.InstanceId);
        Check(c.TaskStatus == AutomationTaskStatus.Stopping && !stop.IsCompleted && !f.Capture.Disposed,
            "Stop disposed capture under existing anomaly logger");
        f.Capture.FrameRelease.TrySetResult(); await Bound(stop);
        Check(c.TaskStatus == AutomationTaskStatus.Stopped && f.Capture.Disposed,
            "existing anomaly logging did not drain during stop");
    }

    private static OperationRequest Request(AutomationOperation op, RunIdentity identity, object? parameters = null) =>
        new(op, JsonSerializer.SerializeToElement(parameters ?? new { }), Guid.NewGuid(), "lifecycle-test", OperationTarget.ForRun(identity), DateTimeOffset.UtcNow.AddSeconds(10));

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root;
        public readonly StateStore Store = new();
        public required CommandBus Bus;
        public readonly PluginAuthorization Auth = new();
        public readonly Windows Windows = new();
        public required InputArbiter Arbiter;
        public required Scheduler Scheduler;
        public readonly ProbeLogger Logger = new();
        public readonly GateOcr Ocr = new();
        public readonly List<GateCapture> Captures = [];
        public readonly List<InputArbitrationTests.ControlledInput> Inputs = [];
        public GateCapture Capture => Captures[0];
        public InputArbitrationTests.ControlledInput Input => Inputs[0];
        public bool HoldFrame, HoldStart, HoldPress, HoldUp;
        public bool Windowed;
        public PluginInstallation Installation => new("lifecycle", Path.Combine(Root, "lifecycle"));
        public static async Task<Fixture> CreateAsync(string script, string[] permissions, IAutomationPermissionChecker? permissionChecker = null)
        {
            string root = Path.Combine(AppContext.BaseDirectory, "lifecycle-" + Guid.NewGuid().ToString("N"));
            string plugin = Path.Combine(root, "lifecycle"); Directory.CreateDirectory(plugin);
            await File.WriteAllTextAsync(Path.Combine(plugin, "manifest.json"), JsonSerializer.Serialize(new
            { id = "lifecycle", name = "Lifecycle", version = "1", targetProcess = "test", permissions, backgroundInput = true }));
            await File.WriteAllTextAsync(Path.Combine(plugin, "main.lua"), script);
            var f = new Fixture { Root = root, Bus = null!, Arbiter = null!, Scheduler = null! };
            f.Bus = new(f.Store); f.Arbiter = new(f.Windows);
            f.Windowed = permissions.Contains(Permission.ControlInput) || permissions.Contains(Permission.Capture);
            TestAuthorizationHost.ApproveCatalog(f.Auth, root);
            f.Scheduler = new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance), root,
                () => { var c = new GateCapture { HoldFrame = f.HoldFrame, HoldStart = f.HoldStart }; f.Captures.Add(c); return c; },
                () => { var i = new InputArbitrationTests.ControlledInput { HoldPress = f.HoldPress, HoldUp = f.HoldUp }; f.Inputs.Add(i); return i; },
                (c, i) => new LuaRuntime(c, f.Ocr, i, f.Logger, new FakeCoordinateMapper(), ensureForeground: _ => true, commandBus: f.Bus),
                NullLogger<Scheduler>.Instance, new NoAnomaly(), commandBus: f.Bus, authorization: f.Auth, inputArbiter: f.Arbiter, permissionChecker: permissionChecker);
            return f;
        }
        public async Task<TaskExecutionContext> StartAsync()
        { var id = await Scheduler.StartTaskAsync("lifecycle", Windowed ? 123 : 0); return Scheduler.Instances.Single(c => c.InstanceId == id); }
        public Task<AutomationCallContext> CallerAsync(TaskExecutionContext c, AutomationSource source = AutomationSource.PluginUI) =>
            Scheduler.Gateway.CreateContextAsync("lifecycle", "test-host", source, c.Identity);
        public async Task<OperationResult> ControlAsync(TaskExecutionContext c, AutomationOperation op) =>
            await Scheduler.Gateway.ExecuteAsync(await CallerAsync(c), Request(op, c.Identity));
        public async ValueTask DisposeAsync()
        {
            foreach (var c in Captures) { c.FrameRelease.TrySetResult(); c.StartRelease.TrySetResult(); c.DisposeRelease.TrySetResult(); }
            foreach (var i in Inputs) { i.PressRelease.TrySetResult(); i.UpRelease.TrySetResult(); }
            Ocr.Release.TrySetResult();
            await Scheduler.DisposeAsync(); Bus.Dispose(); Directory.Delete(Root, true);
        }
    }
    private sealed class Windows : IInputWindowValidator
    {
        public bool Valid = true;
        public InputTarget Bind(nint hwnd) => new(hwnd, 1);
        public bool IsValid(InputTarget target) => Valid && target.WindowStamp == 1;
    }
    private sealed class NoAnomaly : IAnomalyLogger
    {
        public async Task<string> LogAnomalyAsync(TaskExecutionContext context, string reason, string detail)
        { await context.Capture.GrabFrameAsync(); return "unused"; }
    }
    private sealed class ProbeLogger : ILogger<LuaRuntime>
    {
        public Action<string>? Intercept;
        public int StopCalls;
        public IDisposable BeginScope<TState>(TState state) => new Scope();
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        { string message = format(state, error); if (message.Contains("stop_once")) Interlocked.Increment(ref StopCalls); Intercept?.Invoke(message); }
    }
    private sealed class GateCapture : ICaptureService
    {
        public readonly TaskCompletionSource StartEntered = Signal(), StartRelease = Signal(), FrameEntered = Signal(), FrameRelease = Signal();
        public readonly TaskCompletionSource DisposeEntered = Signal(), DisposeRelease = Signal();
        public bool HoldStart, HoldFrame, FailStop, Disposed, HoldDispose;
        public int StopCalls, FrameCalls, DisposeCalls;
        public bool IsCapturing => !Disposed;
        public async Task StartAsync(nint hwnd, CancellationToken cancellationToken = default)
        { StartEntered.TrySetResult(); if (HoldStart) await StartRelease.Task.WaitAsync(cancellationToken); }
        public async Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken = default)
        { FrameCalls++; FrameEntered.TrySetResult(); if (HoldFrame) await FrameRelease.Task; return new(new byte[28], 7, 1, 28); }
        public Task StopAsync() { StopCalls++; return FailStop ? Task.FromException(new IOException("capture stop failed")) : Task.CompletedTask; }
        public async ValueTask DisposeAsync()
        { DisposeCalls++; DisposeEntered.TrySetResult(); if (HoldDispose) await DisposeRelease.Task; Disposed = true; }
    }
    private sealed class GateOcr : IOcrService
    {
        public readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        public bool Hold; public int Calls;
        public async Task<IReadOnlyList<OcrTextBlock>> RecognizeFrameAsync(CapturedFrame frame, OcrEngineType engineType, string? modelId = null, CancellationToken cancellationToken = default)
        { Calls++; Entered.TrySetResult(); if (Hold) await Release.Task; return [new("vision-result", 1, new Rect(0, 0, 1, 1))]; }
        public Task<IReadOnlyList<OcrTextBlock>> RecognizeAsync(string imagePath, OcrEngineType engineType, string? modelId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<string> ListPaddleModelIds() => [];
    }
}
