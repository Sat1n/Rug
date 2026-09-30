using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Exceptions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Security;
using Rug.UI.Core.Services;
using Scheduler = Rug.UI.Core.Services.TaskScheduler;

internal static class AuthorizationTests
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "authorization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await TestContractsAsync(root);
            await TestGatewayAsync(root);
            await TestBridgeGuardsAsync(root);
            await TestRevocationAndReloadAsync(root);
            Console.WriteLine("Explicit grants, revocation, queued dispatch, installation isolation and reload tests passed");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task TestContractsAsync(string root)
    {
        var manifest = new PluginManifest([Permission.Timer, Permission.FileSystem, Permission.Agent])
            { Id = "contracts", PluginDirectory = root };
        var auth = new PluginAuthorization();
        var initial = auth.Register(manifest);
        var ticket = auth.Bind(manifest);
        Check(auth.Missing(ticket, manifest.Permissions).Count == 3, "manifest was treated as a grant");
        var grant = auth.Grant(initial.Installation, manifest.Permissions);
        Check(grant.Revision > initial.Revision && initial.Granted.Count == 0, "snapshot aliased grant state");
        var wrongPath = manifest with { PluginDirectory = Path.Combine(root, "elsewhere") };
        Check(auth.Missing(auth.Bind(wrongPath), [Permission.Timer]).Count == 1, "install path inherited grants");
        Check(auth.Missing(auth.Bind(manifest with { Id = "another" }), [Permission.Timer]).Count == 1, "plugin inherited grants");
        Check(auth.Missing(new PluginAuthorization().Bind(manifest), [Permission.Timer]).Count == 1, "foreign issuer ticket accepted");
        Check(auth.Missing(auth.Bind(manifest, [Permission.Timer]), [Permission.Agent]).Count == 1, "caller ceiling was ignored");
        Throws(() => auth.Grant(initial.Installation, ["input"]));
        Throws(() => auth.Grant(initial.Installation, ["timer.*"]));
        var unknown = manifest with { Id = "unknown", Permissions = ["custom.unknown"] };
        var unknownTicket = auth.Bind(unknown);
        Throws(() => auth.Grant(unknownTicket.Installation, unknown.Permissions));
        Check(auth.Missing(unknownTicket, unknown.Permissions).Count == 1, "unknown permission allowed");
        var gate = new PermissionInterceptor(manifest, NullLogger.Instance, auth, ticket);
        gate.Demand(Permission.Agent, "anomaly");
        auth.Revoke(initial.Installation, [Permission.Agent, Permission.FileSystem]);
        Denied(() => gate.Demand(Permission.Agent, "rug.agent.resolve_anomaly"));
        Denied(() => gate.Demand(Permission.FileSystem, "external template"));
        auth.Grant(initial.Installation, [Permission.Agent, Permission.FileSystem]);
        Denied(() => gate.Demand(Permission.Agent, "old context"));
        new PermissionInterceptor(manifest, NullLogger.Instance, auth).Demand(Permission.Agent, "new context");
        auth.Register(manifest with { Permissions = [Permission.Timer] });
        Check(auth.Query(initial.Installation).Granted.SequenceEqual(new[] { Permission.Timer }), "removed permissions remained effective");
        auth.Register(manifest);
        Check(auth.Missing(auth.Bind(manifest), [Permission.Agent]).Count == 1, "re-added permission regained stale grant");
        var policy = new PluginAuthorization((_, p) => p != Permission.Timer);
        policy.Register(manifest); policy.Grant(initial.Installation, manifest.Permissions);
        Check(policy.Missing(policy.Bind(manifest), [Permission.Timer]).Count == 1, "host policy ignored");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            var current = auth.Bind(manifest);
            using var lease = auth.Admit(current, [Permission.Timer]);
            Check(!lease.Token.IsCancellationRequested, "independent lease unexpectedly cancelled");
        })));
        Check(auth.ActiveLeaseCount == 0, "disposed leases leaked");
        using (var lease = auth.Admit(auth.Bind(manifest), [Permission.Timer]))
        {
            int notifications = 0;
            EventHandler<AuthorizationChangedEventArgs> observer = (_, _) => notifications++;
            EventHandler<AuthorizationChangedEventArgs> broken = (_, _) => throw new Exception("observer failure");
            auth.Changed += broken;
            auth.Changed += observer;
            using var badCancellation = lease.Token.Register(() => throw new Exception("cancellation observer failure"));
            auth.Revoke(initial.Installation, [Permission.Timer]);
            Check(lease.Token.IsCancellationRequested && notifications == 1, "observer failure prevented revocation");
            auth.Changed -= broken;
            auth.Changed -= observer;
            auth.Grant(initial.Installation, [Permission.Timer]);
            Check(notifications == 1, "removed observer remained subscribed");
        }
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            auth.Grant(initial.Installation, [Permission.Timer]);
            auth.Revoke(initial.Installation, [Permission.Timer]);
        })));
        auth.Grant(initial.Installation, [Permission.Timer]);
        Check(auth.Query(initial.Installation).Effective.Contains(Permission.Timer) && auth.ActiveLeaseCount == 0,
            "concurrent grant/revoke corrupted authorization or leaked leases");
        Throws(() => auth.Admit(new PluginAuthorization().Bind(manifest), []));
    }
    private static async Task TestGatewayAsync(string root)
    {
        string script = Path.Combine(root, "gateway.lua");
        await File.WriteAllTextAsync(script, "function on_tick() rug.sleep(1) end");
        var manifest = new PluginManifest([Permission.Timer, Permission.Capture, Permission.VisionMatch])
            { Id = "gateway-auth", PluginDirectory = root };
        var auth = new PluginAuthorization();
        var queued = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool holdQueue = false;
        var gateway = new AutomationGateway(NullLogger.Instance, getPlugin: (_, _) => Task.FromResult(manifest),
            authorization: auth, waitForDispatch: () => holdQueue ? queued.Task : Task.CompletedTask,
            start: (_, _, _, _, _) => throw new Exception("unauthorized start dispatched"));
        auth.Register(manifest);
        var identity = new RunIdentity(manifest.Id, Guid.NewGuid(), 1, null, 0);
        var capture = new LateCapture();
        await using var runtime = new LuaRuntime(capture, new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance);
        runtime.ConfigureInputArbiter(TestAuthorizationHost.InputArbiter);
        runtime.ConfigureGateway(gateway);
        runtime.ConfigureState(identity, new Dictionary<string, object?>(), new Dictionary<string, object?>());
        var noGrant = await gateway.CreateContextAsync(manifest.Id, "ui", AutomationSource.PluginUI);
        var deniedStart = await gateway.ExecuteAsync(noGrant, Request(AutomationOperation.Start, new(manifest.Id)));
        Check(deniedStart is { Status: OperationStatus.Rejected, Error: OperationError.PermissionDenied } &&
            deniedStart.MissingPermissions!.Count == 3 && capture.Calls == 0, "no-grant start dispatched/missing list lost");
        auth.Grant(PluginInstallation.From(manifest), manifest.Permissions);
        await runtime.InitializeAsync(script, manifest);
        foreach (var source in Enum.GetValues<AutomationSource>())
        {
            var caller = await gateway.CreateContextAsync(manifest.Id, "host-" + source, source,
                source == AutomationSource.Lua ? identity : null, capabilities: [Permission.Timer]);
            var denied = await gateway.ExecuteAsync(caller, Request(AutomationOperation.Capture, OperationTarget.ForRun(identity),
                new { permissions = new[] { Permission.Capture }, approved = true, principal = "admin" }));
            Check(denied.Error == OperationError.PermissionDenied && denied.MissingPermissions!.Contains(Permission.Capture), "adapter ceiling/forged grant bypassed");
            Check((await gateway.ExecuteAsync(caller, Request(AutomationOperation.Sleep, OperationTarget.ForRun(identity), new { ms = 0 }))).Status == OperationStatus.Success,
                "granted request did not execute");
        }
        Check(capture.Calls == 0, "denied adapters called capture");
        var full = await gateway.CreateContextAsync(manifest.Id, "mcp", AutomationSource.Mcp);
        var actualManifest = manifest;
        manifest = manifest with { PluginDirectory = Path.Combine(root, "different-install") };
        var wrongInstall = await gateway.CreateContextAsync(manifest.Id, "mcp", AutomationSource.Mcp);
        auth.Grant(wrongInstall.Authorization.Installation, manifest.Permissions);
        Check((await gateway.ExecuteAsync(wrongInstall, Request(AutomationOperation.Capture, OperationTarget.ForRun(identity)))).Error == OperationError.InvalidCaller &&
            capture.Calls == 0, "wrong installation context reached another installation's run");
        manifest = actualManifest;
        var external = await gateway.ExecuteAsync(full, Request(AutomationOperation.FindImage, OperationTarget.ForRun(identity),
            new { path = Path.Combine(Path.GetDirectoryName(root)!, "external.bmp") }));
        Check(external.Error == OperationError.PermissionDenied && external.DeniedPermission == Permission.FileSystem,
            "external template escaped gateway permissions");
        holdQueue = true;
        var pending = gateway.ExecuteAsync(full, Request(AutomationOperation.Capture, OperationTarget.ForRun(identity)));
        auth.Revoke(PluginInstallation.From(manifest), [Permission.Capture]);
        queued.TrySetResult(true);
        var cancelled = await pending;
        Check(cancelled.Status is OperationStatus.Cancelled or OperationStatus.Rejected && capture.Calls == 0, "revoked queued request dispatched");
        auth.Grant(PluginInstallation.From(manifest), [Permission.Capture]);
        var old = await gateway.ExecuteAsync(full, Request(AutomationOperation.Capture, OperationTarget.ForRun(identity)));
        Check(old.Status != OperationStatus.Success && capture.Calls == 0, "regrant revived old run");
    }
    private static async Task TestBridgeGuardsAsync(string root)
    {
        var manifest = new PluginManifest([Permission.Timer, Permission.Capture, Permission.VisionMatch,
            Permission.VisionOcr, Permission.FileSystem, Permission.Agent, Permission.ControlInput])
            { Id = "bridges", PluginDirectory = root, TargetWindow = 123 };
        var auth = new PluginAuthorization();
        auth.Register(manifest);
        var capture = new LateCapture();
        var ocr = new FakeOcr();
        var match = new FakeTemplateMatch();
        var input = new FakeInput();
        using (var visual = new VisualEngineBridge(capture, ocr, match, new PermissionInterceptor(manifest, NullLogger.Instance, auth), root))
        {
            try { await visual.CaptureAsync(); throw new Exception("unapproved bridge captured"); }
            catch (PermissionDeniedException) { }
            try { await visual.OcrAsync(); throw new Exception("unapproved bridge OCR executed"); }
            catch (PermissionDeniedException) { }
            try { await visual.FindImageAsync("missing.bmp"); throw new Exception("unapproved bridge match executed"); }
            catch (PermissionDeniedException) { }
        }
        Check(capture.Calls == 0 && ocr.LastFrame is null && match.MatchCalls == 0, "unapproved bridge called a vision engine");
        auth.Grant(PluginInstallation.From(manifest), manifest.Permissions);
        string script = Path.Combine(root, "bridge.lua");
        await File.WriteAllTextAsync(script, "function on_tick() end");
        var gateway = new AutomationGateway(NullLogger.Instance, getPlugin: (_, _) => Task.FromResult(manifest), authorization: auth);
        var identity = new RunIdentity(manifest.Id, Guid.NewGuid(), 1, Guid.NewGuid(), 123);
        await using var runtime = new LuaRuntime(capture, ocr, input, NullLogger<LuaRuntime>.Instance,
            new FakeCoordinateMapper(), _ => true, match);
        runtime.ConfigureInputArbiter(TestAuthorizationHost.InputArbiter);
        runtime.ConfigureGateway(gateway);
        runtime.ConfigureState(identity, new Dictionary<string, object?>(), new Dictionary<string, object?>());
        await runtime.InitializeAsync(script, manifest);
        var narrowed = await gateway.CreateContextAsync(manifest.Id, "agent", AutomationSource.Agent,
            capabilities: [Permission.VisionMatch]);
        var result = await gateway.ExecuteAsync(narrowed, Request(AutomationOperation.FindImage, OperationTarget.ForRun(identity),
            new { path = Path.Combine(Path.GetDirectoryName(root)!, "outside.bmp") }));
        Check(result.DeniedPermission == Permission.FileSystem && match.MatchCalls == 0,
            "external template borrowed runtime's broader filesystem capability");
        var caller = await gateway.CreateContextAsync(manifest.Id, "ui", AutomationSource.PluginUI);
        var key = gateway.ExecuteAsync(caller, Request(AutomationOperation.PressKey, OperationTarget.ForRun(identity), new { key = 27, durationMs = 10000 }));
        await Until(() => input.KeyDownCalls == 1);
        auth.Revoke(PluginInstallation.From(manifest), [Permission.ControlInput]);
        Check((await key).Status == OperationStatus.Cancelled, "revoked key delay did not cancel");
        await Until(() => input.KeyUpCalls == 1);
        Check(input.KeyDownCalls == 1, "revocation repeated input instead of releasing key");

        // The direct anomaly hook remains outside Gateway, but cannot use an undeclared/ungranted capability.
        var anomalyManifest = new PluginManifest([Permission.Timer]) { Id = "anomaly-denied", PluginDirectory = root };
        auth.Register(anomalyManifest); auth.Grant(PluginInstallation.From(anomalyManifest), anomalyManifest.Permissions);
        await File.WriteAllTextAsync(script, "function on_tick() rug.set_state('approved', true); rug.set_state('permissions', 'agent'); rug.agent.resolve_anomaly('reason', 'detail') end");
        int incidents = 0;
        await using var anomaly = new LuaRuntime(new FakeCapture(), new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance, authorization: auth);
        anomaly.ConfigureAnomalyHandler((_, _) => { incidents++; return Task.FromResult("incident"); });
        await anomaly.InitializeAsync(script, anomalyManifest);
        var fault = await anomaly.StepAsync();
        Check(fault.State == ScriptState.Faulted && fault.Error is PermissionDeniedException && incidents == 0,
            "Lua anomaly hook bypassed effective agent authorization");
    }
    private static async Task TestRevocationAndReloadAsync(string root)
    {
        string pluginsRoot = Path.Combine(root, "plugins");
        async Task Write(string id, string[] permissions)
        {
            string path = Path.Combine(pluginsRoot, id); Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "manifest.json"), JsonSerializer.Serialize(new
            { id, name = id, version = "1", permissions, targetProcess = "demo" }));
            await File.WriteAllTextAsync(Path.Combine(path, "main.lua"), "function on_tick() rug.sleep(1000) end");
            await File.WriteAllTextAsync(Path.Combine(path, "ui.json"), "{\"type\":\"Page\",\"id\":\"page\"}");
        }
        await Write("first", [Permission.Timer, Permission.Capture]);
        await Write("other", [Permission.Timer]);
        var auth = new PluginAuthorization();
        int factories = 0;
        var captures = new List<LateCapture>();
        await using var scheduler = new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance), pluginsRoot,
            () => { factories++; var c = new LateCapture(); captures.Add(c); return c; }, () => new FakeInput(),
            (c, i) => new LuaRuntime(c, new FakeOcr(), i, NullLogger<LuaRuntime>.Instance),
            NullLogger<Scheduler>.Instance, new NoAnomaly(), authorization: auth, inputArbiter: TestAuthorizationHost.InputArbiter);
        try { await scheduler.StartTaskAsync("first", 123); throw new Exception("unapproved scheduler start accepted"); }
        catch (PermissionDeniedException) { }
        Check(factories == 0, "startup created native services before authorization");
        TestAuthorizationHost.ApproveCatalog(auth, pluginsRoot);
        Guid first = await scheduler.StartTaskAsync("first", 123);
        Guid sibling = await scheduler.StartTaskAsync("first", 456);
        Guid other = await scheduler.StartTaskAsync("other", 0);
        var identity = scheduler.Instances.Single(c => c.InstanceId == first).Identity;
        var caller = await scheduler.Gateway.CreateContextAsync("first", "agent", AutomationSource.Agent);
        var duplicate = Request(AutomationOperation.Capture, OperationTarget.ForRun(identity));
        captures[0].Hold = true;
        var work = scheduler.Gateway.ExecuteAsync(caller, duplicate);
        await Until(() => captures[0].Calls == 1);
        var installation = new PluginInstallation("first", Path.Combine(pluginsRoot, "first"));
        auth.Revoke(installation, [Permission.Capture]);
        var outcome = await work;
        Check(outcome.Status == OperationStatus.Cancelled && outcome.OutcomeUncertain, "in-flight revoke lost uncertainty");
        await Until(() => scheduler.Instances.Count == 1);
        Check(scheduler.Instances.Single().InstanceId == other, "revocation affected other plugin or retained sibling");
        auth.Grant(installation, [Permission.Capture]);
        Guid fresh = await scheduler.StartTaskAsync("first", 789);
        Check(fresh != first && fresh != sibling, "new start inherited identity");
        captures[0].Release();
        await Until(() => scheduler.Gateway.ActiveWorkCount == 0);
        Check((await scheduler.Gateway.ExecuteAsync(caller, duplicate)).Status != OperationStatus.Success && captures[0].Calls == 1,
            "revocation cleared dedup and repeated old side effect");
        var freshIdentity = scheduler.Instances.Single(c => c.InstanceId == fresh).Identity;
        Check((await scheduler.Gateway.ExecuteAsync(caller, Request(AutomationOperation.Capture, OperationTarget.ForRun(freshIdentity)))).Error == OperationError.PermissionDenied,
            "old caller regained revoked capability");
        var newCaller = await scheduler.Gateway.CreateContextAsync("first", "agent", AutomationSource.Agent);
        Check((await scheduler.Gateway.ExecuteAsync(newCaller, Request(AutomationOperation.Capture, OperationTarget.ForRun(freshIdentity)))).Status == OperationStatus.Success,
            "fresh granted run failed");
        await using var reload = new HotReloadService(pluginsRoot, scheduler, NullLogger<HotReloadService>.Instance);
        var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        reload.ReloadFailed += (_, _) => failed.TrySetResult(true);
        reload.Start();
        await Write("first", [Permission.Timer, Permission.Capture, Permission.Agent]);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!scheduler.Instances.Any(c => c.PluginId == "first") && auth.Query(installation).Granted.Contains(Permission.Agent) == false,
            "expanded reload automatically authorized/restarted");
        await scheduler.StopTaskAsync(other);
        for (int n = 0; n < 10; n++)
        {
            Guid run = await scheduler.StartTaskAsync("other", 0);
            await scheduler.StopTaskAsync(run);
        }
        Check(scheduler.Instances.Count == 0 && scheduler.Gateway.RegisteredRunCount == 0 && auth.ActiveLeaseCount == 0,
            "repeated runs leaked instance, operation or authorization registrations");
    }
    private static OperationRequest Request(AutomationOperation op, OperationTarget target, object? args = null) =>
        new(op, JsonSerializer.SerializeToElement(args ?? new { }), Guid.NewGuid(), "authorization-test", target, DateTimeOffset.UtcNow.AddSeconds(10));
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Throws(Action action) { try { action(); throw new Exception("invalid grant accepted"); } catch (ArgumentException) { } }
    private static void Denied(Action action) { try { action(); throw new Exception("permission bypassed"); } catch (PermissionDeniedException) { } }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private sealed class NoAnomaly : IAnomalyLogger
    {
        public Task<string> LogAnomalyAsync(TaskExecutionContext context, string reason, string detail) => throw new NotSupportedException();
    }
    private sealed class LateCapture : ICaptureService
    {
        private readonly TaskCompletionSource<CapturedFrame?> _late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool Hold;
        public bool IsCapturing => true;
        public Task StartAsync(nint hwnd, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return Hold ? _late.Task : Task.FromResult<CapturedFrame?>(new(new byte[4], 1, 1, 4)); }
        public void Release() => _late.TrySetResult(new(new byte[4], 1, 1, 4));
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
