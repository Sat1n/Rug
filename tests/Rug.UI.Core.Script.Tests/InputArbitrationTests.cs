using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using Scheduler = Rug.UI.Core.Services.TaskScheduler;

internal static class InputArbitrationTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }
    private static async Task Cancelled(Task task)
    { try { await task; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { } }

    public static async Task RunAsync()
    {
        await MatrixAsync();
        await QueuedAdmissionAsync();
        await QueuedBridgeValidationAsync();
        await ActualGatewayAsync();
        await GatewayOutcomeAsync();
        await CleanupFailureAsync();
        await SchedulerTakeoverAsync();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            var validator = new InputWindowValidator();
            InputTarget old;
            using (var window = new TestWindow())
            {
                old = validator.Bind(window.Handle);
                Check(validator.Bind(window.Handle) == old && validator.IsValid(old), "same physical window got different lifetime");
            }
            Check(!validator.IsValid(old), "destroyed real test window stayed valid");
            using var fresh = new TestWindow();
            Check(validator.Bind(fresh.Handle) != old, "replacement window inherited binding");
        }
        Console.WriteLine("Shared input conflict-FIFO, composite leases, queue cancellation, late native tasks, manual takeover and window lifetime tests passed");
    }

    private static async Task MatrixAsync()
    {
        foreach (var (same, firstMode, secondMode, blocked) in new[]
        {
            (true, InputDeliveryMode.Win32PostMessage, InputDeliveryMode.Win32PostMessage, true),
            (false, InputDeliveryMode.Win32SendInput, InputDeliveryMode.Win32SendInput, true),
            (false, InputDeliveryMode.Win32PostMessage, InputDeliveryMode.Win32PostMessage, false),
            (true, InputDeliveryMode.Win32SendInput, InputDeliveryMode.Win32PostMessage, true),
            (false, InputDeliveryMode.Win32SendInput, InputDeliveryMode.Win32PostMessage, false)
        })
        {
            var arbiter = new InputArbiter(new TestInputWindows());
            var a = new RunIdentity("first", Guid.NewGuid(), 1, Guid.NewGuid(), 123);
            var b = new RunIdentity("second", Guid.NewGuid(), 2, Guid.NewGuid(), same ? 123 : 456);
            var entered = Signal(); var exit = Signal(); var secondEntered = Signal();
            async Task First()
            {
                using var lease = await arbiter.AcquireAsync(InputOwner.ForRun(a), arbiter.BindTarget(a.TargetHwnd), firstMode, () => { });
                entered.SetResult(); await exit.Task;
            }
            async Task Second()
            {
                using var lease = await arbiter.AcquireAsync(InputOwner.ForRun(b), arbiter.BindTarget(b.TargetHwnd), secondMode, () => { });
                secondEntered.SetResult();
            }
            var first = First(); await entered.Task;
            var second = Second();
            Check(secondEntered.Task.IsCompleted != blocked, "mutual exclusion matrix violated");
            if (blocked) Check(arbiter.Snapshot.Waiting == 1, "conflicting action was not queued");
            exit.SetResult(); await Task.WhenAll(first, second);
            Check(arbiter.Snapshot is { Active: 0, Waiting: 0 }, "matrix leaked lease");
        }
        // Conflict FIFO forbids a background request passing an earlier foreground waiter on its window.
        var fair = new InputArbiter(new TestInputWindows(), capacity: 2);
        using var held = await fair.AcquireAsync(InputOwner.ForDevTools("host"), fair.BindTarget(123), InputDeliveryMode.Win32SendInput, () => { });
        var front = fair.AcquireAsync(InputOwner.ForDevTools("host2"), fair.BindTarget(456), InputDeliveryMode.Win32SendInput, () => { });
        var back = fair.AcquireAsync(InputOwner.ForDevTools("host3"), fair.BindTarget(456), InputDeliveryMode.Win32PostMessage, () => { });
        Check(!front.IsCompleted && !back.IsCompleted, "later conflicting waiter bypassed FIFO");
        try { await fair.AcquireAsync(InputOwner.ForDevTools("host4"), fair.BindTarget(789), InputDeliveryMode.Win32PostMessage, () => { }); throw new Exception("unbounded queue"); }
        catch (InputUnavailableException) { }
        held.Dispose(); using var firstLease = await front;
        Check(!back.IsCompleted, "FIFO second overlapped first");
        firstLease.Dispose(); using var secondLease = await back;
    }

    private sealed class MutableWindows : IInputWindowValidator
    {
        public long Stamp = 1;
        public InputTarget Bind(nint hwnd) => new(hwnd, Stamp);
        public bool IsValid(InputTarget target) => target.WindowStamp == Stamp;
    }
    private static async Task QueuedAdmissionAsync()
    {
        var windows = new MutableWindows();
        var arbiter = new InputArbiter(windows);
        var owner = InputOwner.ForDevTools("trusted-host");
        var target = arbiter.BindTarget(123);
        using var held = await arbiter.AcquireAsync(owner, target, InputDeliveryMode.Win32PostMessage, () => { });
        using var cancel = new CancellationTokenSource();
        var waiting = arbiter.AcquireAsync(owner, target, InputDeliveryMode.Win32PostMessage, () => { }, cancel.Token);
        Check(arbiter.Snapshot.Waiting == 1, "not queued"); cancel.Cancel(); await Cancelled(waiting);
        Check(arbiter.Snapshot.Waiting == 0, "cancelled queue retained slot");
        var timed = arbiter.AcquireAsync(owner, target, InputDeliveryMode.Win32PostMessage, () => { }, deadline: DateTimeOffset.UtcNow.AddMilliseconds(40));
        await Cancelled(timed);
        Check(arbiter.Snapshot.Waiting == 0, "deadline retained queue slot");
        bool granted = true;
        var permission = arbiter.AcquireAsync(owner, target, InputDeliveryMode.Win32PostMessage,
            () => { if (!granted) throw new InvalidOperationException("revoked"); });
        granted = false; held.Dispose();
        try { await permission; throw new Exception("queued revoked grant admitted"); }
        catch (InvalidOperationException ex) when (ex.Message == "revoked") { }
        using var again = await arbiter.AcquireAsync(owner, target, InputDeliveryMode.Win32PostMessage, () => { });
        var invalid = arbiter.AcquireAsync(owner, target, InputDeliveryMode.Win32PostMessage, () => { });
        windows.Stamp++; again.Dispose();
        try { await invalid; throw new Exception("reused HWND admitted old target"); }
        catch (InputUnavailableException) { }
        Check(arbiter.Snapshot is { Active: 0, Waiting: 0 }, "failed admission leaked resources");
    }

    private static async Task ActualGatewayAsync()
    {
        var arbiter = new InputArbiter(new TestInputWindows());
        var input = new ControlledInput { HoldPress = true };
        string path = Path.Combine(AppContext.BaseDirectory, "arbiter-real-lua.lua");
        await File.WriteAllTextAsync(path, "function on_tick() rug.press_key(65) end");
        var manifest = new PluginManifest([Permission.ControlInput]) { Id = "arbiter-real", PluginDirectory = AppContext.BaseDirectory, TargetWindow = 123 };
        await using var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), input, NullLogger<LuaRuntime>.Instance, new FakeCoordinateMapper(), _ => true);
        runtime.ConfigureInputArbiter(arbiter);
        TestAuthorizationHost.Approve(runtime.Authorization, manifest);
        await runtime.InitializeAsync(path, manifest);
        var gateway = runtime.Gateway;
        var identity = runtime.BoundIdentity!;
        var siblingInput = new ControlledInput();
        await using var sibling = new LuaRuntime(new FakeCapture(), new FakeOcr(), siblingInput, NullLogger<LuaRuntime>.Instance, new FakeCoordinateMapper(), _ => true);
        sibling.ConfigureInputArbiter(arbiter);
        sibling.ConfigureGateway(gateway);
        await sibling.InitializeAsync(path, manifest);
        var caller = await gateway.CreateContextAsync(manifest.Id, "agent", AutomationSource.Agent);
        var request = new OperationRequest(AutomationOperation.PressKey, JsonSerializer.SerializeToElement(new { key = 65, durationMs = 0 }),
            Guid.NewGuid(), Guid.NewGuid().ToString(), OperationTarget.ForRun(identity), DateTimeOffset.UtcNow.AddMilliseconds(150));
        var first = gateway.ExecuteAsync(caller, request);
        await input.PressEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(arbiter.ActiveOwners.Single() is { Source: InputOwnerSource.Agent, Principal: "agent" } owner && owner.Run == identity, "input owner did not preserve trusted Gateway provenance");
        var result = await first;
        Check(result.Status == OperationStatus.TimedOut && result.OutcomeUncertain, "noncooperative input timeout lost uncertainty");
        Check(arbiter.Snapshot.Active == 1, "gateway timeout released active native lease");
        var next = gateway.ExecuteAsync(caller, request with { OperationId = Guid.NewGuid(), Target = OperationTarget.ForRun(sibling.BoundIdentity!), Deadline = DateTimeOffset.UtcNow.AddSeconds(5) });
        await Until(() => arbiter.Snapshot.Waiting == 1);
        Check(input.PressCalls == 1, "second operation entered live native call");
        Check((await gateway.ExecuteAsync(caller, request)).Status == OperationStatus.TimedOut && input.PressCalls == 1, "timeout duplicate repeated input");
        var takeover = arbiter.TakeOverAsync();
        Check(arbiter.Snapshot.State == InputControlState.TakingOver && !takeover.IsCompleted, "manual control reported before native exit");
        var nextResult = await next;
        Check(nextResult.Status == OperationStatus.Cancelled && !nextResult.OutcomeUncertain && siblingInput.PressCalls == 0, "queued takeover cancellation reported side effects or dispatched input");
        try { arbiter.ReturnControl(); throw new Exception("returned before cleanup"); } catch (InputUnavailableException) { }
        input.PressRelease.SetResult();
        await takeover.WaitAsync(TimeSpan.FromSeconds(3));
        Check(input.KeyUps == 1 && arbiter.Snapshot is { State: InputControlState.ManualControl, Active: 0, Waiting: 0 }, "native key cleanup missing");
        arbiter.ReturnControl();
        Check((await gateway.ExecuteAsync(caller, request with { OperationId = Guid.NewGuid(), Deadline = DateTimeOffset.UtcNow.AddSeconds(1) })).Status != OperationStatus.Success,
            "old run revived after return");
        // Real Lua and bridge composite coverage: move must finish before click, with the entire action reserved.
        var native = new ControlledInput { HoldMove = true };
        await using var click = new InputBridge(native, TestAuthorizationHost.Gate(manifest, NullLogger.Instance), new FakeCoordinateMapper(), _ => true, arbiter);
        await click.BindAsync(123, InputDeliveryMode.Win32PostMessage);
        var moving = click.ClickAsync(1, 2);
        await native.MoveEntered.Task;
        var devtools = arbiter.AcquireAsync(InputOwner.ForDevTools("host.devtools"), arbiter.BindTarget(123), InputDeliveryMode.Win32SendInput, () => { });
        Check(!devtools.IsCompleted && native.Clicks == 0, "click movement not reserved against DevTools");
        native.MoveRelease.SetResult(); await moving;
        using var diagnostic = await devtools;
        Check(native.Clicks == 1 && native.MouseUps == 1, "composite click failed cleanup");
        diagnostic.Dispose();
        using var cancel = new CancellationTokenSource();
        var key = click.PressKeyAsync(65, 60000, cancel.Token);
        await native.DownEntered.Task;
        var competitor = arbiter.AcquireAsync(InputOwner.ForDevTools("host.other"), arbiter.BindTarget(123), InputDeliveryMode.Win32PostMessage, () => { });
        Check(!competitor.IsCompleted, "key hold released resource early");
        cancel.Cancel(); await Cancelled(key);
        using var done = await competitor;
        Check(native.KeyUps == 1, "cancelled key hold not released");
        File.Delete(path);
    }

    private static async Task GatewayOutcomeAsync()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "input-outcomes.lua");
        await File.WriteAllTextAsync(path, "function on_tick() end");
        try
        {
            foreach (string scenario in new[] { "move-window", "move-permission", "foreground-permission", "foreground-key", "foreground-failed", "before-permission", "before-window", "queued-permission", "queued-cancel" })
            {
                bool allowed = true;
                int foregroundCalls = 0;
                var windows = new MutableWindows();
                var arbiter = new InputArbiter(windows);
                var auth = new PluginAuthorization((_, _) => Volatile.Read(ref allowed));
                var manifest = new PluginManifest([Permission.ControlInput]) { Id = "outcome-" + scenario, PluginDirectory = AppContext.BaseDirectory, TargetWindow = 123, InputDelivery = scenario == "foreground-key" ? InputDeliveryMode.Win32SendInput : InputDeliveryMode.Win32PostMessage };
                var input = new ControlledInput();
                input.AfterMove = () =>
                {
                    if (scenario == "move-window") windows.Stamp++;
                    if (scenario == "move-permission") Volatile.Write(ref allowed, false);
                };
                bool Foreground(nint hwnd)
                {
                    foregroundCalls++;
                    if (scenario is "foreground-permission" or "foreground-key") Volatile.Write(ref allowed, false);
                    return scenario != "foreground-failed";
                }
                await using var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), input, NullLogger<LuaRuntime>.Instance,
                    new FakeCoordinateMapper(), Foreground, authorization: auth);
                runtime.ConfigureInputArbiter(arbiter);
                TestAuthorizationHost.Approve(auth, manifest);
                await runtime.InitializeAsync(path, manifest);
                var gateway = runtime.Gateway;
                var caller = await gateway.CreateContextAsync(manifest.Id, "outcome-agent", AutomationSource.Agent);
                bool key = scenario == "foreground-key";
                var request = new OperationRequest(key ? AutomationOperation.PressKey : AutomationOperation.Click,
                    key ? JsonSerializer.SerializeToElement(new { key = 65 }) : JsonSerializer.SerializeToElement(new { x = 1, y = 2,
                        mode = (int)(scenario.StartsWith("foreground", StringComparison.Ordinal) ? InputDeliveryMode.Win32SendInput : InputDeliveryMode.Win32PostMessage) }),
                    Guid.NewGuid(), Guid.NewGuid().ToString(), OperationTarget.ForRun(runtime.BoundIdentity!), DateTimeOffset.UtcNow.AddSeconds(10));
                if (scenario == "before-permission") Volatile.Write(ref allowed, false);
                if (scenario == "before-window") windows.Stamp++;
                using var cancel = new CancellationTokenSource();
                IInputLease? held = null;
                OperationResult result;
                try
                {
                    if (scenario.StartsWith("queued", StringComparison.Ordinal))
                        held = await arbiter.AcquireAsync(InputOwner.ForDevTools("barrier"), arbiter.BindTarget(123), InputDeliveryMode.Win32PostMessage, () => { });
                    var executing = gateway.ExecuteAsync(caller, request, cancel.Token);
                    if (held is not null)
                    {
                        await Until(() => arbiter.Snapshot.Waiting == 1);
                        if (scenario == "queued-cancel") cancel.Cancel();
                        else Volatile.Write(ref allowed, false);
                        held.Dispose(); held = null;
                    }
                    result = await executing.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { held?.Dispose(); }
                bool started = scenario.StartsWith("move", StringComparison.Ordinal) || scenario.StartsWith("foreground", StringComparison.Ordinal);
                var expectedError = scenario.Contains("permission", StringComparison.Ordinal) || scenario == "foreground-key" ? OperationError.PermissionDenied
                    : scenario == "queued-cancel" ? OperationError.Cancelled
                    : scenario == "foreground-failed" ? OperationError.ExecutionFailed : OperationError.InputUnavailable;
                var expectedStatus = scenario == "queued-cancel" ? OperationStatus.Cancelled
                    : scenario == "foreground-failed" ? OperationStatus.Failed : OperationStatus.Rejected;
                Check(result.Status == expectedStatus && result.Error == expectedError && result.OutcomeUncertain == started,
                    $"{scenario}: incorrect outcome {result}");
                Check(input.MoveCalls == (scenario.StartsWith("move", StringComparison.Ordinal) ? 1 : 0) && input.Clicks == 0 && input.PressCalls == 0,
                    $"{scenario}: unexpected native input");
                Check(foregroundCalls == (scenario.StartsWith("foreground", StringComparison.Ordinal) ? 1 : 0), $"{scenario}: unexpected foreground attempt");
                // Restore only the live host policy so admission can read the retained result.
                // A denied pre-admission request is intentionally not cached and stays denied.
                if (scenario != "before-permission") Volatile.Write(ref allowed, true);
                var duplicate = await gateway.ExecuteAsync(caller, request);
                Check(scenario == "before-permission"
                    ? duplicate.Status == result.Status && duplicate.Error == result.Error && !duplicate.OutcomeUncertain
                    : duplicate == result, $"{scenario}: retry lost terminal outcome");
                Check(input.MoveCalls == (scenario.StartsWith("move", StringComparison.Ordinal) ? 1 : 0) && input.Clicks == 0 && input.PressCalls == 0
                    && foregroundCalls == (scenario.StartsWith("foreground", StringComparison.Ordinal) ? 1 : 0), $"{scenario}: duplicate repeated side effects");
                await Until(() => gateway.ActiveWorkCount == 0);
                Check(arbiter.Snapshot is { Active: 0, Waiting: 0 }, $"{scenario}: leaked input lease/queue");
            }
        }
        finally { File.Delete(path); }
        Console.WriteLine("Gateway input outcome boundaries and retained duplicate results passed (9 deterministic scenarios)");
    }

    private static async Task QueuedBridgeValidationAsync()
    {
        foreach (string reason in new[] { "revoke", "stop", "window" })
        {
            var windows = new MutableWindows(); var arbiter = new InputArbiter(windows);
            var auth = new PluginAuthorization();
            var manifest = new PluginManifest([Permission.ControlInput]) { Id = reason, PluginDirectory = AppContext.BaseDirectory };
            TestAuthorizationHost.Approve(auth, manifest);
            var permissions = new Rug.UI.Core.Security.PermissionInterceptor(manifest, NullLogger.Instance, auth);
            var input = new ControlledInput(); using var stopped = new CancellationTokenSource();
            await using var bridge = new InputBridge(input, permissions, new FakeCoordinateMapper(), _ => true, arbiter,
                validateRun: () => stopped.Token.ThrowIfCancellationRequested());
            await bridge.BindAsync(123, InputDeliveryMode.Win32PostMessage);
            using var held = await arbiter.AcquireAsync(InputOwner.ForDevTools("host"), arbiter.BindTarget(123), InputDeliveryMode.Win32PostMessage, () => { });
            var queued = bridge.ClickAsync(1, 2);
            Check(arbiter.Snapshot.Waiting == 1, "bridge did not join shared queue");
            if (reason == "revoke") auth.Revoke(PluginInstallation.From(manifest), [Permission.ControlInput]);
            if (reason == "stop") stopped.Cancel();
            if (reason == "window") windows.Stamp++;
            held.Dispose();
            try { await queued; throw new Exception("invalid queued input entered service"); }
            catch (Exception error) when (error is Rug.UI.Core.Exceptions.PermissionDeniedException or OperationCanceledException or InputUnavailableException) { }
            Check(!input.MoveEntered.Task.IsCompleted && input.Clicks == 0 && arbiter.Snapshot is { Active: 0, Waiting: 0 }, "queued rejection emitted input or leaked resources");
        }
    }

    private static async Task CleanupFailureAsync()
    {
        var arbiter = new InputArbiter(new TestInputWindows());
        var native = new ControlledInput { FailKeyUp = true };
        await using var bridge = new InputBridge(native, TestAuthorizationHost.Gate(new PluginManifest([Permission.ControlInput]), NullLogger.Instance),
            new FakeCoordinateMapper(), _ => true, arbiter);
        await bridge.BindAsync(123, InputDeliveryMode.Win32PostMessage);
        try { await bridge.PressKeyAsync(65); throw new Exception("release failure hidden"); } catch (InputCleanupException) { }
        Check(arbiter.Snapshot is { State: InputControlState.CleanupFailed, Active: 1 }, "unsafe resource released");
        try { await arbiter.TakeOverAsync(); throw new Exception("fault reported manual control"); } catch (InputUnavailableException) { }
        try { arbiter.ReturnControl(); throw new Exception("returned quarantined resource"); } catch (InputUnavailableException) { }
    }

    private static async Task SchedulerTakeoverAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "takeover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var id in new[] { "input-run", "visual-timer", "capture-start" })
            {
                string directory = Path.Combine(root, id); Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
                { id, name = id, version = "1", targetProcess = "test", permissions = id == "input-run" ? new[] { "input", "timer" } : id == "capture-start" ? new[] { "vision.capture", "timer" } : new[] { "timer" } }));
                await File.WriteAllTextAsync(Path.Combine(directory, "main.lua"), id == "input-run"
                    ? "function on_tick() rug.press_key(65, 60000) end" : "function on_tick() rug.sleep(10) end");
            }
            var arbiter = new InputArbiter(new TestInputWindows());
            var auth = new PluginAuthorization(); TestAuthorizationHost.ApproveCatalog(auth, root);
            var inputs = new List<ControlledInput>();
            bool holdPress = false;
            TaskCompletionSource<bool>? startGate = null;
            var captures = new List<FakeCapture>();
            await using var scheduler = new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance), root,
                () => { var c = new FakeCapture { StartGate = startGate }; captures.Add(c); return c; }, () => { var i = new ControlledInput { HoldPress = holdPress }; inputs.Add(i); return i; },
                (c, i) => new LuaRuntime(c, new FakeOcr(), i, NullLogger<LuaRuntime>.Instance, new FakeCoordinateMapper(), _ => true),
                NullLogger<Scheduler>.Instance, new NoAnomaly(), authorization: auth, inputArbiter: arbiter);
            Guid timer = await scheduler.StartTaskAsync("visual-timer", 0);
            for (int round = 0; round < 4; round++)
            {
                Guid run = await scheduler.StartTaskAsync("input-run", 123);
                var identity = scheduler.Instances.Single(c => c.InstanceId == run).Identity;
                var caller = await scheduler.Gateway.CreateContextAsync("input-run", "mcp", AutomationSource.Mcp);
                var native = inputs[^1]; await native.DownEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(arbiter.ActiveOwners.Single().Source == InputOwnerSource.Lua, "actual Lua lease lost caller source");
                var queued = arbiter.AcquireAsync(InputOwner.ForDevTools("devtools"), arbiter.BindTarget(123), InputDeliveryMode.Win32PostMessage, () => { });
                var takeover = arbiter.TakeOverAsync(); Check(ReferenceEquals(takeover, arbiter.TakeOverAsync()), "duplicate takeover changed operation");
                await takeover.WaitAsync(TimeSpan.FromSeconds(5)); await Cancelled(queued);
                Check(native.KeyUps == 1 && native.Disposed && scheduler.Instances.Count == 1 && scheduler.Instances.Single().InstanceId == timer,
                    "takeover did not release input or stopped timer-only run");
                try { await scheduler.StartTaskAsync("input-run", 123); throw new Exception("started input during manual control"); } catch (InputUnavailableException) { }
                arbiter.ReturnControl(); arbiter.ReturnControl();
                var old = new OperationRequest(AutomationOperation.PressKey, JsonSerializer.SerializeToElement(new { key = 65 }),
                    Guid.NewGuid(), Guid.NewGuid().ToString(), OperationTarget.ForRun(identity), DateTimeOffset.UtcNow.AddSeconds(1));
                Check((await scheduler.Gateway.ExecuteAsync(caller, old)).Status == OperationStatus.Rejected, "old run resumed after return");
                Check(arbiter.Snapshot is { Active: 0, Waiting: 0 }, "takeover cycle leaked queue or lease");
            }
            holdPress = true;
            await File.WriteAllTextAsync(Path.Combine(root, "input-run", "main.lua"), "function on_tick() rug.press_key(66) end");
            await scheduler.StartTaskAsync("input-run", 123);
            var stuck = inputs[^1]; await stuck.PressEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task draining = arbiter.TakeOverAsync();
            await Until(() => scheduler.Instances.Any(c => c.PluginId == "input-run" && c.Cancellation.IsCancellationRequested));
            Check(!draining.IsCompleted && !stuck.Disposed && arbiter.Snapshot.Active == 1, "scheduler released native handle while noncooperative input was live");
            stuck.PressRelease.SetResult(); await draining.WaitAsync(TimeSpan.FromSeconds(3));
            Check(stuck.Disposed && stuck.KeyUps == 1 && scheduler.Instances.Single().InstanceId == timer, "native exit did not finish takeover cleanup");
            arbiter.ReturnControl();
            startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var startingVision = scheduler.StartTaskAsync("capture-start", 456);
            await Until(() => captures[^1].StartCalls == 1);
            try
            {
                await arbiter.TakeOverAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Check(!startingVision.IsCompleted, "takeover cancelled or waited for unrelated visual startup");
            }
            finally { startGate.TrySetResult(true); }
            Guid visual = await startingVision;
            await scheduler.StopTaskAsync(visual);
            arbiter.ReturnControl();
            await scheduler.StopTaskAsync(timer);
            await scheduler.DisposeAsync();
            Check(arbiter.RegisteredStopHandlerCount == 0 && scheduler.Instances.Count == 0, "scheduler disposal retained takeover subscription or instance");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class NoAnomaly : IAnomalyLogger
    { public Task<string> LogAnomalyAsync(TaskExecutionContext context, string reason, string detail) => Task.FromResult("unused"); }

    private sealed class ControlledInput : IInputService
    {
        public readonly TaskCompletionSource PressEntered = Signal(), PressRelease = Signal(), MoveEntered = Signal(), MoveRelease = Signal(), DownEntered = Signal();
        public bool HoldPress, HoldMove, FailKeyUp, Disposed;
        public int PressCalls, KeyUps, MouseUps, Clicks, MoveCalls;
        public Action? AfterMove;
        public Task MoveMouseAsync(int x, int y, TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true, CancellationToken cancellationToken = default)
        { MoveCalls++; MoveEntered.TrySetResult(); AfterMove?.Invoke(); return HoldMove ? MoveRelease.Task : Task.CompletedTask; }
        public Task MoveMouseRelativeAsync(int dx, int dy, TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClickAsync(MouseButton button = MouseButton.Left, int holdMs = 0, CancellationToken cancellationToken = default) { Clicks++; return Task.CompletedTask; }
        public Task MouseDownAsync(MouseButton button, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MouseUpAsync(MouseButton button, CancellationToken cancellationToken = default) { MouseUps++; return Task.CompletedTask; }
        public Task DragAndDropAsync(int sx, int sy, int ex, int ey, TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task KeyDownAsync(int virtualKey, CancellationToken cancellationToken = default) { DownEntered.TrySetResult(); return Task.CompletedTask; }
        public Task KeyUpAsync(int virtualKey, CancellationToken cancellationToken = default)
        { KeyUps++; return FailKeyUp ? Task.FromException(new IOException("release failed")) : Task.CompletedTask; }
        public Task KeyPressAsync(int virtualKey, int holdMs = 0, CancellationToken cancellationToken = default)
        { PressCalls++; PressEntered.TrySetResult(); return HoldPress ? PressRelease.Task : Task.CompletedTask; }
        public Task SendTextAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetTargetAsync(nint hwnd, bool background, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetHumanizeConfigAsync(HumanizeConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TrajectorySample>> PlanTrajectoryAsync(int sx, int sy, int ex, int ey, TrajectoryType trajectory = TrajectoryType.CubicBezier, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TrajectorySample>>([]);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
