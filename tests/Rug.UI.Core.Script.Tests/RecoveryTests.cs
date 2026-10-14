using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using Scheduler = Rug.UI.Core.Services.TaskScheduler;

internal static class RecoveryTests
{
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); } }
    private static Task Bound(Task task) => task.WaitAsync(TimeSpan.FromSeconds(8));
    private const string RecoveryScript = """
        local once = false
        function recover()
          local retained = 42
          rug.set_state('before', (rug.get_state('before') or 0) + 1)
          local r = rug.agent.resolve_anomaly('controlled-ready', 'bounded business context')
          assert(type(r) == 'table' and r.status == 'resolved' and type(r.recoveryId) == 'string')
          assert(retained == 42 and rug.get_state('before') == 1)
          rug.set_state('after', (rug.get_state('after') or 0) + 1)
        end
        function on_init() INIT end
        function on_tick() TICK rug.sleep(60000) end
        function on_stop() rug.log('recovery_stop_once') end
        """;
    internal static string Script(bool init) => RecoveryScript.Replace("INIT", init ? "recover()" : "")
        .Replace("TICK", init ? "" : "if not once then once = true; recover() end");

    public static async Task RunAsync()
    {
        foreach (bool init in new[] { true, false }) await PreservedAsync(init);
        foreach (RecoveryStatus status in Enum.GetValues<RecoveryStatus>()) await AdviceAsync(status);
        await MissingAndRejectedAsync(); await RepeatedCoroutineAsync(); await ConcurrentAsync(); await TwoRunsAsync();
        foreach (string stage in new[] { "human", "attempt", "validation", "native" }) await StopAsync(stage);
        foreach (string failure in new[] { "lua", "permission", "archive", "cleanup", "validator" }) await FaultsAsync(failure);
        await IdentityAsync(); await PlannedOthersAsync(); await PlannedHumanAsync(); await UnexpectedAsync(); await LateEpochAsync();
        await TimeoutAsync(); await NativeTimeoutAsync(); await CallerCancellationAsync(); await RepetitionAsync();
        Console.WriteLine("Recovery PASS: real Lua init/tick locals, explicit tables, frozen mainline, trusted validation, dedup, Stop/late work, grants/identity, planned handoff and epoch isolation");
    }
    private static async Task PreservedAsync(bool init)
    {
        await using var f = await Fixture.CreateAsync(Script(init));
        var c = await f.StartAsync(); await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman || c.IsBusinessClosed);
        Check(c.TaskStatus == AutomationTaskStatus.AwaitingHuman, "recovery wait failed: " + c.LastError);
        Check(f.Read(c, "before") == 1 && f.Read(c, "after") == 0 && !f.Scheduler.PauseTask(c.InstanceId) && !f.Scheduler.ResumeTask(c.InstanceId), "human wait advanced or ordinary Resume bypassed recovery");
        var recovery = c.Snapshot.Recovery!;
        var denied = await f.Operation(c, AutomationOperation.PressKey, new { key = 66 });
        Check(denied.Error == OperationError.InvalidLifecycleState && f.Inputs[0].KeyDownCalls == 0, "recovery admitted ordinary input");
        f.Registry.Register(f.Installation, "controlled-ready", new Validator());
        var result = await f.Verify(c);
        Check(result is { Status: OperationStatus.Success, Value: RecoveryResult { Status: RecoveryStatus.Resolved } }, "explicit verification failed");
        await Until(() => f.Read(c, "after") == 1);
        Check(c.Snapshot.Recovery is { Phase: RecoveryPhase.Resolved } && c.Snapshot.Revision > recovery.Generation && f.Read(c, "before") == 1, "coroutine restarted or revision lost");
    }
    private static async Task AdviceAsync(RecoveryStatus status)
    {
        var executor = new Executor(status);
        await using var f = await Fixture.CreateAsync(Script(true), executor: executor);
        var validator = new Validator(); f.Registry.Register(f.Installation, "controlled-ready", validator);
        var c = await f.StartAsync();
        if (status == RecoveryStatus.Resolved)
        { await Until(() => f.Read(c, "after") == 1); Check(validator.Calls == 1, "executor bypassed validation"); }
        else if (status == RecoveryStatus.Failed)
        { await Bound(c.Completion); Check(c.TaskStatus == AutomationTaskStatus.Faulted && f.Read(c, "after") == 0, "failed suggestion resumed mainline"); }
        else
        { await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman); Check(validator.Calls == 0 && f.Read(c, "after") == 0, "needs_human returned to Lua"); }
        Check(executor.Calls == 1, "executor ran more than once");
    }
    private static async Task MissingAndRejectedAsync()
    {
        await using var f = await Fixture.CreateAsync(Script(true), executor: new Executor(RecoveryStatus.Resolved));
        var c = await f.StartAsync(); await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman);
        Check(c.Snapshot.Recovery!.Result.Error == RecoveryError.NoValidator && f.Read(c, "after") == 0, "missing validator defaulted success");
        var validator = new Validator { Valid = false }; f.Registry.Register(f.Installation, "controlled-ready", validator);
        await f.Verify(c); Check(c.TaskStatus == AutomationTaskStatus.AwaitingHuman && f.Read(c, "after") == 0, "false validation resumed Lua");
        validator.Valid = true; await f.Verify(c); await Until(() => f.Read(c, "after") == 1);
        Check(validator.Calls == 2, "validation retry replayed advice");
    }
    private static async Task RepeatedCoroutineAsync()
    {
        await using var f = await Fixture.CreateAsync("""
            function on_init()
              local retained = 42
              rug.press_key(66,1)
              local a = rug.agent.resolve_anomaly('controlled-ready','first')
              rug.press_key(66,1)
              local b = rug.agent.resolve_anomaly('controlled-ready','second')
              assert(a.status == 'resolved' and b.status == 'resolved' and a.recoveryId ~= b.recoveryId and retained == 42)
              rug.press_key(66,1)
              rug.set_state('after',1)
            end
            function on_tick() rug.sleep(60000) end
            """, input: true, executor: new Executor(RecoveryStatus.Resolved));
        var validator = new Validator(); f.Registry.Register(f.Installation, "controlled-ready", validator);
        var c = await f.StartAsync(); await Until(() => f.Read(c, "after") == 1);
        Check(c.Snapshot.Recovery!.Generation == 2 && validator.Calls == 2 && f.Inputs[0].KeyDownCalls == 3 &&
            f.Inputs[0].KeyUpCalls == 3 && f.Coordinator.ActiveRecoveries == 0 && f.Monitor.Snapshot.Sessions == 1,
            "same coroutine recovery generations leaked, replaced locals or replayed completed input");
    }
    private static async Task ConcurrentAsync()
    {
        await using var f = await Fixture.CreateAsync(Script(false));
        var v = new Validator { Hold = true }; f.Registry.Register(f.Installation, "controlled-ready", v);
        var c = await f.StartAsync(); await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman);
        var caller = await f.Caller(c); var request = f.Request(c, AutomationOperation.VerifyRecovery, new { recoveryId = c.Snapshot.Recovery!.RecoveryId });
        var a = f.Scheduler.Gateway.ExecuteAsync(caller, request); await Bound(v.Entered.Task);
        var b = f.Scheduler.Gateway.ExecuteAsync(caller, request); var other = f.Verify(c);
        Check(v.Calls == 1 && !f.Scheduler.ResumeTask(c.InstanceId), "parallel verify/ordinary resume escaped gate");
        v.Release.TrySetResult(); await Bound(Task.WhenAll(a, b, other));
        await Until(() => f.Read(c, "after") == 1);
        Check(v.Calls == 1 && await f.Scheduler.Gateway.ExecuteAsync(caller, request) == await a, "dedup repeated validation");
    }
    private static async Task TwoRunsAsync()
    {
        await using var f = await Fixture.CreateAsync(Script(true));
        f.Registry.Register(f.Installation, "controlled-ready", new Validator());
        var a = await f.StartAsync(); var b = await f.StartAsync();
        await Until(() => a.TaskStatus == AutomationTaskStatus.AwaitingHuman && b.TaskStatus == AutomationTaskStatus.AwaitingHuman);
        Check(a.Snapshot.Recovery!.RecoveryId != b.Snapshot.Recovery!.RecoveryId, "recovery ids shared");
        await f.Verify(a); await Until(() => f.Read(a, "after") == 1);
        Check(f.Read(b, "after") == 0 && b.TaskStatus == AutomationTaskStatus.AwaitingHuman, "cross-instance continuation");
        await f.Scheduler.StopTaskAsync(b.InstanceId); Check(a.TaskStatus == AutomationTaskStatus.Running, "other recovery Stop crossed instances");
    }
    private static async Task StopAsync(string stage)
    {
        var executor = stage == "attempt" ? new Executor(RecoveryStatus.Resolved) { Hold = true } : null;
        await using var f = await Fixture.CreateAsync(Script(true), executor: executor);
        var v = new Validator { Hold = stage == "validation", Capture = stage == "native" };
        f.Registry.Register(f.Installation, "controlled-ready", v);
        var c = await f.StartAsync();
        Task<OperationResult>? verify = null;
        if (stage == "attempt") await Bound(executor!.Entered.Task);
        else
        {
            await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman);
            if (stage != "human")
            {
                f.Captures[0].Hold = stage == "native";
                verify = f.Verify(c);
                await Bound(stage == "native" ? f.Captures[0].Entered.Task : v.Entered.Task);
            }
        }
        var stop = f.Scheduler.StopTaskAsync(c.InstanceId);
        if (stage == "native")
        { Check(!stop.IsCompleted && c.TaskStatus == AutomationTaskStatus.Stopping && !f.Captures[0].Disposed, "native capture disposed before real completion"); f.Captures[0].Release.TrySetResult(); }
        await Bound(stop); v.Release.TrySetResult(); executor?.Release.TrySetResult();
        if (verify is not null) await Bound(verify);
        await Until(() => f.Coordinator.ActiveRecoveries == 0 && f.Coordinator.AdviceInFlight == 0);
        Check(c.TaskStatus == AutomationTaskStatus.Stopped && c.LastError is null && f.Logger.Stops == 1 && f.Read(c, "after") == 0, "late recovery revived/faulted Stop");
    }
    private static async Task FaultsAsync(string failure)
    {
        string script = failure == "lua" ? "function on_init() error('genuine') end" : Script(true);
        await using var f = await Fixture.CreateAsync(script, agent: failure != "permission");
        f.Archive.Fail = failure == "archive";
        if (failure == "validator") f.Registry.Register(f.Installation, "controlled-ready", new Validator { Fail = true });
        var c = await f.StartAsync();
        if (failure is "cleanup" or "validator")
        {
            await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman);
            if (failure == "cleanup") { f.Captures[0].FailCleanup = true; await f.Scheduler.StopTaskAsync(c.InstanceId); }
            else Check((await f.Verify(c)) is { Status: OperationStatus.Failed, Value: RecoveryResult { Status: RecoveryStatus.Failed } }, "validator fault mapped to normal cancellation");
        }
        await Bound(c.Completion);
        Check(c.TaskStatus == AutomationTaskStatus.Faulted && c.LastError is not null && f.Read(c, "after") == 0, $"{failure} swallowed as recovery success/cancellation");
    }
    private static async Task IdentityAsync()
    {
        foreach (string mode in new[] { "id", "generation", "binding", "caller", "revocation", "window", "reload" })
        {
            await using var f = await Fixture.CreateAsync(Script(true));
            var v = new Validator(); f.Registry.Register(f.Installation, "controlled-ready", v);
            var c = await f.StartAsync(); await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman);
            var caller = await f.Caller(c, mode == "caller" ? AutomationSource.Agent : AutomationSource.PluginUI);
            var request = f.Request(c, AutomationOperation.VerifyRecovery, new { recoveryId = mode == "id" ? Guid.NewGuid() : c.Snapshot.Recovery!.RecoveryId });
            if (mode == "generation") request = request with { Target = request.Target with { RunGeneration = c.RunGeneration + 1 } };
            if (mode == "binding") request = request with { Target = request.Target with { TargetBindingId = Guid.NewGuid() } };
            if (mode == "revocation") f.Auth.Revoke(f.Installation, [Permission.Agent]);
            if (mode == "window") f.Windows.Valid = false;
            if (mode == "reload") { await f.Scheduler.StopTaskAsync(c.InstanceId); await f.Scheduler.RefreshPluginsAsync(); }
            var result = await f.Scheduler.Gateway.ExecuteAsync(caller, request);
            Check(result.Status != OperationStatus.Success && v.Calls == 0 && f.Read(c, "after") == 0, $"{mode} bypassed recovery identity/authority");
            if (mode == "window") { await Bound(c.Completion); Check(c.TaskStatus == AutomationTaskStatus.Faulted, "irreversible target loss did not fault recovery"); }
        }
    }
    private static async Task PlannedOthersAsync()
    {
        string script = Script(true).Replace("function on_init() recover() end",
            "function on_init() if rug.get_state('recoverNow') then recover() else rug.set_state('alive',1); rug.sleep(60000) end end");
        await using var f = await Fixture.CreateAsync(script, input: true);
        string directory = Path.Combine(f.Root, "visual"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        { id = "visual", name = "Visual", version = "1", targetProcess = "test", permissions = new[] { "vision.capture", "timer" } }));
        await File.WriteAllTextAsync(Path.Combine(directory, "main.lua"), "function on_init() rug.sleep(60000) end");
        TestAuthorizationHost.ApproveCatalog(f.Auth, f.Root); await f.Scheduler.RefreshPluginsAsync();
        var visualId = await f.Scheduler.StartTaskAsync("visual", 123);
        var visual = f.Scheduler.Instances.Single(c => c.InstanceId == visualId);
        var other = await f.StartAsync(); await Until(() => f.Read(other, "alive") == 1);
        var id = await f.Scheduler.StartTaskAsync("recovery", 123, new Dictionary<string, object?> { ["recoverNow"] = true });
        var recovering = f.Scheduler.Instances.Single(c => c.InstanceId == id);
        await Until(() => recovering.TaskStatus == AutomationTaskStatus.AwaitingHuman);
        Check(other.TaskStatus == AutomationTaskStatus.Stopped && !visual.IsBusinessClosed &&
            !recovering.Cancellation.IsCancellationRequested && f.Monitor.Snapshot.Sessions == 0,
            "planned global handoff preserved an unregistered input run or stopped pure vision");
    }
    private static async Task PlannedHumanAsync()
    {
        await using var f = await Fixture.CreateAsync(Script(true), input: true);
        var c = await f.StartAsync(); await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman);
        Check(f.Arbiter.Snapshot.State == InputControlState.ManualControl && !c.Cancellation.IsCancellationRequested && f.Monitor.Snapshot.Sessions == 0, "planned handoff cancelled VM or retained input session");
        f.Registry.Register(f.Installation, "controlled-ready", new Validator());
        var blocked = await f.Verify(c);
        Check(blocked.Value is RecoveryResult { Status: RecoveryStatus.NeedsHuman } && f.Read(c, "after") == 0, "verification skipped explicit Return");
        f.Arbiter.ReturnControl(); Check(f.Read(c, "after") == 0, "Return resumed VM");
        await f.Verify(c); await Until(() => f.Read(c, "after") == 1);
        Check(f.Monitor.Snapshot.Sessions == 1 && !c.InputEpochToken.IsCancellationRequested, "fresh input session not obtained");
        f.Source.Emit(new(InputEventKind.Keyboard, 1, false, 65, 30));
        await Bound(c.Completion);
        Check(c.TaskStatus == AutomationTaskStatus.Stopped && f.Monitor.Snapshot.Sessions == 0, "unplanned human event after recovery failed to stop instance");
    }
    private static async Task UnexpectedAsync()
    {
        var executor = new Executor(RecoveryStatus.Resolved) { Hold = true };
        await using var f = await Fixture.CreateAsync(Script(true), input: true, executor: executor);
        var c = await f.StartAsync(); await Bound(executor.Entered.Task);
        f.Source.Emit(new(InputEventKind.Mouse, 2, false, X: 1));
        await Bound(c.Completion); executor.Release.TrySetResult();
        Check(c.TaskStatus == AutomationTaskStatus.Stopped && !c.PreservedForHuman, "unexpected takeover preserved recovering run");
    }
    private static async Task LateEpochAsync()
    {
        await using var f = await Fixture.CreateAsync(Script(true), input: true);
        var c = await f.StartAsync(); await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman);
        var stale = f.Source.Capture(new(InputEventKind.Mouse, 2, false, X: 2));
        f.Registry.Register(f.Installation, "controlled-ready", new Validator());
        f.Arbiter.ReturnControl(); await f.Verify(c); await Until(() => f.Read(c, "after") == 1);
        f.Source.Deliver(stale);
        Check(!c.IsBusinessClosed && !c.Cancellation.IsCancellationRequested, "delayed pre-session event stopped new input epoch");
    }
    private static async Task TimeoutAsync()
    {
        var executor = new Executor(RecoveryStatus.Resolved) { Hold = true };
        await using var f = await Fixture.CreateAsync(Script(true), executor: executor,
            options: new(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(80)));
        var c = await f.StartAsync(); await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman);
        Check(c.Snapshot.Recovery!.Result.Error == RecoveryError.AttemptTimedOut, "attempt timeout lost");
        executor.Release.TrySetResult(); await Until(() => f.Coordinator.AdviceInFlight == 0);
        Check(f.Read(c, "after") == 0, "late advice resumed coroutine");
        var v = new Validator { Hold = true }; f.Registry.Register(f.Installation, "controlled-ready", v);
        await f.Verify(c); Check(c.Snapshot.Recovery!.Result.Error == RecoveryError.ValidationTimedOut, "verification timeout not visible");
        await f.Verify(c); Check(v.Calls == 1, "noncooperative validation ran in parallel after timeout");
        v.Release.TrySetResult(); await Until(() => f.Coordinator.AdviceInFlight == 0);
        Check(f.Read(c, "after") == 0, "late validation resumed coroutine");
    }
    private static async Task NativeTimeoutAsync()
    {
        await using var f = await Fixture.CreateAsync(Script(true), options: new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(80)));
        var v = new Validator { Capture = true }; f.Registry.Register(f.Installation, "controlled-ready", v);
        var c = await f.StartAsync(); await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman);
        f.Captures[0].Hold = true;
        await f.Verify(c);
        Check(c.Snapshot.Recovery!.Result.Error == RecoveryError.ValidationTimedOut && c.RecoveryWorkInFlight, "native timeout released actual work");
        for (int i = 0; i < 8; i++)
            Check((await f.Verify(c)).Value is RecoveryResult { Error: RecoveryError.Busy }, "native timeout queued another verification");
        Check(v.Calls == 1 && f.Captures[0].Calls == 2 && f.Read(c, "after") == 0, "late native validation work accumulated/replayed");
        f.Captures[0].Release.TrySetResult(); await Until(() => !c.RecoveryWorkInFlight);
        Check(f.Read(c, "after") == 0, "late native capture resumed mainline");
    }
    private static async Task CallerCancellationAsync()
    {
        await using var f = await Fixture.CreateAsync(Script(true));
        var v = new Validator { Hold = true }; f.Registry.Register(f.Installation, "controlled-ready", v);
        var c = await f.StartAsync(); await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman);
        using var cancel = new CancellationTokenSource();
        var request = f.Request(c, AutomationOperation.VerifyRecovery, new { recoveryId = c.Snapshot.Recovery!.RecoveryId });
        var task = f.Scheduler.Gateway.ExecuteAsync(await f.Caller(c), request, cancel.Token);
        await Bound(v.Entered.Task); cancel.Cancel();
        Check((await task).Status == OperationStatus.Cancelled, "caller cancellation not reflected by Gateway");
        await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman);
        v.Release.TrySetResult(); await Until(() => f.Coordinator.AdviceInFlight == 0);
        Check(f.Read(c, "after") == 0, "cancelled control applied late verification");
    }
    private static async Task RepetitionAsync()
    {
        await using var f = await Fixture.CreateAsync(Script(true), input: true);
        for (int n = 0; n < 10; n++)
        {
            var c = await f.StartAsync(); await Until(() => c.TaskStatus == AutomationTaskStatus.AwaitingHuman);
            await f.Scheduler.StopTaskAsync(c.InstanceId); f.Arbiter.ReturnControl();
            Check(f.Coordinator.ActiveRecoveries == 0 && f.Scheduler.Gateway.RegisteredRunCount == 0 && f.Monitor.Snapshot.Sessions == 0 && f.Arbiter.Snapshot.Active == 0 && f.Arbiter.Snapshot.Waiting == 0, "repeated recovery leaked run/session/lease");
        }
    }
    internal sealed class Executor(RecoveryStatus status) : IRecoveryExecutor
    {
        public bool Hold; public int Calls;
        public readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        public async Task<RecoveryResult> TryRecoverAsync(RecoveryRequest request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); if (Hold) await Release.Task; return new(status, Guid.NewGuid(), "untrusted suggestion"); }
    }
    private sealed class Validator : IRecoveryValidator
    {
        public bool Valid = true, Hold, Capture, Fail; public int Calls;
        public readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        public async Task<bool> ValidateAsync(RecoveryValidationContext context, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); if (Hold) await Release.Task; if (Capture) await context.CaptureFreshAsync(); if (Fail) throw new IOException("validator resource failed"); return Valid; }
    }
    private sealed class Capture : ICaptureService
    {
        public bool Hold, Disposed, FailCleanup; public int Calls;
        public readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        public bool IsCapturing => !Disposed;
        public Task StartAsync(nint hwnd, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); if (Hold) { Entered.TrySetResult(); await Release.Task; } return new([0, 200, 0, 255], 1, 1, 4); }
        public Task StopAsync() => FailCleanup ? Task.FromException(new IOException("cleanup failed")) : Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Archive : IAnomalyLogger
    {
        public bool Fail;
        public async Task<string> LogAnomalyAsync(TaskExecutionContext context, string reason, string detail)
        { if (Fail) throw new IOException("archive failed"); await context.Capture.GrabFrameAsync(); return "test-only.json"; }
    }
    private sealed class Windows : IInputWindowValidator
    { public bool Valid = true; public InputTarget Bind(nint hwnd) => new(hwnd, 1); public bool IsValid(InputTarget target) => Valid; }
    private sealed class Logger : Microsoft.Extensions.Logging.ILogger<LuaRuntime>
    {
        public int Stops;
        public IDisposable BeginScope<TState>(TState state) => new Scope();
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (formatter(state, exception).Contains("recovery_stop_once")) Interlocked.Increment(ref Stops); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root;
        public readonly StateStore Store = new(); public readonly PluginAuthorization Auth = new();
        public required CommandBus Bus; public required Scheduler Scheduler; public required RecoveryCoordinator Coordinator;
        public readonly RecoveryValidatorRegistry Registry = new(); public readonly Archive Archive = new(); public readonly Logger Logger = new();
        public readonly Windows Windows = new(); public required InputArbiter Arbiter; public required InputInterventionCoordinator Monitor;
        public readonly InputInterventionTests.Source Source = new();
        public readonly List<Capture> Captures = []; public readonly List<FakeInput> Inputs = [];
        public PluginInstallation Installation => new("recovery", Path.Combine(Root, "recovery"));
        public static async Task<Fixture> CreateAsync(string script, bool input = false, IRecoveryExecutor? executor = null, RecoveryOptions? options = null, bool agent = true)
        {
            var f = new Fixture { Root = Path.Combine(AppContext.BaseDirectory, "recovery-" + Guid.NewGuid().ToString("N")), Bus = null!, Scheduler = null!, Coordinator = null!, Arbiter = null!, Monitor = null! };
            string directory = Path.Combine(f.Root, "recovery"); Directory.CreateDirectory(directory);
            var permissions = new List<string> { Permission.Capture, Permission.Timer }; if (agent) permissions.Add(Permission.Agent); if (input) permissions.Add(Permission.ControlInput);
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new { id = "recovery", name = "Recovery", version = "1", targetProcess = "isolated", permissions, backgroundInput = true }));
            await File.WriteAllTextAsync(Path.Combine(directory, "main.lua"), script);
            TestAuthorizationHost.ApproveCatalog(f.Auth, f.Root);
            f.Bus = new(f.Store); f.Arbiter = new(f.Windows); f.Monitor = new(f.Source, f.Arbiter, NullLogger<InputInterventionCoordinator>.Instance);
            await f.Monitor.StartAsync();
            f.Coordinator = new(f.Archive, f.Auth, f.Registry, executor, options);
            f.Scheduler = new(new PluginManager(NullLogger<PluginManager>.Instance), f.Root,
                () => { var capture = new Capture(); f.Captures.Add(capture); return capture; },
                () => { var service = new FakeInput(); f.Inputs.Add(service); return service; },
                (c, i) => new LuaRuntime(c, new FakeOcr(), i, f.Logger, new FakeCoordinateMapper(), ensureForeground: _ => true, commandBus: f.Bus),
                NullLogger<Scheduler>.Instance, f.Archive, commandBus: f.Bus, authorization: f.Auth, inputArbiter: f.Arbiter, intervention: f.Monitor, recovery: f.Coordinator);
            return f;
        }
        public async Task<TaskExecutionContext> StartAsync() { var id = await Scheduler.StartTaskAsync("recovery", 123); return Scheduler.Instances.Single(c => c.InstanceId == id); }
        public int Read(TaskExecutionContext context, string key) => Store.GetState(context.InstanceScope, key, 0);
        public Task<AutomationCallContext> Caller(TaskExecutionContext context, AutomationSource source = AutomationSource.PluginUI) => Scheduler.Gateway.CreateContextAsync("recovery", "recovery-test-host", source, context.Identity);
        public OperationRequest Request(TaskExecutionContext context, AutomationOperation operation, object parameters) => new(operation, JsonSerializer.SerializeToElement(parameters), Guid.NewGuid(), Guid.NewGuid().ToString("N"), OperationTarget.ForRun(context.Identity), DateTimeOffset.UtcNow.AddSeconds(10));
        public async Task<OperationResult> Operation(TaskExecutionContext context, AutomationOperation operation, object parameters) => await Scheduler.Gateway.ExecuteAsync(await Caller(context), Request(context, operation, parameters));
        public Task<OperationResult> Verify(TaskExecutionContext context) => Operation(context, AutomationOperation.VerifyRecovery, new { recoveryId = context.Snapshot.Recovery!.RecoveryId });
        public async ValueTask DisposeAsync()
        { foreach (var c in Captures) c.Release.TrySetResult(); await Scheduler.DisposeAsync(); await Monitor.DisposeAsync(); Bus.Dispose(); Directory.Delete(Root, true); }
    }
}
