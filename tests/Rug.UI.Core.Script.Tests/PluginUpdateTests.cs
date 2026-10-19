using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Schema;
using Rug.UI.Core.Services;

internal sealed class TestPluginUiStage(Action? commit = null, Action? rollback = null, Action? dispose = null) : IPluginUiUpdate
{
    public Task CommitAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); commit?.Invoke(); return Task.CompletedTask; }
    public Task RollbackAsync() { rollback?.Invoke(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { dispose?.Invoke(); return ValueTask.CompletedTask; }
}
internal static partial class CheckpointTests
{
    private static Task<T> NextUpdate<T>(ChannelReader<T> reader) => reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    private const string UpdateScript = """
        function on_init() rug.log('update-init'); rug.set_state('started',true) end
        function on_tick() rug.sleep(60000) end
        function on_stop() rug.log('update-stop') end
        """;
    private static string UpdateUi(string title) => "{\"type\":\"Page\",\"id\":\"page\",\"title\":\"" + title + "\"}";
    public static async Task RunPluginUpdatesAsync()
    {
        await UpdatePreservesAsync(); await UpdateOrderingAsync(); await UpdateRecoveryStatesAsync(); await UpdatePinnedStartAsync(); await UpdateCapacityAsync();
        Console.WriteLine("Plugin updates PASS: immutable candidates, compile-only validation, old runs/UI/config/grants retained, UI-only pause/identity, staged rollback, bounded latest-save ordering");
    }
    private static async Task UpdatePreservesAsync()
    {
        await using var f = await Fixture.CreateAsync(script: UpdateScript);
        string dir = Path.Combine(f.Root, "plugins", "probe"), ui = Path.Combine(dir, "ui.json");
        await File.WriteAllTextAsync(ui, UpdateUi("Before"));
        var baseline = (await f.Scheduler.GetPluginSnapshotsAsync()).Single();
        var logs = new List<string>(); f.Log.Intercept = text => { lock (logs) logs.Add(text); };
        var run = await f.StartAsync(); await Until(() => f.StoreState.GetState(run.InstanceScope, "started", false));
        var result = Channel.CreateUnbounded<HotReloadEventArgs>(); var failure = Channel.CreateUnbounded<HotReloadFailedEventArgs>();
        await using var service = new HotReloadService(Path.Combine(f.Root, "plugins"), f.Scheduler, NullLogger<HotReloadService>.Instance, TimeSpan.FromMilliseconds(50)) { WatchFiles = false };
        service.Reloaded += (_, e) => result.Writer.TryWrite(e); service.ReloadFailed += (_, e) => failure.Writer.TryWrite(e);
        string visible = "Before"; int stages = 0, commits = 0; bool buildFails = false, commitFails = false;
        using var handler = service.RegisterUiHandler("probe", (snapshot, token) =>
        {
            if (buildFails) throw new IOException("controlled UI build failure");
            string next = new SchemaParser().Parse(snapshot.UiJson!).Title, old = visible; stages++;
            return Task.FromResult<IPluginUiUpdate>(new TestPluginUiStage(() => { visible = next; commits++; if (commitFails) throw new IOException("controlled UI commit failure"); },
                () => visible = old, () => stages--));
        });
        service.Start(); await service.Ready;
        foreach (var (name, bad) in new[] { ("main.lua", "function broken("), ("manifest.json", "{}"), ("config.json", "{bad"), ("ui.json", "{bad"),
            ("manifest.json", baseline.Candidate.Files["manifest.json"].Text!.Replace("\"probe\"", "\"imposter\"")),
            ("manifest.json", baseline.Candidate.Files["manifest.json"].Text!.Replace("\"checkpoint\"", "\"unknown.*\"")) })
        {
            string path = Path.Combine(dir, name); string? original = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
            await File.WriteAllTextAsync(path, bad); service.RequestReload(dir);
            await failure.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Scheduler.Instances.Single().InstanceId == run.InstanceId && !run.IsBusinessClosed && visible == "Before" && commits == 0,
                "invalid " + name + " disturbed old UI/run");
            await f.Scheduler.RefreshPluginsAsync();
            Check((await f.Scheduler.GetPluginSnapshotsAsync()).Single().ContentVersion == baseline.ContentVersion, "failed candidate poisoned effective cache");
            if (original is null) File.Delete(path); else await File.WriteAllTextAsync(path, original);
        }
        Check(f.Scheduler.PauseTask(run.InstanceId), "pause not accepted"); await Until(() => run.TaskStatus == AutomationTaskStatus.Paused);
        f.StoreState.SetState(run.InstanceScope, "retained", "value");
        await File.WriteAllTextAsync(ui, UpdateUi("After")); service.RequestReload(dir);
        Check((await NextUpdate(result.Reader)).Status == PluginUpdateStatus.UiApplied && visible == "After" && stages == 0 &&
            run.TaskStatus == AutomationTaskStatus.Paused && f.StoreState.GetState(run.InstanceScope, "retained", "") == "value", "UI update lost paused run state");
        service.RequestReload(dir); Check((await NextUpdate(result.Reader)).Status == PluginUpdateStatus.Unchanged && commits == 1, "unchanged content reapplied");
        foreach (bool afterBuild in new[] { false, true })
        {
            var old = (await f.Scheduler.GetPluginSnapshotsAsync()).Single();
            buildFails = !afterBuild; commitFails = afterBuild;
            await File.WriteAllTextAsync(ui, UpdateUi("Must not replace")); service.RequestReload(dir);
            await failure.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => stages == 0);
            Check(visible == "After" && (await f.Scheduler.GetPluginSnapshotsAsync()).Single().ContentVersion == old.ContentVersion, "UI build/commit failure lost old view/cache");
        }
        buildFails = commitFails = false;
        await File.WriteAllTextAsync(ui, UpdateUi("After"));
        await File.WriteAllTextAsync(Path.Combine(dir, "config.json"), """{"fields":[{"key":"new_default","type":"TextBox","default":"new"}]}""");
        service.RequestReload(dir); Check((await NextUpdate(result.Reader)).Status == PluginUpdateStatus.RuntimePending &&
            run.TaskStatus == AutomationTaskStatus.Paused && !(await f.Scheduler.GetPluginSnapshotsAsync()).Single().Manifest.Configuration!.ContainsKey("new_default"), "disk config replaced active defaults");
        File.Delete(Path.Combine(dir, "config.json"));
        await File.WriteAllTextAsync(Path.Combine(dir, "main.lua"), "rug.set_state('new_top',true)\n" + UpdateScript);
        service.RequestReload(dir); Check((await NextUpdate(result.Reader)).Status == PluginUpdateStatus.RuntimePending && run.TaskStatus == AutomationTaskStatus.Paused &&
            !f.StoreState.GetState(run.InstanceScope, "new_top", false), "compile/pending update executed chunk or replaced paused run");
        string manifestPath = Path.Combine(dir, "manifest.json"), manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"checkpoint\"", "\"checkpoint\",\"agent\""));
        service.RequestReload(dir); Check((await NextUpdate(result.Reader)).Status == PluginUpdateStatus.RuntimePending, "permission expansion applied while active");
        var install = run.CheckpointMetadata!.Installation;
        Check(!f.Authorization.Query(install).Granted.Contains(Permission.Agent) && !f.Authorization.Query(install).Requested.Contains(Permission.Agent), "pending permissions polluted effective authorization");
        f.Authorization.Revoke(install, [Permission.Capture]); await Bound(run.Completion);
        Check(run.TaskStatus == AutomationTaskStatus.Stopped, "pending update blocked revocation");
        // Remove the proposed expansion; explicitly publish only after the source has ended.
        await File.WriteAllTextAsync(manifestPath, manifest); service.RequestReload(dir);
        Check((await NextUpdate(result.Reader)).Status == PluginUpdateStatus.RuntimeApplied, "inactive runtime candidate not applied");
        TestAuthorizationHost.ApproveCatalog(f.Authorization, Path.Combine(f.Root, "plugins"));
        var next = await f.StartAsync(); await Until(() => f.StoreState.GetState(next.InstanceScope, "started", false));
        Check(f.StoreState.GetState(next.InstanceScope, "new_top", false) && next.InstanceId != run.InstanceId && stages == 0, "new explicit start did not use pinned code");
        lock (logs) Check(logs.Count(x => x.Contains("update-init")) == 2 && logs.Count(x => x.Contains("update-stop")) == 1, "update replayed lifecycle hooks");
    }
    private static async Task UpdateOrderingAsync()
    {
        await using var f = await Fixture.CreateAsync(script: UpdateScript);
        string dir = Path.Combine(f.Root, "plugins", "probe"), path = Path.Combine(dir, "ui.json");
        await File.WriteAllTextAsync(path, UpdateUi("Base")); await f.Scheduler.GetPluginSnapshotsAsync();
        var entered = Signal(); var release = Signal(); bool first = true;
        var pipeline = new PluginUpdatePipeline { BeforeValidation = async (_, _) => { if (first) { first = false; entered.TrySetResult(); await release.Task; } } };
        var results = Channel.CreateUnbounded<HotReloadEventArgs>(); string? applied = null;
        await using var service = new HotReloadService(Path.Combine(f.Root, "plugins"), f.Scheduler, NullLogger<HotReloadService>.Instance, TimeSpan.FromMilliseconds(50), pipeline) { WatchFiles = false };
        service.Reloaded += (_, e) => results.Writer.TryWrite(e);
        using var handler = service.RegisterUiHandler("probe", (snapshot, _) => Task.FromResult<IPluginUiUpdate>(new TestPluginUiStage(() => applied = snapshot.UiJson)));
        service.Start(); await service.Ready;
        await File.WriteAllTextAsync(path, UpdateUi("Old")); service.RequestReload(dir); await Bound(entered.Task);
        await File.WriteAllTextAsync(path, UpdateUi("Newest")); service.RequestReload(dir); release.TrySetResult();
        Check((await NextUpdate(results.Reader)).Status == PluginUpdateStatus.Superseded, "late old validation was not discarded");
        var latest = await NextUpdate(results.Reader); Check(latest.Status == PluginUpdateStatus.UiApplied && applied == UpdateUi("Newest"), "latest save was overwritten");
        pipeline.AfterValidation = async (_, _) => { await File.WriteAllTextAsync(path, UpdateUi("Unobserved disk")); pipeline.AfterValidation = null; };
        await File.WriteAllTextAsync(path, UpdateUi("Validated snapshot")); service.RequestReload(dir);
        await NextUpdate(results.Reader);
        Check(applied == UpdateUi("Validated snapshot") && (await f.Scheduler.GetPluginSnapshotsAsync()).Single().UiJson == applied && await File.ReadAllTextAsync(path) != applied,
            "application reread disk after validation");
        var snapshot = (await f.Scheduler.GetPluginSnapshotsAsync()).Single(); snapshot.Manifest.Permissions.Clear();
        Check(snapshot.Manifest.Permissions.Count > 0, "manifest leaked mutable reference");
        await File.WriteAllBytesAsync(Path.Combine(dir, "huge.lua"), new byte[PluginUpdatePipeline.MaxFileBytes + 1]);
        try { await pipeline.PrepareAsync(dir, 100); throw new Exception("unbounded candidate accepted"); } catch (InvalidDataException) { }
        File.Delete(Path.Combine(dir, "huge.lua"));
        string manifest = await File.ReadAllTextAsync(Path.Combine(dir, "manifest.json"));
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), "{\"entry\":\"entry.txt\"," + manifest[1..]);
        await File.WriteAllTextAsync(Path.Combine(dir, "entry.txt"), "function broken(");
        bool rejected = false;
        try { await pipeline.ValidateAsync(await pipeline.PrepareAsync(dir, 101)); } catch (NLua.Exceptions.LuaScriptException) { rejected = true; }
        Check(rejected, "non-.lua entry escaped compile validation");
        await File.WriteAllTextAsync(Path.Combine(dir, "entry.txt"), UpdateScript);
        Check((await pipeline.ValidateAsync(await pipeline.PrepareAsync(dir, 102))).EntryCode.Entry == "entry.txt", "valid non-.lua entry was not captured");
    }
    private static async Task UpdateRecoveryStatesAsync()
    {
        var archive = new UpdateArchiveBarrier();
        await using var f = await Fixture.CreateAsync(script: "function on_init() rug.agent.resolve_anomaly('update','scene') end\nfunction on_tick() rug.sleep(60000) end", agent: true, anomalyLogger: archive);
        await f.Scheduler.GetPluginSnapshotsAsync(); var run = await f.StartAsync(); await Bound(archive.Entered.Task);
        var pipeline = new PluginUpdatePipeline(); string dir = Path.Combine(f.Root, "plugins", "probe");
        try
        {
            await File.AppendAllTextAsync(Path.Combine(dir, "main.lua"), "\n-- proposed runtime");
            var snapshot = await pipeline.ValidateAsync(await pipeline.PrepareAsync(dir, 1));
            Check(await f.Scheduler.ClassifyPluginUpdateAsync(snapshot) == PluginUpdateStatus.RuntimePending && run.TaskStatus == AutomationTaskStatus.Recovering, "Recovering was not active");
            archive.Release.TrySetResult(); await Until(() => run.TaskStatus == AutomationTaskStatus.AwaitingHuman);
            Check(await f.Scheduler.ClassifyPluginUpdateAsync(snapshot) == PluginUpdateStatus.RuntimePending && run.TaskStatus == AutomationTaskStatus.AwaitingHuman, "AwaitingHuman was not active");
            await f.Scheduler.StopTaskAsync(run.InstanceId);
        }
        finally { archive.Release.TrySetResult(); }
    }
    private sealed class UpdateArchiveBarrier : IAnomalyLogger
    {
        public readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        public async Task<string> LogAnomalyAsync(TaskExecutionContext context, string reason, string detail) { Entered.TrySetResult(); await Release.Task; return "controlled-update-archive"; }
    }
    private static async Task UpdatePinnedStartAsync()
    {
        await using var f = await Fixture.CreateAsync(script: UpdateScript);
        var initial = (await f.Scheduler.GetPluginSnapshotsAsync()).Single();
        var run = await f.StartAsync(); await Until(() => f.StoreState.GetState(run.InstanceScope, "started", false));
        string dir = Path.Combine(f.Root, "plugins", "probe");
        await File.WriteAllTextAsync(Path.Combine(dir, "main.lua"), "error('unapplied disk code must never run')");
        await f.Scheduler.RefreshPluginsAsync();
        var next = await f.StartAsync(); await Until(() => f.StoreState.GetState(next.InstanceScope, "started", false));
        Check(next.Runtime.LoadedCode == initial.EntryCode && !next.IsBusinessClosed && !run.IsBusinessClosed,
            "normal start reread unapplied disk code or corrupted the old instance");
    }
    private static async Task UpdateCapacityAsync()
    {
        await using var f = await Fixture.CreateAsync(script: UpdateScript);
        await f.Scheduler.GetPluginSnapshotsAsync();
        var entered = Signal(); var release = Signal();
        var pipeline = new PluginUpdatePipeline { BeforeValidation = async (_, _) => { entered.TrySetResult(); await release.Task; } };
        await using var service = new HotReloadService(Path.Combine(f.Root, "plugins"), f.Scheduler, NullLogger<HotReloadService>.Instance, TimeSpan.FromMilliseconds(50), pipeline) { WatchFiles = false };
        var overflow = Signal(); service.ReloadFailed += (_, e) => { if (e.Error.Message.Contains("capacity")) overflow.TrySetResult(); };
        using var handler = service.RegisterUiHandler("probe", (_, _) => Task.FromResult<IPluginUiUpdate>(new TestPluginUiStage()));
        service.Start(); await service.Ready; service.RequestReload(Path.Combine(f.Root, "plugins", "probe")); await Bound(entered.Task);
        try
        {
            for (int i = 0; i <= HotReloadService.MaxPending; i++) service.RequestReload(Path.Combine(f.Root, "plugins", "queued-" + i));
            await Bound(overflow.Task);
            Check(service.PendingCount == HotReloadService.MaxPending && service.HandlerCount == 1, "unbounded update queue/handlers");
        }
        finally { release.TrySetResult(); }
        await service.DisposeAsync();
        Check(service.PendingCount == 0 && service.HandlerCount == 0, "update registrations remained after dispose");
    }
}
