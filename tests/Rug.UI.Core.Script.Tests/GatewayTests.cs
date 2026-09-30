using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using Scheduler = Rug.UI.Core.Services.TaskScheduler;

internal static class GatewayTests
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "gateway-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string script = Path.Combine(root, "main.lua");
            await File.WriteAllTextAsync(script, "function on_tick() rug.sleep(1); local f=rug.capture(); assert(f.width==1) end");
            var manifest = new PluginManifest([Permission.Capture, Permission.Timer]) { Id = "gateway" };
            var checker = new DenyingChecker();
            var clock = new TestClock();
            var gateway = new AutomationGateway(NullLogger.Instance, checker,
                (id, _) => Task.FromResult(id == manifest.Id ? manifest : throw new KeyNotFoundException()), clock: clock);
            var first = new RunIdentity(manifest.Id, Guid.NewGuid(), 1, Guid.NewGuid(), 123);
            var second = new RunIdentity(manifest.Id, Guid.NewGuid(), 2, Guid.NewGuid(), 456);
            var captureA = new ControlledCapture(1);
            var captureB = new ControlledCapture(2);
            await using var runtimeA = Runtime(captureA, gateway, first);
            await using var runtimeB = Runtime(captureB, gateway, second);
            await runtimeA.InitializeAsync(script, manifest);
            await runtimeB.InitializeAsync(script, manifest);

            var ui = await gateway.CreateContextAsync(manifest.Id, "host-ui", AutomationSource.PluginUI);
            var agent = await gateway.CreateContextAsync(manifest.Id, "trusted-agent", AutomationSource.Agent);
            var mcp = await gateway.CreateContextAsync(manifest.Id, "trusted-mcp", AutomationSource.Mcp);
            var lua = await gateway.CreateContextAsync(manifest.Id, "lua-adapter", AutomationSource.Lua, first);
            OperationRequest Request(AutomationOperation type, RunIdentity run, object? args = null) =>
                new(type, JsonSerializer.SerializeToElement(args ?? new { }), Guid.NewGuid(), "gateway-test",
                    OperationTarget.ForRun(run), clock.GetUtcNow().AddSeconds(5));

            // Every adapter uses exactly the same policy and identity checks before service execution.
            checker.Deny = true;
            foreach (var caller in new[] { ui, agent, mcp, lua })
                Expect((await gateway.ExecuteAsync(caller, Request(AutomationOperation.Capture, first))).Error == OperationError.PermissionDenied,
                    "adapter bypassed replacement permission policy");
            checker.Deny = false;
            Expect(captureA.Calls == 0 && captureB.Calls == 0, "denial reached capture");
            var deniedIdentity = new RunIdentity(manifest.Id, Guid.NewGuid(), 3, null, 0);
            var deniedCapture = new ControlledCapture(1);
            await using (var deniedRuntime = Runtime(deniedCapture, gateway, deniedIdentity))
            {
                await deniedRuntime.InitializeAsync(script, manifest with { Permissions = [] });
                var forgedGrants = Request(AutomationOperation.Capture, deniedIdentity,
                    new { principal = "admin", source = "host", permissions = new[] { Permission.Capture } });
                Expect((await gateway.ExecuteAsync(agent, forgedGrants)).Error == OperationError.PermissionDenied && deniedCapture.Calls == 0,
                    "request-supplied grants overrode run permission");
            }
            var foreignGateway = new AutomationGateway(NullLogger.Instance,
                getPlugin: (_, _) => Task.FromResult(manifest));
            var foreignCaller = await foreignGateway.CreateContextAsync(manifest.Id, "host-ui", AutomationSource.PluginUI);
            Expect((await gateway.ExecuteAsync(foreignCaller, Request(AutomationOperation.Capture, first))).Error == OperationError.InvalidCaller,
                "foreign host capability accepted");
            var wrongGeneration = Request(AutomationOperation.Capture, first) with
                { Target = OperationTarget.ForRun(first) with { RunGeneration = 999 } };
            Expect((await gateway.ExecuteAsync(ui, wrongGeneration)).Error == OperationError.StaleRun, "stale run accepted");
            var wrongBinding = wrongGeneration with { Target = OperationTarget.ForRun(first) with { TargetBindingId = Guid.NewGuid() } };
            Expect((await gateway.ExecuteAsync(agent, wrongBinding)).Error == OperationError.BindingMismatch, "wrong binding accepted");
            var unknown = wrongGeneration with { Target = OperationTarget.ForRun(first) with { InstanceId = Guid.NewGuid() } };
            Expect((await gateway.ExecuteAsync(mcp, unknown)).Error == OperationError.InstanceNotFound, "unknown instance accepted");
            var wrongPlugin = unknown with { Target = OperationTarget.ForRun(first) with { PluginId = "forged" } };
            Expect((await gateway.ExecuteAsync(agent, wrongPlugin)).Error == OperationError.InvalidCaller, "forged plugin accepted");
            Expect((await gateway.ExecuteAsync(lua, Request(AutomationOperation.Capture, second))).Error == OperationError.InvalidCaller,
                "bound Lua caller accessed another instance");
            var expired = Request(AutomationOperation.Capture, first) with { Deadline = clock.GetUtcNow().AddSeconds(-1) };
            Expect((await gateway.ExecuteAsync(ui, expired)) is { Status: OperationStatus.TimedOut, OutcomeUncertain: false }, "expired work started");
            Expect(captureA.Calls == 0, "invalid identity/expired work reached service");
            using (var cancelledBeforeStart = new CancellationTokenSource())
            {
                cancelledBeforeStart.Cancel();
                Expect((await gateway.ExecuteAsync(ui, Request(AutomationOperation.Capture, first), cancelledBeforeStart.Token)) is
                    { Status: OperationStatus.Cancelled, OutcomeUncertain: false }, "pre-cancelled work started");
            }
            var unsupported = Request((AutomationOperation)999, first);
            Expect((await gateway.ExecuteAsync(mcp, unsupported)).Error == OperationError.UnsupportedOperation, "unsupported operation accepted");
            var invalid = Request(AutomationOperation.Sleep, first, new { ms = -1 });
            Expect((await gateway.ExecuteAsync(ui, invalid)).Error == OperationError.InvalidParameters, "bad timer parameters accepted");

            var duplicate = Request(AutomationOperation.Capture, first);
            captureA.Hold = true;
            var pending = Enumerable.Range(0, 20).Select(_ => Task.Run(() => gateway.ExecuteAsync(agent, duplicate))).ToArray();
            await UntilAsync(() => captureA.Calls == 1);
            var conflict = duplicate with { Parameters = JsonSerializer.SerializeToElement(new { forgedPermissions = new[] { "input" } }) };
            Expect((await gateway.ExecuteAsync(agent, conflict)).Error == OperationError.DeduplicationConflict, "conflicting duplicate accepted");
            captureA.Release();
            var results = await Task.WhenAll(pending);
            Expect(results.All(result => result.Status == OperationStatus.Success && ((CapturedFrameMetadata)result.Value!).Width == 1) && captureA.Calls == 1,
                "concurrent duplicates executed repeatedly");
            Expect((await gateway.ExecuteAsync(agent, duplicate)).Status == OperationStatus.Success && captureA.Calls == 1, "completed duplicate executed again");
            Expect(((CapturedFrameMetadata)(await gateway.ExecuteAsync(mcp, Request(AutomationOperation.Capture, second))).Value!).Width == 2 && captureB.Calls == 1,
                "service/result crossed instance boundary");

            // The noncooperative capture ignores cancellation. The terminal result must stay terminal.
            captureA.ResetHold();
            var timeoutRequest = Request(AutomationOperation.Capture, first) with { Deadline = clock.GetUtcNow().AddMilliseconds(60) };
            OperationResult timeout = await gateway.ExecuteAsync(ui, timeoutRequest);
            Expect(timeout is { Status: OperationStatus.TimedOut, Error: OperationError.DeadlineExceeded, OutcomeUncertain: true }, "in-flight timeout mapping");
            captureA.Release();
            Expect(await gateway.ExecuteAsync(ui, timeoutRequest) == timeout && captureA.Calls == 2, "late result retried/replaced timeout");

            captureA.ResetHold();
            using (var cancel = new CancellationTokenSource())
            {
                var cancelRequest = Request(AutomationOperation.Capture, first);
                int previousCalls = captureA.Calls;
                Task<OperationResult> cancelling = gateway.ExecuteAsync(mcp, cancelRequest, cancel.Token);
                await UntilAsync(() => captureA.Calls > previousCalls);
                cancel.Cancel();
                Expect((await cancelling) is { Status: OperationStatus.Cancelled, OutcomeUncertain: true }, "in-flight cancellation mapping");
                captureA.Release();
                Expect((await gateway.ExecuteAsync(mcp, cancelRequest)).Status == OperationStatus.Cancelled, "cancelled duplicate retried");
            }

            using (var blockedPrefix = new ManualResetEventSlim())
            {
                captureB.BlockingPrefix = blockedPrefix;
                try
                {
                    var blockingRequest = Request(AutomationOperation.Capture, second) with { Deadline = clock.GetUtcNow().AddMilliseconds(60) };
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    Task<OperationResult> blocking = gateway.ExecuteAsync(ui, blockingRequest);
                    Expect(stopwatch.ElapsedMilliseconds < 100, "synchronous service prefix blocked gateway caller");
                    Expect((await blocking.WaitAsync(TimeSpan.FromSeconds(1))) is { Status: OperationStatus.TimedOut, OutcomeUncertain: true },
                        "synchronous noncooperative work blocked timeout delivery");
                    Expect((await gateway.ExecuteAsync(ui, Request(AutomationOperation.Sleep, second, new { ms = 0 }))).Status == OperationStatus.Success,
                        "blocked service held gateway registry lock");
                }
                finally { blockedPrefix.Set(); captureB.BlockingPrefix = null; }
                await UntilAsync(() => gateway.ActiveWorkCount == 0);
            }

            captureA.ResetHold();
            Task<OperationResult> stopping = gateway.ExecuteAsync(agent, Request(AutomationOperation.Capture, first));
            runtimeA.Stop();
            Expect((await stopping).Status == OperationStatus.Cancelled, "instance stop did not cancel request");
            captureA.Release();
            int calls = captureA.Calls;
            Expect((await gateway.ExecuteAsync(ui, Request(AutomationOperation.Capture, first))).Error == OperationError.InstanceNotFound && captureA.Calls == calls,
                "stopped instance reached service");
            Expect(gateway.RegisteredRunCount == 1, "stopped run registration leaked");

            // Actual Lua yields traverse the shared gateway, and permission claims in data have no authority.
            var luaIdentity = new RunIdentity(manifest.Id, Guid.NewGuid(), 3, null, 0);
            await using (var luaRuntime = Runtime(new ControlledCapture(1), gateway, luaIdentity))
            {
                await luaRuntime.InitializeAsync(script, manifest);
                Expect((await luaRuntime.StepAsync()).State == ScriptState.Yielded, "real Lua sleep did not yield");
                Expect((await luaRuntime.ResumeAsync()).State == ScriptState.Yielded, "real Lua capture did not yield");
                Expect((await luaRuntime.ResumeAsync()).State == ScriptState.Stopped, "real Lua gateway values failed");
                Expect((await gateway.ExecuteAsync(ui, Request(AutomationOperation.Capture, luaIdentity))).Error == OperationError.InstanceNotFound,
                    "completed one-shot runtime accepted service requests");
            }
            Expect(gateway.Records.Any(record => record.Source == AutomationSource.Lua && record.Operation == AutomationOperation.Capture && record.Status == OperationStatus.Success),
                "real Lua bypassed operation records");
            string invalidScript = Path.Combine(root, "invalid.lua");
            await File.WriteAllTextAsync(invalidScript, "function on_tick() rug.click('invalid', 2) end");
            var badIdentity = new RunIdentity(manifest.Id, Guid.NewGuid(), 4, null, 0);
            await using (var invalidRuntime = Runtime(new ControlledCapture(1), gateway, badIdentity))
            {
                await invalidRuntime.InitializeAsync(invalidScript, manifest);
                Expect((await invalidRuntime.StepAsync()).State == ScriptState.Faulted && gateway.Records.Any(record =>
                    record.Target.InstanceId == badIdentity.InstanceId && record.Error == OperationError.InvalidParameters),
                    "invalid Lua arguments bypassed gateway error mapping/records");
            }
            captureB.Fail = true;
            Expect((await gateway.ExecuteAsync(agent, Request(AutomationOperation.Capture, second))) is
                { Status: OperationStatus.Failed, Error: OperationError.ExecutionFailed, Value: null }, "service failure was not sanitized");
            captureB.Fail = false;
            while (gateway.RetainedOperationCount < AutomationGateway.Capacity)
                await gateway.ExecuteAsync(ui, Request(AutomationOperation.Sleep, second, new { ms = 0 }));
            Expect((await gateway.ExecuteAsync(ui, Request(AutomationOperation.Capture, second))).Error == OperationError.CapacityExceeded,
                "deduplication capacity did not apply backpressure");
            clock.Advance(AutomationGateway.Retention + TimeSpan.FromSeconds(1));
            Expect(gateway.RetainedOperationCount == 0, "completed entries did not expire");
            captureB.ResetHold();
            var pinned = Request(AutomationOperation.Capture, second) with { Deadline = clock.GetUtcNow().AddMilliseconds(20) };
            var pinnedResult = await gateway.ExecuteAsync(agent, pinned);
            clock.Advance(AutomationGateway.Retention + TimeSpan.FromSeconds(1));
            Expect(gateway.RetainedOperationCount == 1 && gateway.ActiveWorkCount == 1 &&
                await gateway.ExecuteAsync(agent, pinned) == pinnedResult, "in-flight timeout entry expired and allowed duplicate execution");
            captureB.Release();
            await UntilAsync(() => gateway.ActiveWorkCount == 0);
            Expect(gateway.RetainedOperationCount == 0, "settled late work did not release expired entry");
            runtimeB.Stop();
            Expect(gateway.RegisteredRunCount == 0 && gateway.RetainedOperationCount == 0, "ended run operation records leaked");

            for (int i = 0; i < 30; i++)
            {
                var identity = new RunIdentity(manifest.Id, Guid.NewGuid(), i + 3, null, 0);
                await using var runtime = Runtime(new ControlledCapture(1), gateway, identity);
                await runtime.InitializeAsync(script, manifest);
                await gateway.ExecuteAsync(ui, Request(AutomationOperation.Capture, identity));
                runtime.Stop();
            }
            Expect(gateway.RegisteredRunCount == 0 && gateway.RetainedOperationCount == 0, "repeated runs accumulated entries");
            for (int i = 0; i < AutomationGateway.RecordCapacity * 2; i++) await gateway.ExecuteAsync(ui, unknown);
            Expect(gateway.Records.Count == AutomationGateway.RecordCapacity, "audit buffer exceeded bound");

            await TestLifecycleAsync(root);
            await TestLateLuaAsync(root);
            await GatewayRepairTests.RunAsync(root);
            Console.WriteLine("Automation gateway identity, adapters, deduplication, cancellation and lifecycle tests passed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static LuaRuntime Runtime(ICaptureService capture, AutomationGateway gateway, RunIdentity identity)
    {
        var runtime = new LuaRuntime(capture, new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance);
        runtime.ConfigureGateway(gateway);
        runtime.ConfigureState(identity, new Dictionary<string, object?>(), new Dictionary<string, object?>());
        return runtime;
    }
    private static async Task TestLifecycleAsync(string root)
    {
        string plugin = Path.Combine(root, "life"); Directory.CreateDirectory(plugin);
        await File.WriteAllTextAsync(Path.Combine(plugin, "manifest.json"), """
            {"id":"life","name":"Lifecycle","version":"1.0.0","targetProcess":"demo","permissions":["timer"]}
            """);
        await File.WriteAllTextAsync(Path.Combine(plugin, "main.lua"), "function on_tick() rug.sleep(20) end");
        await using var scheduler = new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance), root,
            () => new ControlledCapture(1), () => new FakeInput(),
            (capture, input) => new LuaRuntime(capture, new FakeOcr(), input, NullLogger<LuaRuntime>.Instance),
            NullLogger<Scheduler>.Instance, new NoAnomaly());
        var caller = await scheduler.Gateway.CreateContextAsync("life", "ui", AutomationSource.PluginUI);
        OperationRequest Request(AutomationOperation operation, OperationTarget target) =>
            new(operation, JsonSerializer.SerializeToElement(new { }), Guid.NewGuid(), "lifecycle", target, DateTimeOffset.UtcNow.AddSeconds(5));
        var start = Request(AutomationOperation.Start, new("life"));
        var launches = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => scheduler.Gateway.ExecuteAsync(caller, start)));
        var identity = (RunIdentity)launches[0].Value!;
        Expect(launches.All(result => Equals(result.Value, identity)) && scheduler.Instances.Count == 1, "duplicate start created multiple runs");
        Expect((await scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.Pause, OperationTarget.ForRun(identity)))).Status == OperationStatus.Success &&
            scheduler.Instances.Single().TaskStatus == AutomationTaskStatus.Paused, "gateway pause failed");
        Expect((await scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.Resume, OperationTarget.ForRun(identity)))).Status == OperationStatus.Success,
            "gateway resume failed");
        Expect((await scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.Stop, OperationTarget.ForRun(identity)))).Status == OperationStatus.Success &&
            scheduler.Instances.Count == 0, "gateway stop failed");
        // Start entries have no target instance; they are capacity/TTL bounded rather than run-evicted.
        Expect(scheduler.Gateway.RegisteredRunCount == 0 && scheduler.Gateway.RetainedOperationCount == 1, "lifecycle records leaked");
    }
    private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task TestLateLuaAsync(string root)
    {
        string script = Path.Combine(root, "late.lua");
        await File.WriteAllTextAsync(script, "function on_tick() rug.capture(); rug.set_state('late', true) end");
        var manifest = new PluginManifest([Permission.Capture]) { Id = "late" };
        var gateway = new AutomationGateway(NullLogger.Instance);
        using var bus = new CommandBus(new StateStore());
        var capture = new ControlledCapture(1) { Hold = true };
        var old = new RunIdentity("late", Guid.NewGuid(), 1, null, 0);
        await using var runtime = new LuaRuntime(capture, new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance, commandBus: bus);
        runtime.ConfigureGateway(gateway);
        runtime.ConfigureState(old, new Dictionary<string, object?>(), new Dictionary<string, object?>());
        await runtime.InitializeAsync(script, manifest);
        Expect((await runtime.StepAsync()).State == ScriptState.Yielded, "late Lua capture did not yield");
        await UntilAsync(() => capture.Calls == 1);
        Task<ScriptExecutionResult> pending = runtime.ResumeAsync();
        runtime.Stop();
        Expect((await pending.WaitAsync(TimeSpan.FromSeconds(1))).State == ScriptState.Stopped, "noncooperative capture blocked Lua stop");
        var fresh = new RunIdentity("late", Guid.NewGuid(), 2, null, 0);
        bus.OpenInstance(fresh.InstanceScope);
        bus.SetState(fresh.InstanceScope, "late", false);
        capture.Release();
        await UntilAsync(() => gateway.ActiveWorkCount == 0);
        Expect(!bus.Snapshot(old.InstanceScope).ContainsKey("late") &&
            !bus.GetState(fresh.InstanceScope, "late", true) && runtime.State == ScriptState.Stopped,
            "late capture revived old Lua or overwrote fresh run state");
        bus.CloseInstance(fresh.InstanceScope);
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private sealed class DenyingChecker : IAutomationPermissionChecker
    {
        public bool Deny { get; set; }
        public bool IsAllowed(AutomationCallContext caller, IReadOnlyList<string> permissions, AutomationOperation operation) =>
            !Deny && new ManifestAutomationPermissionChecker().IsAllowed(caller, permissions, operation);
    }
    private sealed class TestClock : TimeProvider
    {
        private TimeSpan _offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;
        public void Advance(TimeSpan duration) => _offset += duration;
    }
    private sealed class NoAnomaly : IAnomalyLogger
    {
        public Task<string> LogAnomalyAsync(TaskExecutionContext context, string reason, string detail) => throw new NotSupportedException();
    }
    private sealed class ControlledCapture(int width) : ICaptureService
    {
        public int Width { get; set; } = width;
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Hold { get; set; }
        public bool Fail { get; set; }
        public ManualResetEventSlim? BlockingPrefix { get; set; }
        private TaskCompletionSource<CapturedFrame?> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsCapturing => true;
        public Task StartAsync(nint hwnd, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            BlockingPrefix?.Wait();
            if (Fail) return Task.FromException<CapturedFrame?>(new InvalidOperationException("Internal private diagnostic"));
            return Hold ? _release.Task : Task.FromResult<CapturedFrame?>(Frame());
        }
        private CapturedFrame Frame() => new(new byte[Width * 4], Width, 1, Width * 4);
        public void Release() { Hold = false; _release.TrySetResult(Frame()); }
        public void ResetHold() { _release = new(TaskCreationOptions.RunContinuationsAsynchronously); Hold = true; }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
