using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using Scheduler = Rug.UI.Core.Services.TaskScheduler;

internal static partial class CheckpointTests
{
    private const string RestoreScript = """
        function on_init()
          rug.set_state('before',true)
          rug.press_key(66,1)
          local r=rug.checkpoint.commit('ready','{"step":1,"precision":1e-40}','["panel-ready"]')
          assert(r.status=='committed')
          rug.set_state('after',1)
        end
        function on_restore(json)
          assert(type(json)=='string' and json:find('"precision":1e-40',1,true))
          assert(rug.get_state('before')==nil)
          rug.press_key(67,1)
          rug.set_state('restore_done',true)
        end
        function on_tick() rug.sleep(60000) end
        """;
    private sealed class RestoreValidator : ICheckpointRestoreValidator
    {
        public bool Hold, Valid = true; public int Calls;
        public TaskCompletionSource Entered = Signal(), Release = Signal();
        public async Task<bool> ValidateAsync(CheckpointObservation observation, CheckpointRecord record, CancellationToken token)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); if (Hold) await Release.Task; var frame = await observation.CaptureFreshAsync(); return Valid && frame.Pixels[1] == 200; }
    }
    private static OperationRequest RestoreRequest(Guid selection, Guid? operation = null) => new(AutomationOperation.RestoreCheckpoint,
        JsonSerializer.SerializeToElement(new { selectionId = selection }), operation ?? Guid.NewGuid(), "checkpoint-restore-test", new("probe"), DateTimeOffset.UtcNow.AddSeconds(15));
    public static async Task RunRestoreAsync()
    {
        await RestoreRoundTripAsync();
        foreach (string mode in new[] { "false", "cancel", "stop", "code", "config", "record", "revoke", "target", "node", "deadline", "validation-timeout" }) await RestoreRejectAsync(mode);
        await RestoreCancelBoundaryAsync();
        await RestoreDefinitionAsync();
        await RestoreActualWorkAsync();
        await RestoreDiscoveryAsync();
        await RestoreCurrentManifestAsync();
        Console.WriteLine("Checkpoint restore PASS: actual Lua, new-run links, no BEFORE replay, dedup/exclusion, fresh verification, late results");
    }
    private static async Task RestoreRoundTripAsync()
    {
        await using var f = await Fixture.CreateAsync(script: RestoreScript, input: true);
        var source = await f.StartAsync(); await Until(() => f.StoreState.GetState(source.InstanceScope, "after", 0) == 1);
        var validator = new RestoreValidator { Hold = true }; var m = source.CheckpointMetadata!;
        using var node = f.Registry.RegisterRestore(new(m.Installation, m.PluginVersion, m.Code.ContentHash, "ready", 1, new[] { "panel-ready" }), validator);
        var active = (await f.Scheduler.QueryCheckpointsAsync("probe")).Single();
        Check(active.Compatibility == "SourceStillActive", "active checkpoint source not reported");
        var caller = await f.Scheduler.CreateCheckpointRestoreContextAsync("probe", new(123, 1));
        Check((await f.Scheduler.Gateway.ExecuteAsync(caller, RestoreRequest(active.SelectionId))).Error == OperationError.SourceStillActive, "active source restored");
        await f.Scheduler.StopTaskAsync(source.InstanceId);
        var choice = (await f.Scheduler.QueryCheckpointsAsync("probe")).Single(); Check(choice.Compatibility == "Ready", "compatible choice unavailable");
        Check((await f.Scheduler.Gateway.ExecuteAsync(caller, RestoreRequest(Guid.NewGuid()))).Error == OperationError.CheckpointChanged && f.Inputs.Count == 1,
            "unknown checkpoint reached service creation");
        var agent = await f.Scheduler.Gateway.CreateContextAsync("probe", "fake-mcp", AutomationSource.Mcp, startHwnd: 123);
        Check((await f.Scheduler.Gateway.ExecuteAsync(agent, RestoreRequest(choice.SelectionId))).Error == OperationError.InvalidCaller, "MCP self-approved restore");
        await using (var other = await Fixture.CreateAsync(script: RestoreScript, input: true))
        {
            TestAuthorizationHost.ApproveCatalog(f.Authorization, Path.Combine(other.Root, "plugins"));
            var otherManifest = new PluginManager(NullLogger<PluginManager>.Instance).ScanPlugins(Path.Combine(other.Root, "plugins")).Single();
            var wrong = f.Scheduler.Gateway.BindContext(otherManifest, "checkpoint-host-ui", AutomationSource.PluginUI, hwnd: 123);
            wrong.RestoreTarget = new(123, 1);
            Check((await f.Scheduler.Gateway.ExecuteAsync(wrong, RestoreRequest(choice.SelectionId))).Error == OperationError.CheckpointChanged && f.Inputs.Count == 1,
                "same-ID wrong installation restored another installation's checkpoint");
        }
        var request = RestoreRequest(choice.SelectionId); var first = f.Scheduler.Gateway.ExecuteAsync(caller, request);
        await Bound(validator.Entered.Task); var candidate = f.Scheduler.Instances.Single();
        Check(candidate.TaskStatus == AutomationTaskStatus.Starting && f.Inputs.Last().KeyDownCalls == 0, "preparation ran business input");
        Check(ReferenceEquals(first, f.Scheduler.Gateway.ExecuteAsync(caller, request)), "same operation did not share result");
        var otherTarget = await f.Scheduler.CreateCheckpointRestoreContextAsync("probe", new(124, 1));
        Check((await f.Scheduler.Gateway.ExecuteAsync(otherTarget, request)).Error == OperationError.DeduplicationConflict, "same operation silently changed host target");
        Check((await f.Scheduler.Gateway.ExecuteAsync(caller, RestoreRequest(choice.SelectionId))).Error == OperationError.Busy, "parallel restore created another candidate");
        Check((await f.Scheduler.Gateway.ExecuteAsync(caller, request with { Parameters = JsonSerializer.SerializeToElement(new { selectionId = Guid.NewGuid() }) })).Error == OperationError.DeduplicationConflict, "same-key conflict accepted");
        validator.Release.TrySetResult(); var result = await first;
        Check(result.Status == OperationStatus.Success && result.Value is RunIdentity, "restore did not return identity: " + result);
        var run = (RunIdentity)result.Value!; await Until(() => f.StoreState.GetState(run.InstanceScope, "restore_done", false));
        Check(run.InstanceId != source.InstanceId && run.RunGeneration > source.RunGeneration && run.TargetBindingId != source.TargetBindingId &&
            candidate.CheckpointOrigin?.ParentInstanceId == source.InstanceId && f.Inputs.Last().KeyDownCalls == 1 && validator.Calls == 1,
            "restore identity/origin or AFTER-only action failed");
        Check((await f.Scheduler.Gateway.ExecuteAsync(caller, RestoreRequest(choice.SelectionId))).Error == OperationError.Busy, "active descendant duplicated");
        await f.Arbiter.TakeOverAsync();
        Check(candidate.TaskStatus == AutomationTaskStatus.Stopped, "restored input run survived takeover");
        f.Arbiter.ReturnControl();
        Check(!f.Scheduler.Instances.Any(c => c.InstanceId == run.InstanceId), "return control revived checkpoint descendant");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(CheckpointTests).Assembly.Location);
        start.ArgumentList.Add("--checkpoint-restore-child"); start.ArgumentList.Add(f.Root);
        using var child = Process.Start(start)!; var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
        Check(child.ExitCode == 0 && (await stdout).Contains("checkpoint-restore-child-PASS"), "child restart restore failed: " + await stderr);
        Console.WriteLine("Checkpoint restart restore PASS: actual new process, re-granted test host, fresh Fake observation (NOT WGC), AFTER only");
        var ordinary = await f.Scheduler.Gateway.ExecuteAsync(caller, new(AutomationOperation.Start,
            JsonSerializer.SerializeToElement(new { selectionId = choice.SelectionId, restore = true, checkpointJson = "forged" }),
            Guid.NewGuid(), "forged-launch-input", new("probe"), DateTimeOffset.UtcNow.AddSeconds(15)));
        Check(ordinary.Value is RunIdentity, "ordinary start with business fields failed");
        var normal = (RunIdentity)ordinary.Value!; await Until(() => f.StoreState.GetState(normal.InstanceScope, "after", 0) == 1);
        Check(f.Scheduler.Instances.Single(c => c.InstanceId == normal.InstanceId).CheckpointOrigin is null &&
            f.StoreState.GetState(normal.InstanceScope, "before", false) && !f.StoreState.GetState(normal.InstanceScope, "restore_done", false),
            "ordinary launch fields forged restore mode");
    }
    public static async Task RestoreChildAsync(string root)
    {
        await using var store = new FileCheckpointStore(Path.Combine(root, "records"));
        using var bus = new CommandBus(new StateStore()); bus.SetState(StateScope.Plugin("probe"), "speed", 2);
        var grants = new PluginAuthorization(); TestAuthorizationHost.ApproveCatalog(grants, Path.Combine(root, "plugins"));
        var manifest = new PluginManager(NullLogger<PluginManager>.Instance).ScanPlugins(Path.Combine(root, "plugins")).Single();
        var registry = new CheckpointNodeRegistry(); var code = CheckpointJson.Loaded(manifest.Entry, await File.ReadAllTextAsync(Path.Combine(manifest.PluginDirectory, manifest.Entry)));
        using var node = registry.RegisterRestore(new(PluginInstallation.From(manifest), manifest.Version, code.ContentHash, "ready", 1, new[] { "panel-ready" }), new RestoreValidator());
        var inputs = new List<FakeInput>();
        await using var scheduler = new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance), Path.Combine(root, "plugins"), () => new Capture(),
            () => { var input = new FakeInput(); inputs.Add(input); return input; }, (c, i) => new LuaRuntime(c, new FakeOcr(), i, NullLogger<LuaRuntime>.Instance,
                new FakeCoordinateMapper(), ensureForeground: _ => true, commandBus: bus), NullLogger<Scheduler>.Instance, new NoAnomaly(),
            commandBus: bus, authorization: grants, inputArbiter: new InputArbiter(new WindowValidator()), checkpointStore: store, checkpointRestores: registry);
        var choice = (await scheduler.QueryCheckpointsAsync("probe")).Single(); var caller = await scheduler.CreateCheckpointRestoreContextAsync("probe", new(123, 1));
        var result = await scheduler.Gateway.ExecuteAsync(caller, RestoreRequest(choice.SelectionId)); Check(result.Value is RunIdentity, "child restore rejected: " + result);
        var run = (RunIdentity)result.Value!; await Until(() => bus.GetState(run.InstanceScope, "restore_done", false));
        Check(inputs.Single().KeyDownCalls == 1 && !bus.GetState(run.InstanceScope, "before", false), "child replayed BEFORE");
        Console.WriteLine("checkpoint-restore-child-PASS; actual subprocess, Fake capture/input; not hardware acceptance");
    }
    private static async Task RestoreRejectAsync(string mode)
    {
        await using var f = await Fixture.CreateAsync(script: RestoreScript, input: true); var source = await f.StartAsync();
        await Until(() => f.StoreState.GetState(source.InstanceScope, "after", 0) == 1); await f.Scheduler.StopTaskAsync(source.InstanceId);
        var m = source.CheckpointMetadata!; var validator = new RestoreValidator { Hold = true, Valid = mode != "false" };
        using var node = f.Registry.RegisterRestore(new(m.Installation, m.PluginVersion, m.Code.ContentHash, "ready", 1, new[] { "panel-ready" }), validator);
        var choice = (await f.Scheduler.QueryCheckpointsAsync("probe")).Single(); var caller = await f.Scheduler.CreateCheckpointRestoreContextAsync("probe", new(123, 1));
        using var cancel = new CancellationTokenSource();
        if (mode == "validation-timeout") f.Scheduler.CheckpointStartValidationTimeout = TimeSpan.FromMilliseconds(300);
        var request = RestoreRequest(choice.SelectionId);
        if (mode == "deadline") request = request with { Deadline = DateTimeOffset.UtcNow.AddMilliseconds(800) };
        var pending = f.Scheduler.Gateway.ExecuteAsync(caller, request, cancel.Token);
        await Bound(validator.Entered.Task); var candidate = f.Scheduler.Instances.Single();
        try
        {
            if (mode == "cancel") cancel.Cancel();
            if (mode == "stop") candidate.Cancel();
            if (mode == "code") await File.AppendAllTextAsync(Path.Combine(f.Root, "plugins", "probe", "main.lua"), "\n--changed");
            if (mode == "config") f.Bus.SetState(StateScope.Plugin("probe"), "speed", 99);
            if (mode == "record") { var record = (await f.Store.ReadAsync(Address(source), Expected(source))).Record!; await f.Store.PublishAsync(record with { CheckpointId = Guid.NewGuid(), SubmissionSequence = 2 }, () => true, default); }
            if (mode == "revoke") f.Authorization.Revoke(m.Installation, [Permission.Checkpoint]);
            if (mode == "target") f.Windows.Stamp++;
            if (mode == "node") node.Dispose();
            if (mode is "deadline" or "validation-timeout") Check((await pending).Status == OperationStatus.TimedOut, "restore deadline not reported as timeout");
            validator.Release.TrySetResult(); var result = await pending; await Bound(candidate.Completion);
            Check(result.Status != OperationStatus.Success && f.Inputs.Last().KeyDownCalls == 0, "rejected/late restore executed business: " + mode + " " + result);
            Console.WriteLine("Checkpoint restore rejection PASS: " + mode);
        }
        finally { validator.Release.TrySetResult(); }
    }
    private static async Task RestoreCancelBoundaryAsync()
    {
        await using var f = await Fixture.CreateAsync(script: RestoreScript, input: true); var source = await f.StartAsync();
        await Until(() => f.StoreState.GetState(source.InstanceScope, "after", 0) == 1); await f.Scheduler.StopTaskAsync(source.InstanceId);
        var m = source.CheckpointMetadata!; using var node = f.Registry.RegisterRestore(new(m.Installation, m.PluginVersion, m.Code.ContentHash, "ready", 1, new[] { "panel-ready" }), new RestoreValidator());
        var choice = (await f.Scheduler.QueryCheckpointsAsync("probe")).Single(); var caller = await f.Scheduler.CreateCheckpointRestoreContextAsync("probe", new(123, 1));
        var entered = Signal(); var release = Signal();
        f.Scheduler.BeforeCheckpointStartDecision = async () => { entered.TrySetResult(); await release.Task; };
        using var original = new CancellationTokenSource();
        var pending = f.Scheduler.Gateway.ExecuteAsync(caller, RestoreRequest(choice.SelectionId), original.Token);
        await Bound(entered.Task); var candidate = f.Scheduler.Instances.Single();
        using var callbackRelease = new ManualResetEventSlim(); var callbackEntered = Signal();
        using var blocked = original.Token.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Wait(); });
        var cancelling = Task.Run(original.Cancel);
        try
        {
            await Bound(callbackEntered.Task); Check(original.IsCancellationRequested, "original restore token not cancelled");
            release.TrySetResult(); var result = await pending; await Bound(candidate.Completion);
            Check(result.Status == OperationStatus.Cancelled && candidate.TaskStatus == AutomationTaskStatus.Stopped && f.Inputs.Last().KeyDownCalls == 0,
                "blocked original-token propagation admitted Lua or faulted normal cancellation");
        }
        finally { callbackRelease.Set(); release.TrySetResult(); await Bound(cancelling); }
        f.Scheduler.BeforeCheckpointStartDecision = null;
        using var late = new CancellationTokenSource();
        var request = RestoreRequest(choice.SelectionId); var successful = await f.Scheduler.Gateway.ExecuteAsync(caller, request, late.Token);
        Check(successful.Value is RunIdentity, "fresh request after cancellation failed"); var run = (RunIdentity)successful.Value!;
        late.Cancel(); await Until(() => f.StoreState.GetState(run.InstanceScope, "restore_done", false));
        Check(f.Scheduler.Instances.Any(c => c.InstanceId == run.InstanceId && !c.IsBusinessClosed), "post-commit request cancellation stopped new run");
        Check((await f.Scheduler.Gateway.ExecuteAsync(caller, request)).Value is RunIdentity same && same == run, "post-commit retry lost original result");
        Console.WriteLine("Checkpoint restore decision PASS: blocked original cancellation before commit stops; cancellation after commit keeps tracked new run");
    }
    private static async Task RestoreDefinitionAsync()
    {
        foreach (string top in new[] { "rug.press_key(67,1)", "rug.checkpoint.commit('ready','{}','[]')", "rug.set_state('bad',true)", "rug.log('bad')", "error('real Lua definition failure')", "missing" })
        {
            string script = RestoreScript;
            // Normal start remains valid; the top-level declaration action is guarded by a read-only config value.
            if (top == "missing") script = script.Replace("function on_restore(json)", "function not_restore(json)");
            else script = "if rug.get_config('restore_definition_test') then " + top + " end\n" + script;
            await using var f = await Fixture.CreateAsync(script: script, input: true); var source = await f.StartAsync();
            await Until(() => f.StoreState.GetState(source.InstanceScope, "after", 0) == 1); await f.Scheduler.StopTaskAsync(source.InstanceId);
            // For a definition-side-effect scenario create a checkpoint with the same current config that the restore will use.
            if (top != "missing")
            {
                f.Bus.SetState(StateScope.Plugin("probe"), "restore_definition_test", true);
                var record = (await f.Store.ReadAsync(Address(source), Expected(source))).Record!;
                var cfg = new Dictionary<string, object?> { ["speed"] = 2, ["restore_definition_test"] = true };
                await f.Store.PublishAsync(record with { SubmissionSequence = 2, ConfigHash = CheckpointJson.ConfigHash(cfg) }, () => true, default);
            }
            var m = source.CheckpointMetadata!; using var node = f.Registry.RegisterRestore(new(m.Installation, m.PluginVersion, m.Code.ContentHash, "ready", 1, new[] { "panel-ready" }), new RestoreValidator());
            var choice = (await f.Scheduler.QueryCheckpointsAsync("probe")).Single(); var caller = await f.Scheduler.CreateCheckpointRestoreContextAsync("probe", new(123, 1));
            var result = await f.Scheduler.Gateway.ExecuteAsync(caller, RestoreRequest(choice.SelectionId));
            Check(result.Status != OperationStatus.Success && f.Inputs.Last().KeyDownCalls == 0, "restore declaration performed business or fell back to init: " + top);
        }
        Console.WriteLine("Checkpoint restore definitions PASS: missing hook, input/checkpoint/state/log attempts reject; actual Lua error remains failure; no business input");
    }
    private static async Task RestoreActualWorkAsync()
    {
        await using var f = await Fixture.CreateAsync(script: RestoreScript, input: true); var source = await f.StartAsync();
        await Until(() => f.StoreState.GetState(source.InstanceScope, "after", 0) == 1); await f.Scheduler.StopTaskAsync(source.InstanceId);
        var m = source.CheckpointMetadata!; using var node = f.Registry.RegisterRestore(new(m.Installation, m.PluginVersion, m.Code.ContentHash, "ready", 1, new[] { "panel-ready" }), new RestoreValidator());
        f.HoldCapture = true;
        var choice = (await f.Scheduler.QueryCheckpointsAsync("probe")).Single(); var caller = await f.Scheduler.CreateCheckpointRestoreContextAsync("probe", new(123, 1));
        var pending = f.Scheduler.Gateway.ExecuteAsync(caller, RestoreRequest(choice.SelectionId));
        await Until(() => f.Captures.Count == 2); var capture = f.Captures.Last(); await Bound(capture.Entered.Task);
        var candidate = f.Scheduler.Instances.Single(); var stop = f.Scheduler.StopTaskAsync(candidate.InstanceId);
        try
        {
            Check(candidate.TaskStatus == AutomationTaskStatus.Stopping && !stop.IsCompleted && !capture.Disposed && f.Inputs.Last().KeyDownCalls == 0,
                "restore Stop prematurely released actual native capture work");
            capture.Release.TrySetResult(); await Bound(stop); await Bound(pending);
            Check(candidate.TaskStatus == AutomationTaskStatus.Stopped && capture.Disposed && (await pending).Status != OperationStatus.Success, "late frame restored stopped candidate");
        }
        finally { capture.Release.TrySetResult(); }
        Console.WriteLine("Checkpoint restore actual-work PASS: Stopping/resources retained until noncooperative capture ends; late frame never starts Lua");
    }
    private static async Task RestoreCurrentManifestAsync()
    {
        await using var f = await Fixture.CreateAsync(script: RestoreScript, input: true); var source = await f.StartAsync();
        await Until(() => f.StoreState.GetState(source.InstanceScope, "after", 0) == 1); await f.Scheduler.StopTaskAsync(source.InstanceId);
        var m = source.CheckpointMetadata!; using var node = f.Registry.RegisterRestore(new(m.Installation, m.PluginVersion, m.Code.ContentHash, "ready", 1, new[] { "panel-ready" }), new RestoreValidator());
        string path = Path.Combine(f.Root, "plugins", "probe", "manifest.json");
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("\"version\":\"1\"", "\"version\":\"2\""));
        var choice = (await f.Scheduler.QueryCheckpointsAsync("probe")).Single();
        Check(choice.Compatibility == "ContentMismatch" && choice.SelectionId == Guid.Empty, "selection trusted stale catalog version after manifest changed");
        Console.WriteLine("Checkpoint selection current-manifest PASS: cached version/default/permission identity not reused");
    }
    private static async Task RestoreDiscoveryAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "checkpoint-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileCheckpointStore(root);
            var installation = new PluginInstallation("probe", Path.Combine(root, "installed"));
            var other = new PluginInstallation("probe", Path.Combine(root, "other"));
            var record = Record(CheckpointJson.InstallationId(installation), Guid.NewGuid());
            var second = Record(CheckpointJson.InstallationId(other), Guid.NewGuid());
            await store.PublishAsync(record, () => true, default); await store.PublishAsync(second, () => true, default);
            foreach (string mode in new[] { "legacy", "corrupt", "oversized" })
            {
                var invalid = record with { SourceInstanceId = Guid.NewGuid(), CheckpointId = Guid.NewGuid() };
                string path = store.Locate(Address(invalid));
                await WriteEnvelopeAsync(path, invalid with { SchemaVersion = mode == "legacy" ? 1 : CheckpointJson.SchemaVersion });
                if (mode == "corrupt") await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("\"step\":1", "\"step\":2"));
                if (mode == "oversized") await File.AppendAllTextAsync(path, new string(' ', CheckpointJson.RecordBytes));
            }
            await File.WriteAllTextAsync(Path.Combine(root, "unfinished.tmp"), "partial");
            await File.WriteAllTextAsync(Path.Combine(root, "unknown.json"), "{\"partial\":");
            var found = await store.QueryAsync(installation);
            Check(found.Count == 4 && found.Select(c => c.Status).Order().SequenceEqual(new[] { CheckpointReadStatus.Valid, CheckpointReadStatus.Corrupt, CheckpointReadStatus.UnsupportedFormat, CheckpointReadStatus.Oversized }.Order()),
                "discovery did not isolate installation and classify invalid/legacy/oversized records");
            Check((await store.QueryAsync(other)).Count == 1 && found.Count(c => c.Record is not null) == 1, "discovery crossed installation or trusted invalid metadata");
            Console.WriteLine("Checkpoint discovery PASS: bounded real files, installation isolation, corrupt/legacy/oversized reasons and temp/unattributable-fragment exclusion");
        }
        finally { Directory.Delete(root, true); }
    }
}
