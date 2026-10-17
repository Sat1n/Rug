using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;
using Scheduler = Rug.UI.Core.Services.TaskScheduler;

internal static class NativeCheckpointRestore
{
    private sealed class NoAnomaly : IAnomalyLogger
    { public Task<string> LogAnomalyAsync(TaskExecutionContext context, string reason, string detail) => throw new NotSupportedException(); }
    public static async Task RunAsync()
    {
        DirectoryInfo? repo = new(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Rug.slnx"))) repo = repo.Parent;
        string dll = Path.Combine(repo!.FullName, "x64", "Debug", "Rug.Core.dll");
        if (!OperatingSystem.IsWindows() || !File.Exists(dll)) { Console.WriteLine("Checkpoint Native NOT RUN: Windows/x64 Rug.Core unavailable"); return; }
        var assembly = typeof(InputService).Assembly;
        nint module = NativeLibrary.Load(dll, assembly, DllImportSearchPath.UseDllDirectoryForDependencies);
        NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name == "Rug.Core.dll" ? module : 0);
        string root = Path.Combine(AppContext.BaseDirectory, "native-checkpoint-" + Guid.NewGuid().ToString("N"));
        string plugin = Path.Combine(root, "plugins", "checkpoint_probe"); Directory.CreateDirectory(plugin);
        foreach (string file in new[] { "manifest.json", "main.lua" }) File.Copy(Path.Combine(repo.FullName, "plugins", "checkpoint_probe", file), Path.Combine(plugin, file));
        try
        {
            using var target = new RecoveryDiagnosticWindow(); nint hwnd = await target.Ready; target.SetReady(true);
            await using var files = new FileCheckpointStore(Path.Combine(root, "records"));
            var registry = new CheckpointNodeRegistry(); var node = CheckpointSamples.Node(new("checkpoint_probe", plugin));
            using var saveRegistration = registry.Register(node, new CheckpointSamples.GreenPanelValidator());
            using var restoreRegistration = registry.RegisterRestore(node, new CheckpointSamples.GreenRestoreValidator());
            var grants = new PluginAuthorization(); TestAuthorizationHost.ApproveCatalog(grants, Path.Combine(root, "plugins"));
            var states = new StateStore(); using var bus = new CommandBus(states);
            var arbiter = new InputArbiter();
            await using var scheduler = new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance), Path.Combine(root, "plugins"),
                () => new CaptureService(), () => new InputService(), (c, i) => new LuaRuntime(c, new FakeOcr(), i, NullLogger<LuaRuntime>.Instance, commandBus: bus),
                NullLogger<Scheduler>.Instance, new NoAnomaly(), commandBus: bus, authorization: grants, inputArbiter: arbiter,
                checkpoints: new CheckpointCoordinator(files, registry, grants), checkpointStore: files, checkpointRestores: registry);
            var id = await scheduler.StartTaskAsync("checkpoint_probe", hwnd);
            var source = scheduler.Instances.Single(c => c.InstanceId == id);
            await RecoveryTests.Until(() => states.GetState(source.InstanceScope, "checkpoint_saved", false) || source.IsBusinessClosed);
            RecoveryTests.Check(!source.IsBusinessClosed && target.KeyCounts == (1, 1) && target.AfterKeyCounts == (0, 0), "native BEFORE/save failed: " + source.LastError);
            await scheduler.StopTaskAsync(id);
            var choice = (await scheduler.QueryCheckpointsAsync("checkpoint_probe")).Single();
            var caller = await scheduler.CreateCheckpointRestoreContextAsync("checkpoint_probe", arbiter.BindTarget(hwnd));
            OperationRequest Request() => new(AutomationOperation.RestoreCheckpoint, JsonSerializer.SerializeToElement(new { selectionId = choice.SelectionId }),
                Guid.NewGuid(), "native-checkpoint-restore", new("checkpoint_probe"), DateTimeOffset.UtcNow.AddSeconds(20));
            target.SetReady(false);
            var rejected = await scheduler.Gateway.ExecuteAsync(caller, Request());
            RecoveryTests.Check(rejected.Error == OperationError.SceneRejected && target.AfterKeyCounts == (0, 0), "fresh red scene was not safely rejected: " + rejected);
            target.SetReady(true);
            var result = await scheduler.Gateway.ExecuteAsync(caller, Request());
            RecoveryTests.Check(result.Value is RunIdentity, "native green restore rejected: " + result);
            var run = (RunIdentity)result.Value!;
            await RecoveryTests.Until(() => states.GetState(run.InstanceScope, "checkpoint_restored", false));
            RecoveryTests.Check(target.KeyCounts == (1, 1) && target.AfterKeyCounts == (1, 1), "native restored run replayed BEFORE or omitted AFTER");
            var restored = scheduler.Instances.Single(c => c.InstanceId == run.InstanceId);
            await scheduler.StopTaskAsync(run.InstanceId);
            RecoveryTests.Check(!restored.Capture.IsCapturing && restored.CheckpointOrigin!.ParentInstanceId == id, "native resource cleanup/origin failed");
            Console.WriteLine("Checkpoint Native PASS: actual Lua/InputService/Rug.Core/PostMessage, fresh WGC red rejection/green restore, BEFORE B=(1,1) unchanged, AFTER C=(1,1), new identity/origin and cleanup.");
            Console.WriteLine("NOT human/WinUI acceptance: controlled target changed by test host; this native-only fixture does not install an intervention source. Independent Hook gap unchanged.");
        }
        finally { Directory.Delete(root, true); }
    }
}
