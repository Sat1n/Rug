using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;
using Scheduler = Rug.UI.Core.Services.TaskScheduler;

internal static class NativeRecoveryTests
{
    public static async Task RunAsync()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Rug.slnx"))) repository = repository.Parent;
        string dll = Path.Combine(repository!.FullName, "x64", "Debug", "Rug.Core.dll");
        if (!OperatingSystem.IsWindows() || !File.Exists(dll))
        { Console.WriteLine("Native recovery NOT RUN: Windows desktop/x64 Rug.Core.dll unavailable"); return; }
        var assembly = typeof(InputService).Assembly;
        nint module = NativeLibrary.Load(dll, assembly, DllImportSearchPath.UseDllDirectoryForDependencies);
        NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name == "Rug.Core.dll" ? module : 0);
        string root = Path.Combine(AppContext.BaseDirectory, "native-recovery-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "recovery_probe"); Directory.CreateDirectory(directory);
        try
        {
            using var target = new RecoveryDiagnosticWindow();
            nint hwnd = await target.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
            { id = "recovery_probe", name = "Recovery", version = "1", targetProcess = "isolated", permissions = new[] { "agent", "vision.capture", "input", "timer" }, inputDelivery = "Win32PostMessage" }));
            await File.WriteAllTextAsync(Path.Combine(directory, "main.lua"), ManualRecovery.CreateScript("recovery_probe"));
            var authorization = new PluginAuthorization(); TestAuthorizationHost.ApproveCatalog(authorization, root);
            var arbiter = new InputArbiter();
            await using var source = new RawInputEventSource();
            await using var monitor = new InputInterventionCoordinator(source, arbiter, NullLogger<InputInterventionCoordinator>.Instance);
            await monitor.StartAsync();
            var store = new StateStore(); using var bus = new CommandBus(store);
            var registry = new RecoveryValidatorRegistry();
            using var registration = registry.Register(new("recovery_probe", directory), "controlled-ready", new FreshGreenFrameValidator());
            var logger = new AnomalyLogger(NullLogger<AnomalyLogger>.Instance, Path.Combine(root, "logs"));
            var coordinator = new RecoveryCoordinator(logger, authorization, registry);
            await using var scheduler = new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance), root,
                () => new CaptureService(), () => new InputService(),
                (capture, input) => new LuaRuntime(capture, new FakeOcr(), input, NullLogger<LuaRuntime>.Instance, commandBus: bus),
                NullLogger<Scheduler>.Instance, logger, commandBus: bus, authorization: authorization,
                inputArbiter: arbiter, intervention: monitor, recovery: coordinator);
            var id = await scheduler.StartTaskAsync("recovery_probe", hwnd);
            var context = scheduler.Instances.Single(c => c.InstanceId == id);
            await RecoveryTests.Until(() => context.TaskStatus == AutomationTaskStatus.AwaitingHuman || context.IsBusinessClosed);
            RecoveryTests.Check(context.TaskStatus == AutomationTaskStatus.AwaitingHuman && target.KeyCounts == (1, 1),
                "real Lua/InputService before-action or preserved handoff failed: " + context.LastError);
            var caller = await scheduler.Gateway.CreateContextAsync("recovery_probe", "native-test-host", AutomationSource.PluginUI, context.Identity);
            OperationRequest Request() => new(AutomationOperation.VerifyRecovery,
                JsonSerializer.SerializeToElement(new { recoveryId = context.Snapshot.Recovery!.RecoveryId }), Guid.NewGuid(),
                "native-verify", OperationTarget.ForRun(context.Identity), DateTimeOffset.UtcNow.AddSeconds(20));
            arbiter.ReturnControl();
            var rejected = await scheduler.Gateway.ExecuteAsync(caller, Request());
            RecoveryTests.Check(rejected.Value is RecoveryResult { Status: RecoveryStatus.NeedsHuman, Error: RecoveryError.ValidationRejected } && target.KeyCounts == (1, 1),
                "red panel incorrectly verified/resumed: " + rejected + "; lifecycle=" + context.Snapshot + "; error=" + context.LastError);
            target.SetReady(true); // Controlled host change, not human acceptance.
            arbiter.ReturnControl();
            var verified = await scheduler.Gateway.ExecuteAsync(caller, Request());
            RecoveryTests.Check(verified.Value is RecoveryResult { Status: RecoveryStatus.Resolved },
                "one green verification request did not resolve: " + verified);
            await RecoveryTests.Until(() => store.GetState(context.InstanceScope, "steps.after", 0) == 1);
            RecoveryTests.Check(target.KeyCounts == (2, 2) && store.GetState(context.InstanceScope, "steps.before", 0) == 1,
                "real original coroutine replayed/dropped physical input");
            var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(root, "logs"), "*.json").Single()));
            using (metadata)
            {
                RecoveryTests.Check(metadata.RootElement.GetProperty("verification").GetProperty("status").GetString() == "resolved" &&
                    metadata.RootElement.GetProperty("recoveryId").GetGuid() == context.Snapshot.Recovery!.RecoveryId &&
                    !metadata.RootElement.GetProperty("scriptContext").GetString()!.Contains("stack traceback"), "archive lacks verified recovery or persisted VM stack");
            }
            await scheduler.StopTaskAsync(id);
            RecoveryTests.Check(context.TaskStatus == AutomationTaskStatus.Stopped && coordinator.ActiveRecoveries == 0 && monitor.Snapshot.Sessions == 0 && !context.Capture.IsCapturing,
                "actual WGC/input recovery cleanup incomplete");
            Console.WriteLine("Native recovery PASS: real Lua → InputService/Rug.Core/PostMessage B=(1,1)→(2,2); production Raw source; fresh WGC red rejected/green verified; original init/state once; PNG/JSON and disposal");
            Console.WriteLine("Human acceptance NOT RUN: target color changed by trusted test host; no simulated event counts as human acceptance. Hook gap unchanged.");
        }
        finally { Directory.Delete(root, true); }
    }
}
