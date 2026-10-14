using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using Scheduler = Rug.UI.Core.Services.TaskScheduler;

internal static class InputInterventionTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }
    private static InputObservation Key(bool released = false, bool repeat = false, bool software = false) =>
        new(InputEventKind.Keyboard, 1, false, 66, 48, released ? (ushort)1 : (ushort)0, IsRepeat: repeat,
            ExtraInformation: software ? InputObservation.RugSoftwareInputTag : 0);
    private static InputObservation Mouse(ushort flags = 0, short wheel = 0, long x = 0, bool software = false) =>
        new(InputEventKind.Mouse, 2, false, ButtonFlags: flags, WheelDelta: wheel, X: x,
            ExtraInformation: software ? InputObservation.RugSoftwareInputTag : 0);

    internal sealed class Source : IInputEventSource
    {
        private long _sequence, _revision;
        public bool FailStartup, Disposed;
        public InputEventSourceState State = InputEventSourceState.Uninitialized;
        public long Generation = 1;
        public int Starts;
        public event EventHandler<InputSourceEvent>? InputReceived;
        public event EventHandler? StateChanged;
        public InputEventSourceSnapshot Snapshot => new(State, Generation, _revision, State == InputEventSourceState.Faulted ? InputEventSourceFault.NativeFailure : InputEventSourceFault.None,
            State == InputEventSourceState.Faulted ? "controlled source failure" : null, 0, 0, 0, 0, 0, 0, 0,
            (InputReceived?.GetInvocationList().Length ?? 0) + (StateChanged?.GetInvocationList().Length ?? 0), _sequence);
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            if (FailStartup) { SetState(InputEventSourceState.Faulted); throw new IOException("registration failed"); }
            SetState(InputEventSourceState.Ready); return Task.CompletedTask;
        }
        public Task<InputEventBoundary> CaptureBoundaryAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(new InputEventBoundary(Generation, _sequence, Stopwatch.GetTimestamp())); }
        public InputSourceEvent Capture(InputObservation observation) => new(Generation, ++_sequence, Stopwatch.GetTimestamp(), observation, FirstSequence: _sequence);
        public void Deliver(InputSourceEvent item) => InputReceived?.Invoke(this, item);
        public void Emit(InputObservation observation) => Deliver(Capture(observation));
        public void SetState(InputEventSourceState state) { State = state; _revision++; StateChanged?.Invoke(this, EventArgs.Empty); }
        public Task StopAsync() { SetState(InputEventSourceState.Stopped); InputReceived = null; StateChanged = null; return Task.CompletedTask; }
        public async ValueTask DisposeAsync() { Disposed = true; await StopAsync(); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly Source Source = new();
        public readonly InputArbiter Arbiter = new(new TestInputWindows());
        public readonly InputInterventionCoordinator Monitor;
        public ushort[] Held = [];
        public Fixture() => Monitor = new(Source, Arbiter, NullLogger<InputInterventionCoordinator>.Instance, () => Held);
        public Task StartAsync() => Monitor.StartAsync();
        public Task<IInputSession> SessionAsync(bool plugin = false) => Monitor.BeginSessionAsync(plugin
            ? InputOwner.ForRun(new RunIdentity("plugin", Guid.NewGuid(), 1, Guid.NewGuid(), 123)) : InputOwner.ForDevTools("host.devtools"));
        public async ValueTask DisposeAsync()
        {
            await Monitor.DisposeAsync(); await Monitor.DisposeAsync();
            Check(Source.Disposed && Source.Snapshot.Subscribers == 0 && Arbiter.RegisteredStopHandlerCount == 0 && Arbiter.RegisteredAdmissionGuardCount == 0,
                "detector disposal retained source subscriptions or arbiter handlers");
        }
    }
    public static async Task RunAsync()
    {
        await SchedulerCancellationOrderAsync();
        ManualFixtureSchema();
        await ManualFixtureScriptAsync();
        await PolicyAsync(); await AbsoluteMotionAsync(); await BoundariesAsync(); await FaultsAsync(); await LeasesAsync();
        await GatewayLateAsync(); await GatewayLateAsync(detectorFailure: true);
        await SchedulerAsync(); await PausedSourceFaultAsync(); await ProductionBarrierAsync(); await ProductionMotionAsync(); await ConsumerFaultAsync();
        Console.WriteLine("Candidate intervention policy, session boundaries, faults, delayed native cleanup and scheduler integration tests passed");
    }
    private static async Task SchedulerCancellationOrderAsync()
    {
        foreach (string phase in new[] { "init", "tick", "unexplained" })
        {
            string root = Path.Combine(AppContext.BaseDirectory, "cancel-order-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                foreach (string id in new[] { "input", "timer" })
                {
                    string directory = Path.Combine(root, id); Directory.CreateDirectory(directory);
                    await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
                    { id, name = id, version = "1", targetProcess = "test", permissions = id == "input" ? new[] { "input", "timer" } : new[] { "timer" } }));
                    string hook = id == "input" && phase == "init" ? "on_init" : "on_tick";
                    await File.WriteAllTextAsync(Path.Combine(directory, "main.lua"),
                        $"function {hook}() rug.sleep(60000) end function on_stop() rug.log('order_stop') end");
                }
                await using var f = new Fixture(); await f.StartAsync();
                var authorization = new PluginAuthorization(); TestAuthorizationHost.ApproveCatalog(authorization, root);
                using var release = new ManualResetEventSlim();
                var entered = Signal();
                CancellationTokenRegistration barrier = default;
                var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
                bool inputStarting = false;
                LuaRuntime? inputRuntime = null;
                FakeCapture? inputCapture = null;
                await using var scheduler = new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance), root,
                    () => { var capture = new FakeCapture(); if (inputStarting) inputCapture = capture; return capture; },
                    () => new FakeInput(), (capture, input) =>
                    {
                        // Cancellation callbacks run LIFO. Hold the earlier epoch/session link
                        // after the current host-owned epoch registration has closed admission.
                        // Lua lifetime is now separate so planned recovery can retire this epoch.
                        if (inputStarting && phase != "unexplained")
                            barrier = f.Arbiter.AutomationToken.Register(() => { entered.TrySetResult(); release.Wait(); });
                        var runtime = new LuaRuntime(capture, new FakeOcr(), input,
                            new RecordingLogger<LuaRuntime>(logs), new FakeCoordinateMapper(), _ => true);
                        if (inputStarting) inputRuntime = runtime;
                        return runtime;
                    }, new RecordingLogger<Scheduler>(logs), new NoAnomaly(), authorization: authorization,
                    inputArbiter: f.Arbiter, intervention: f.Monitor);
                try
                {
                    Guid timer = await scheduler.StartTaskAsync("timer", 0);
                    inputStarting = true;
                    Guid id = await scheduler.StartTaskAsync("input", 123);
                    var context = scheduler.Instances.Single(item => item.InstanceId == id);
                    await Until(() => inputRuntime!.State == ScriptState.Yielded);
                    if (phase == "unexplained") inputRuntime!.Stop();
                    else
                    {
                        f.Source.Emit(Mouse(x: 2));
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    }
                    await context.Runner!.WaitAsync(TimeSpan.FromSeconds(3));
                    if (phase == "unexplained")
                        Check(context.TaskStatus == AutomationTaskStatus.Faulted && context.LastError?.Message.Contains("Unexpected Lua hook state") == true,
                            "unexplained runtime stop was silently treated as takeover cancellation");
                    else
                        Check(context.TaskStatus == AutomationTaskStatus.Stopped && context.LastError is null &&
                            !logs.Any(item => item.Contains("faulted")), "known input epoch cancellation faulted the scheduler: " + phase);
                    Check(inputCapture is { StopCalls: 1, Disposed: true } && inputRuntime!.State == ScriptState.Stopped &&
                        scheduler.Instances.Single().InstanceId == timer && f.Monitor.Snapshot.Sessions == 0 &&
                        logs.Count(item => item == "Lua: order_stop") == 1, "cancellation order lost cleanup, hook or timer isolation");
                    release.Set();
                    if (phase != "unexplained")
                    {
                        await f.Monitor.PendingTakeover.WaitAsync(TimeSpan.FromSeconds(3));
                        Check(f.Arbiter.Snapshot is { State: InputControlState.ManualControl, Active: 0, Waiting: 0 },
                            "cancellation order did not finish takeover");
                        f.Arbiter.ReturnControl();
                        Check(scheduler.Instances.Single().InstanceId == timer, "return revived cancelled run");
                    }
                }
                finally { release.Set(); barrier.Dispose(); }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
        Console.WriteLine("Scheduler takeover cancellation callback order passed (init/tick); unexplained stops remain faults");
    }
    private static void ManualFixtureSchema()
    {
        var parser = new Rug.UI.Core.Schema.SchemaParser();
        foreach (string plugin in new[] { "timer_probe", "vision_probe", "input_probe" })
        {
            var schema = parser.Parse(ManualInputIntervention.CreateSchema(plugin));
            var start = schema.Children.Single(item => item.Id == "start");
            if (plugin == "timer_probe")
                Check(start.Type == "ButtonCard" && start.Command == "host.start_windowless",
                    "timer fixture lost its valid windowless start command");
            else
                Check(start.Type == "InfoBanner" && start.Description.Contains("运行绑定窗口任务") &&
                    schema.Children.All(item => item.Command != "host.start_windowless"),
                    "window-bound fixture still exposes an invalid windowless start command");
            Check(schema.Children.Single(item => item.Id == "stop").Command == "host.stop_selected",
                "manual fixture lost its instance stop command");
        }
    }
    private static async Task ManualFixtureScriptAsync()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "manual-fixture-" + Guid.NewGuid().ToString("N") + ".lua");
        await File.WriteAllTextAsync(path, ManualInputIntervention.CreateScript("input_probe"));
        try
        {
            foreach (bool cancelBeforeInput in new[] { true, false })
            {
                var input = new FakeInput();
                var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
                await using var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), input,
                    new RecordingLogger<LuaRuntime>(logs), new FakeCoordinateMapper(), _ => true);
                var manifest = new PluginManifest([Permission.Timer, Permission.ControlInput], TargetWindow: 123)
                    { InputDelivery = InputDeliveryMode.Win32SendInput };
                await TestAuthorizationHost.InitializeAsync(runtime, path, manifest);
                await runtime.PrepareLifecycleAsync();
                Check((await runtime.StepAsync()).State == ScriptState.Yielded && input.KeyDownCalls == 0 &&
                    logs.Any(item => item.Contains("waiting-before-first-B")), "fixture did not yield before input");
                var watch = Stopwatch.StartNew();
                Task<ScriptExecutionResult> waiting = runtime.ResumeAsync();
                if (cancelBeforeInput)
                {
                    runtime.Stop();
                    Check((await waiting.WaitAsync(TimeSpan.FromSeconds(1))).State == ScriptState.Stopped && input.KeyDownCalls == 0,
                        "fixture startup wait bypassed cancellation or emitted input");
                    continue;
                }
                Check((await waiting).State == ScriptState.Ready && watch.ElapsedMilliseconds >= 4900,
                    "fixture startup wait was shortened");
                await runtime.BeginTickAsync();
                Check((await runtime.StepAsync()).State == ScriptState.Yielded && logs.Any(item => item.Contains("issuing-B")),
                    "fixture omitted issuing phase");
                Check((await runtime.ResumeAsync()).State == ScriptState.Yielded && input.KeyDownCalls == 1 && input.KeyUpCalls == 1 &&
                    input.Key == 66 && logs.Any(item => item.Contains("B-call-completed")),
                    "fixture did not finish the B hold/release before its wait");
                Task<ScriptExecutionResult> gap = runtime.ResumeAsync();
                runtime.Stop();
                Check((await gap.WaitAsync(TimeSpan.FromSeconds(1))).State == ScriptState.Stopped && input.KeyDownCalls == 1,
                    "fixture waiting period survived stop or repeated input");
                await runtime.InvokeStopHookAsync(TimeSpan.FromSeconds(1));
                Check(logs.Any(item => item.Contains("on_stop")), "fixture lost stop evidence");
            }
        }
        finally { File.Delete(path); }
    }
    private static async Task ExpectUnavailable(Func<Task> action)
    { try { await action(); throw new Exception("Expected detector rejection"); } catch (InputUnavailableException) { } }
    private static async Task CompleteAsync(Fixture f, IInputSession session)
    {
        await Until(() => session.Token.IsCancellationRequested);
        session.Dispose();
        await f.Monitor.PendingTakeover.WaitAsync(TimeSpan.FromSeconds(5));
        Check(f.Arbiter.Snapshot.State == InputControlState.ManualControl && f.Monitor.Snapshot.Sessions == 0, "trigger did not safely hand control back");
    }
    private static async Task PolicyAsync()
    {
        foreach (var action in new[] { Key(), Mouse(flags: 1), Mouse(flags: 0x400, wheel: 120), Mouse(flags: 0x800, wheel: -120), Mouse(x: 2) })
        {
            await using var f = new Fixture(); await f.StartAsync();
            f.Source.Emit(action);
            Check(f.Arbiter.Snapshot.State == InputControlState.AutomationAvailable, "no-session action triggered takeover");
            f.Source.Emit(Key(released: true)); f.Source.Emit(Mouse(flags: 2));
            using var session = await f.SessionAsync();
            f.Source.Emit(Key(software: true)); f.Source.Emit(Key(released: true, software: true));
            f.Source.Emit(Mouse(flags: 1, x: 3, software: true)); f.Source.Emit(Mouse(flags: 2, software: true));
            f.Source.Emit(Key(released: true)); f.Source.Emit(Key(repeat: true)); f.Source.Emit(Key(released: true));
            f.Source.Emit(Mouse()); f.Source.Emit(Mouse(flags: 2)); f.Source.Emit(Mouse(flags: 0x400));
            f.Source.Emit(new(InputEventKind.DeviceArrived, 8, false));
            Check(f.Monitor.Snapshot.Monitoring && f.Arbiter.Snapshot.State == InputControlState.AutomationAvailable, "software/repeat/release/device/zero move triggered takeover");
            f.Source.Emit(action);
            var pending = f.Monitor.PendingTakeover;
            for (int i = 0; i < 10; i++) f.Source.Emit(Mouse(x: 1));
            Check(ReferenceEquals(pending, f.Monitor.PendingTakeover), "repeated events created new takeover tasks");
            await CompleteAsync(f, session);
        }
    }
    private static async Task AbsoluteMotionAsync()
    {
        await using var f = new Fixture(); await f.StartAsync();
        var position = Mouse(x: 100) with { MouseFlags = 1 };
        f.Source.Emit(position);
        using var session = await f.SessionAsync();
        f.Source.Emit(position);
        Check(f.Arbiter.Snapshot.State == InputControlState.AutomationAvailable, "unchanged absolute position triggered");
        f.Source.Emit(position with { X = 200, ExtraInformation = InputObservation.RugSoftwareInputTag });
        f.Source.Emit(position with { X = 200 });
        Check(f.Arbiter.Snapshot.State == InputControlState.AutomationAvailable, "stationary sample after software absolute movement triggered");
        f.Source.Emit(position with { X = 201 });
        await CompleteAsync(f, session);
    }
    private static async Task BoundariesAsync()
    {
        await using var f = new Fixture(); f.Held = [66]; await f.StartAsync(); await f.StartAsync();
        Check(f.Source.Starts == 1, "detector restarted on duplicate host startup");
        f.Source.Emit(Mouse(flags: 1)); // launch click is before registration
        var delayed = f.Source.Capture(Mouse(x: 5));
        using var old = await f.SessionAsync(plugin: true);
        f.Source.Deliver(delayed);
        f.Source.Emit(Key()); f.Source.Emit(Key(repeat: true)); // held before listener/session
        f.Source.Emit(Mouse(flags: 2));
        Check(f.Arbiter.Snapshot.State == InputControlState.AutomationAvailable, "old movement/held key/start-button release triggered");
        f.Source.Emit(Key(released: true));
        f.Source.Emit(Key()); await CompleteAsync(f, old);
        var manualEvent = f.Source.Capture(Mouse(x: 9));
        f.Held = [];
        f.Arbiter.ReturnControl();
        using var fresh = await f.SessionAsync(plugin: true);
        f.Source.Deliver(manualEvent);
        f.Source.Deliver(manualEvent with { Generation = 0, Sequence = 1000 });
        f.Source.Emit(Key(released: true)); f.Source.Emit(Mouse(flags: 2));
        var zero = f.Source.Capture(Mouse());
        f.Source.Deliver(zero with { FirstSequence = manualEvent.Sequence, HasMovement = true,
            MovementSequence = manualEvent.Sequence, MovementTicks = manualEvent.MonotonicTicks });
        Check(f.Arbiter.Snapshot.State == InputControlState.AutomationAvailable && old.Token.IsCancellationRequested,
            "old session event or return-button release revived/triggered a new run");
        var current = f.Source.Capture(Mouse(x: 1));
        f.Source.Deliver(current with { Samples = 2, FirstSequence = current.Sequence, HasMovement = true, Observation = Mouse() });
        await CompleteAsync(f, fresh);
        f.Arbiter.ReturnControl();
        for (int round = 0; round < 20; round++)
        {
            using var cycle = await f.SessionAsync();
            f.Source.Emit(Key(released: true)); f.Source.Emit(Key());
            await CompleteAsync(f, cycle); f.Arbiter.ReturnControl();
        }
        Check(f.Monitor.Snapshot.Sessions == 0 && f.Arbiter.Snapshot is { Active: 0, Waiting: 0 } && f.Source.Snapshot.Subscribers == 2,
            "repeated cycles retained sessions/leases/subscriptions");
    }
    private static async Task FaultsAsync()
    {
        await using (var f = new Fixture())
        {
            await ExpectUnavailable(async () => { using var session = await f.SessionAsync(); });
            await ExpectUnavailable(async () => { using var lease = await f.Arbiter.AcquireAsync(InputOwner.ForDevTools("host"), f.Arbiter.BindTarget(123), InputDeliveryMode.Win32PostMessage, () => { }); });
            f.Source.FailStartup = true;
            try { await f.StartAsync(); throw new Exception("source registration failure hidden"); } catch (IOException) { }
            await ExpectUnavailable(f.StartAsync);
            Check(f.Source.Starts == 1 && f.Monitor.Snapshot.DetectorError is not null, "fault silently retried detector startup");
        }
        foreach (var state in new[] { InputEventSourceState.Faulted, InputEventSourceState.Stopped })
        {
            await using var f = new Fixture(); await f.StartAsync();
            using var session = await f.SessionAsync();
            f.Source.SetState(state);
            Check(f.Monitor.Snapshot.DetectorError is not null, "source fault not visible");
            await CompleteAsync(f, session);
            await ExpectUnavailable(() => { f.Arbiter.ReturnControl(); return Task.CompletedTask; });
            Check(f.Monitor.Snapshot.CleanupError is null, "detector fault falsely reported cleanup failure");
        }
        await using (var f = new Fixture())
        {
            await f.StartAsync(); using var session = await f.SessionAsync();
            f.Source.Generation++; f.Source.SetState(InputEventSourceState.Ready);
            await CompleteAsync(f, session);
            Check(f.Monitor.Snapshot.DetectorError is not null, "unexpected source generation allowed input");
        }
    }
    private static async Task LeasesAsync()
    {
        foreach (bool failRelease in new[] { false, true })
        {
            await using var f = new Fixture(); await f.StartAsync();
            using var session = await f.SessionAsync();
            using var lease = await f.Arbiter.AcquireAsync(session.Owner, f.Arbiter.BindTarget(123), InputDeliveryMode.Win32PostMessage, () => { }, session.Token);
            var queued = f.Arbiter.AcquireAsync(session.Owner, f.Arbiter.BindTarget(123), InputDeliveryMode.Win32PostMessage, () => { }, session.Token);
            var cleanupEntered = Signal(); var release = Signal();
            async Task ActualNativeTask()
            {
                using var cancel = lease.Token.Register(() => cleanupEntered.TrySetResult());
                await cleanupEntered.Task;
                await release.Task; // noncooperative native task and required release still own lease
                if (failRelease) lease.CleanupFailed(new IOException("controlled release failure"));
                lease.Dispose(); session.Dispose();
            }
            var actual = ActualNativeTask();
            f.Source.Emit(Mouse(x: 1));
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!f.Monitor.PendingTakeover.IsCompleted && f.Arbiter.Snapshot is { State: InputControlState.TakingOver, Active: 1 },
                "candidate callback waited for cleanup or released live native work early");
            try { using var result = await queued; throw new Exception("queued input survived takeover"); } catch (OperationCanceledException) { }
            release.SetResult(); await actual; await f.Monitor.PendingTakeover;
            Check(f.Arbiter.Snapshot.State == (failRelease ? InputControlState.CleanupFailed : InputControlState.ManualControl), "cleanup status incorrect");
            Check((f.Monitor.Snapshot.CleanupError is not null) == failRelease && f.Monitor.Snapshot.DetectorError is null,
                "cleanup/source faults were conflated");
        }
    }
    private sealed class NoAnomaly : IAnomalyLogger
    { public Task<string> LogAnomalyAsync(TaskExecutionContext context, string reason, string detail) => Task.FromResult("unused"); }
    private static async Task GatewayLateAsync(bool detectorFailure = false)
    {
        await using var f = new Fixture(); await f.StartAsync();
        var native = new InputArbitrationTests.ControlledInput { HoldPress = true };
        string path = Path.Combine(AppContext.BaseDirectory, "intervention-gateway.lua");
        await File.WriteAllTextAsync(path, "function on_tick() end");
        try
        {
            var manifest = new PluginManifest([Permission.ControlInput]) { Id = "intervention-gateway", PluginDirectory = AppContext.BaseDirectory, TargetWindow = 123 };
            await using var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), native, NullLogger<LuaRuntime>.Instance, new FakeCoordinateMapper(), _ => true);
            runtime.ConfigureInputArbiter(f.Arbiter); TestAuthorizationHost.Approve(runtime.Authorization, manifest);
            await runtime.InitializeAsync(path, manifest);
            using var session = await f.Monitor.BeginSessionAsync(InputOwner.ForRun(runtime.BoundIdentity!));
            try
            {
                var caller = await runtime.Gateway.CreateContextAsync(manifest.Id, "mcp", AutomationSource.Mcp);
                var request = new OperationRequest(AutomationOperation.PressKey, JsonSerializer.SerializeToElement(new { key = 66, durationMs = 0 }),
                    Guid.NewGuid(), "candidate-late-native", OperationTarget.ForRun(runtime.BoundIdentity!), DateTimeOffset.UtcNow.AddMilliseconds(250));
                var resultTask = runtime.Gateway.ExecuteAsync(caller, request);
                await native.PressEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var result = await resultTask;
                Check(result.Status == OperationStatus.TimedOut && result.OutcomeUncertain && f.Arbiter.Snapshot.Active == 1, "Gateway timeout freed noncooperative input");
                var duplicate = await runtime.Gateway.ExecuteAsync(caller, request);
                Check(duplicate.Status == OperationStatus.TimedOut && native.PressCalls == 1, "retained operation was replayed");
                using var stop = f.Arbiter.RegisterStopHandler(async () =>
                { await runtime.DisposeAsync(); session.Dispose(); });
                // Dispose waits for actual input; the event callback must be able to return meanwhile.
                if (detectorFailure) f.Source.SetState(InputEventSourceState.Faulted);
                else f.Source.Emit(Mouse(x: 1));
                await Until(() => session.Token.IsCancellationRequested);
                Check(!f.Monitor.PendingTakeover.IsCompleted && f.Arbiter.Snapshot.State == InputControlState.TakingOver,
                    "candidate takeover handed control back before actual Gateway task completed");
                duplicate = await runtime.Gateway.ExecuteAsync(caller, request);
                Check(native.PressCalls == 1 && duplicate.Status != OperationStatus.Success, "candidate cancellation replayed retained operation");
                native.PressRelease.TrySetResult(); await f.Monitor.PendingTakeover.WaitAsync(TimeSpan.FromSeconds(3));
                Check(native.KeyUps == 1 && f.Arbiter.Snapshot is { State: InputControlState.ManualControl, Active: 0, Waiting: 0 }, "late input did not release before manual control");
                if (detectorFailure) await ExpectUnavailable(() => { f.Arbiter.ReturnControl(); return Task.CompletedTask; });
            }
            finally { native.PressRelease.TrySetResult(); }
        }
        finally { native.PressRelease.TrySetResult(); File.Delete(path); }
    }
    private static async Task SchedulerAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "intervention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var id in new[] { "input", "timer", "vision" })
            {
                string dir = Path.Combine(root, id); Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new
                { id, name = id, version = "1", targetProcess = "test", permissions = id == "input" ? new[] { "input", "timer" } : id == "vision" ? new[] { "vision.capture", "timer" } : new[] { "timer" } }));
                await File.WriteAllTextAsync(Path.Combine(dir, "main.lua"), "function on_tick() rug.sleep(60000) end");
            }
            await using var f = new Fixture(); await f.StartAsync();
            var auth = new PluginAuthorization(); TestAuthorizationHost.ApproveCatalog(auth, root);
            var captureGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool holdStart = false, failStart = false;
            int factories = 0;
            await using var scheduler = new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance), root,
                () => { factories++; if (failStart) throw new IOException("controlled factory failure"); return new FakeCapture { StartGate = holdStart ? captureGate : null }; },
                () => new FakeInput(), (c, i) => new LuaRuntime(c, new FakeOcr(), i, NullLogger<LuaRuntime>.Instance, new FakeCoordinateMapper(), _ => true),
                NullLogger<Scheduler>.Instance, new NoAnomaly(), authorization: auth, inputArbiter: f.Arbiter, intervention: f.Monitor);
            Guid timer = await scheduler.StartTaskAsync("timer", 0);
            Guid visual = await scheduler.StartTaskAsync("vision", 456);
            f.Source.Emit(Key()); f.Source.Emit(Key(released: true));
            Check(f.Monitor.Snapshot.Sessions == 0 && f.Arbiter.Snapshot.State == InputControlState.AutomationAvailable, "vision/timer registered input session");
            int before = factories;
            f.Source.SetState(InputEventSourceState.Stopped);
            await ExpectUnavailable(async () => await scheduler.StartTaskAsync("input", 123));
            Check(factories == before, "unready detector created underlying services");
            // A separate healthy host is required after a latched detector failure.
            await scheduler.DisposeAsync();
        }
        finally { Directory.Delete(root, true); }
        await SchedulerHealthyAsync();
    }
    private static async Task SchedulerHealthyAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "intervention-runs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (var id in new[] { "input", "timer" })
            {
                string dir = Path.Combine(root, id); Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new
                { id, name = id, version = "1", targetProcess = "test", permissions = id == "input" ? new[] { "input", "timer" } : new[] { "timer" } }));
                await File.WriteAllTextAsync(Path.Combine(dir, "main.lua"), "function on_tick() rug.sleep(60000) end");
            }
            await using var f = new Fixture(); await f.StartAsync();
            var auth = new PluginAuthorization(); TestAuthorizationHost.ApproveCatalog(auth, root);
            bool fail = false; TaskCompletionSource<bool>? gate = null;
            await using var scheduler = new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance), root,
                () => fail ? throw new IOException("controlled startup failure") : new FakeCapture { StartGate = gate },
                () => new FakeInput(), (c, i) => new LuaRuntime(c, new FakeOcr(), i, NullLogger<LuaRuntime>.Instance, new FakeCoordinateMapper(), _ => true),
                NullLogger<Scheduler>.Instance, new NoAnomaly(), authorization: auth, inputArbiter: f.Arbiter, intervention: f.Monitor);
            Guid timer = await scheduler.StartTaskAsync("timer", 0);
            fail = true;
            try { await scheduler.StartTaskAsync("input", 123); throw new Exception("factory failure ignored"); } catch (IOException) { }
            Check(f.Monitor.Snapshot.Sessions == 0, "failed startup retained input session"); fail = false;
            var a = await scheduler.StartTaskAsync("input", 123); var b = await scheduler.StartTaskAsync("input", 456);
            var paused = scheduler.Instances.Single(c => c.InstanceId == a);
            Check(scheduler.PauseTask(a), "input pause request failed before candidate takeover");
            await Until(() => paused.TaskStatus == AutomationTaskStatus.Paused);
            using var devtools = await f.SessionAsync();
            Check(f.Monitor.Snapshot.Sessions == 3 && f.Arbiter.Snapshot.Active == 0, "sessions failed to cover waiting periods/multiple instances/DevTools");
            f.Source.Emit(Mouse(x: 3)); await Until(() => devtools.Token.IsCancellationRequested); devtools.Dispose();
            await f.Monitor.PendingTakeover.WaitAsync(TimeSpan.FromSeconds(5));
            Check(scheduler.Instances.Count == 1 && scheduler.Instances.Single().InstanceId == timer, "global trigger stopped timer or retained input runs");
            Check(paused.TaskStatus == AutomationTaskStatus.Stopped, "candidate did not stop the Paused input instance");
            f.Arbiter.ReturnControl();
            Check(!scheduler.ResumeTask(a), "Return control revived a Paused input instance");
            var fresh = await scheduler.StartTaskAsync("input", 123);
            Check(fresh != a && fresh != b && scheduler.Instances.Count == 2, "return revived old instances");
            await scheduler.StopTaskAsync(fresh);
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var starting = scheduler.StartTaskAsync("input", 123);
            await Until(() => f.Monitor.Snapshot.Sessions == 1);
            f.Source.Emit(Key());
            try { await starting; throw new Exception("candidate allowed input startup"); } catch (OperationCanceledException) { }
            await f.Monitor.PendingTakeover.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Monitor.Snapshot.Sessions == 0 && scheduler.Instances.Single().InstanceId == timer, "starting input cleanup leaked session/run");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task PausedSourceFaultAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "paused-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string id in new[] { "input", "timer" })
            {
                string dir = Path.Combine(root, id); Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new
                { id, name = id, version = "1", targetProcess = "test", permissions = id == "input" ? new[] { "input", "timer" } : new[] { "timer" } }));
                await File.WriteAllTextAsync(Path.Combine(dir, "main.lua"), "function on_tick() rug.sleep(60000) end");
            }
            await using var f = new Fixture(); await f.StartAsync();
            var auth = new PluginAuthorization(); TestAuthorizationHost.ApproveCatalog(auth, root);
            await using var scheduler = new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance), root,
                () => new FakeCapture(), () => new FakeInput(),
                (c, i) => new LuaRuntime(c, new FakeOcr(), i, NullLogger<LuaRuntime>.Instance, ensureForeground: _ => true),
                NullLogger<Scheduler>.Instance, new NoAnomaly(), authorization: auth, inputArbiter: f.Arbiter, intervention: f.Monitor);
            var timer = await scheduler.StartTaskAsync("timer", 0);
            var input = await scheduler.StartTaskAsync("input", 123);
            var context = scheduler.Instances.Single(c => c.InstanceId == input);
            Check(scheduler.PauseTask(input), "source fault fixture pause failed");
            await Until(() => context.TaskStatus == AutomationTaskStatus.Paused);
            f.Source.SetState(InputEventSourceState.Faulted);
            await f.Monitor.PendingTakeover.WaitAsync(TimeSpan.FromSeconds(5));
            Check(context.TaskStatus == AutomationTaskStatus.Stopped && f.Monitor.Snapshot.Sessions == 0 &&
                scheduler.Instances.Single().InstanceId == timer && !scheduler.ResumeTask(input),
                "source fault revived Paused input or stopped timer");
            try { f.Arbiter.ReturnControl(); throw new Exception("unhealthy detector allowed return"); } catch (InputUnavailableException) { }
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Receiver : Rug.UI.Core.Native.IRawInputReceiver
    {
        public Action<InputObservation>? Receive;
        public readonly ManualResetEventSlim Stopped = new();
        public void Run(Action ready, Action<InputObservation> receive, CancellationToken token)
        { Receive = receive; ready(); Stopped.Wait(token); }
        public void RequestStop() => Stopped.Set();
        public Task SynchronizeAsync() => Task.CompletedTask;
    }
    private static async Task ProductionBarrierAsync()
    {
        var receiver = new Receiver();
        await using var source = new RawInputEventSource(() => receiver);
        var entered = Signal(); var release = new ManualResetEventSlim();
        source.InputReceived += (_, _) => { entered.TrySetResult(); release.Wait(); };
        await source.StartAsync(); receiver.Receive!(Key()); await entered.Task;
        var boundary = source.CaptureBoundaryAsync();
        Check(!boundary.IsCompleted, "source boundary did not wait for in-flight consumer");
        release.Set(); var result = await boundary.WaitAsync(TimeSpan.FromSeconds(3));
        Check(result.Sequence == 1 && result.Generation == source.Snapshot.Generation, "consumer boundary lost captured identity");
        await source.StopAsync();
        await ExpectUnavailable(async () => await source.CaptureBoundaryAsync());
    }
    private static async Task ProductionMotionAsync()
    {
        var receiver = new Receiver();
        var source = new RawInputEventSource(() => receiver);
        var arbiter = new InputArbiter(new TestInputWindows());
        var entered = Signal(); using var release = new ManualResetEventSlim();
        source.InputReceived += (_, item) =>
        { if (item.Observation.Kind == InputEventKind.DeviceArrived) { entered.TrySetResult(); release.Wait(); } };
        await using var monitor = new InputInterventionCoordinator(source, arbiter, NullLogger<InputInterventionCoordinator>.Instance, () => []);
        await monitor.StartAsync(); using var session = await monitor.BeginSessionAsync(InputOwner.ForDevTools("motion-host"));
        try
        {
            receiver.Receive!(new(InputEventKind.DeviceArrived, 1, false)); await entered.Task;
            receiver.Receive(Mouse(x: 5)); receiver.Receive(Mouse(x: -5)); receiver.Receive(Mouse());
            Check(source.Snapshot.Coalesced == 2, "motion samples were not merged deterministically");
            release.Set(); await Until(() => session.Token.IsCancellationRequested); session.Dispose();
            await monitor.PendingTakeover.WaitAsync(TimeSpan.FromSeconds(3));
            Check(arbiter.Snapshot.State == InputControlState.ManualControl, "zero-sum merged motion lost candidate activity");
        }
        finally { release.Set(); session.Dispose(); }
    }
    private static async Task ConsumerFaultAsync()
    {
        var receiver = new Receiver(); var source = new RawInputEventSource(() => receiver);
        var arbiter = new InputArbiter(new TestInputWindows());
        await using var monitor = new InputInterventionCoordinator(source, arbiter, NullLogger<InputInterventionCoordinator>.Instance, () => []);
        await monitor.StartAsync(); using var session = await monitor.BeginSessionAsync(InputOwner.ForDevTools("consumer-host"));
        source.InputReceived += (_, _) => throw new IOException("controlled consumer failure");
        receiver.Receive!(Key(software: true));
        await Until(() => session.Token.IsCancellationRequested); session.Dispose();
        await monitor.PendingTakeover.WaitAsync(TimeSpan.FromSeconds(3));
        Check(monitor.Snapshot.DetectorError is not null && monitor.Snapshot.CleanupError is null && source.Snapshot.ConsumerErrors == 1,
            "consumer reliability failure did not safely stop sessions");
        try { arbiter.ReturnControl(); throw new Exception("consumer failure bypassed by return control"); } catch (InputUnavailableException) { }
    }
}
