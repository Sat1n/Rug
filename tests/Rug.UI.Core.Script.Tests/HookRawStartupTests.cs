using Rug.UI.Core.Native;

internal static class HookRawStartupTests
{
    private static void Check(bool value, string message) => RawInputTests.Check(value, message);
    internal static async Task RunAsync()
    {
        foreach (var (stage, module, order) in new[] { ("both", "callback", "raw-first"),
            ("keyboard-native-callback", "original", "raw-first"), ("keyboard-native-callback", "callback", "bad"),
            ("keyboard-native-callback", "callback", "") })
        {
            try { HookRawStartup.Validate(stage, module, order); throw new Exception("Invalid startup control accepted"); }
            catch (ArgumentException) { }
        }
        if (!OperatingSystem.IsWindows() || !File.Exists(NativeKeyboardHookTests.LibraryPath) || !File.Exists(DiagnosticNativeRawInputPump.LibraryPath))
        { Console.WriteLine("NOT RUN: real Hook/Raw startup order; build native diagnostic DLLs first"); return; }
        foreach (string order in new[] { "hook-first", "raw-first" })
        {
            for (int run = 0; run < 3; run++) await LifecycleAsync(order);
            foreach (bool failHook in new[] { true, false }) await LifecycleAsync(order, failHook: failHook, failRaw: !failHook);
        }
        Console.WriteLine("Hook/Raw operation order, real registrations, deferred single installation, phase clocks, failures and full cleanup passed (6 success + 4 failure lifecycles; no software AUTO)");
    }
    private static async Task LifecycleAsync(string order, bool failHook = false, bool failRaw = false)
    {
        using var target = new RawInputDiagnosticWindow(false, false);
        using var hook = new RawInputDiagnosticWindow(true, false, messageOnly: true,
            nativeKeyboardPath: NativeKeyboardHookTests.LibraryPath, deferKeyboardHook: true);
        await Task.WhenAll(target.Ready.Task, hook.Ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await using var raw = new DiagnosticNativeRawInputPump();
        var startup = new HookRawStartup();
        Check(!hook.Status.KeyboardInstalled && hook.NativeKeyboard!.Status.Attached == 0
            && !WindowsRawInputReceiver.Registrations().Any(d => d.Page == 1 && d.Usage is 2 or 6), "Deferred readiness installed a Hook or registration");
        Check(WindowsRawInputReceiver.PostMessageW(hook.Ready.Task.Result, 0x8003, 0, 0), "Could not post unrequested command");
        await hook.SynchronizeAsync();
        Check(!hook.Status.KeyboardInstalled && hook.NativeKeyboard!.Status.Attached == 0, "Unrequested command installed Hook before its phase");
        bool hookEntered = false, rawEntered = false;
        if (failHook) hook.FailDeferredHookForTest();
        WindowsRawInputReceiver.Device[] foreign = [new() { Page = 1, Usage = 6, Flags = 0x2100, Target = target.Ready.Task.Result }];
        if (failRaw) Check(WindowsRawInputReceiver.RegisterRawInputDevices(foreign, 1, (uint)System.Runtime.InteropServices.Marshal.SizeOf<WindowsRawInputReceiver.Device>()), "Could not create real registration conflict");
        try
        {
            async Task Install()
            {
                hookEntered = true;
                Check(raw.Status.PumpReady == (order == "raw-first"), "Hook entered before expected Raw readiness");
                await Task.WhenAll(hook.InstallDeferredKeyboardHookAsync(), hook.InstallDeferredKeyboardHookAsync());
                Check(hook.Status.KeyboardInstalled && hook.NativeKeyboard!.Status.Attached == 1
                    && hook.NativeKeyboard.Status.ThreadId == hook.Status.InstallThreadId && hook.NativeKeyboard.Status.ThreadMismatch == 0
                    && hook.HookInstallModule == hook.NativeKeyboard.ProcedureModule && hook.HookInstallModule != hook.WindowInstanceModule,
                    "Deferred installation changed binding/module or installed twice");
            }
            async Task Register()
            {
                rawEntered = true;
                Check(hook.Status.KeyboardInstalled == (order == "hook-first"), "Raw entered before expected Hook installation");
                await Task.WhenAll(raw.StartAsync(), raw.StartAsync());
                var owned = WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).ToArray();
                Check(raw.Status is { PumpReady: true, RegistrationEnabled: true, PacketReadingEnabled: false, Evidence: null }
                    && owned.Length == 1 && owned[0].Usage == 6 && owned[0].Target == raw.Status.Hwnd && (owned[0].Flags & ~0x2000u) == 0x100,
                    "Raw phase completed without expected registration");
            }
            Exception? error = null;
            try { await startup.StartAsync(order, Install, Register); } catch (Exception failure) { error = failure; }
            var steps = startup.Snapshot;
            if (failHook || failRaw)
            {
                Check(error is not null && !steps[^1].Succeeded && steps[^1].ErrorType is not null,
                    "Startup failure lost phase evidence");
                bool firstFailed = order == "hook-first" ? failHook : failRaw;
                Check(steps.Length == (firstFailed ? 1 : 2) && (firstFailed ? hookEntered != rawEntered : hookEntered && rawEntered),
                    "Failed first phase entered second phase");
                if (failHook && hookEntered)
                {
                    await hook.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(hook.Status.Failure!.Contains("Controlled deferred Hook") && hook.NativeKeyboard!.Status.Attached == 0,
                        "Post-ready installation failure was hidden");
                }
                if (failRaw && rawEntered)
                {
                    await raw.DisposeAsync();
                    Check(raw.Status.Failure is not null && raw.Status.ThreadExited, "Registration failure was hidden");
                }
            }
            else
            {
                Check(error is null && steps.Length == 2 && steps.All(s => s.Succeeded && s.BeforeTicks > 0 && s.AfterTicks >= s.BeforeTicks)
                    && steps[0].AfterTicks <= steps[1].BeforeTicks
                    && steps[0].Name == (order == "hook-first" ? "hook-install" : "raw-register-ready"), "Phase order/timing incorrect");
                var hookStep = steps.Single(s => s.Name == "hook-install");
                Check(hook.KeyboardInstallStartedTicks >= hookStep.BeforeTicks && hook.KeyboardInstallCompletedTicks <= hookStep.AfterTicks
                    && hook.KeyboardInstallCompletedTicks >= hook.KeyboardInstallStartedTicks
                    && new[] { target.Status.InstallThreadId, hook.Status.InstallThreadId, raw.Status.ThreadId }.Distinct().Count() == 3,
                    "Actual Hook installation outside phase or thread topology changed");
                steps[0] = steps[0] with { Name = "mutated" }; Check(startup.Snapshot[0].Name != "mutated", "Phase snapshot shared mutable storage");
            }
            try { await startup.StartAsync(order, Install, Register); throw new Exception("Repeated startup accepted"); }
            catch (InvalidOperationException) { }
        }
        finally
        {
            await raw.DisposeAsync(); hook.Dispose();
            if (failRaw)
            {
                Check(WindowsRawInputReceiver.Registrations().Single(d => d.Page == 1 && d.Usage == 6).Target == foreign[0].Target, "Conflict cleanup removed foreign registration");
                WindowsRawInputReceiver.Device[] remove = [new() { Page = 1, Usage = 6, Flags = 1 }];
                Check(WindowsRawInputReceiver.RegisterRawInputDevices(remove, 1, (uint)System.Runtime.InteropServices.Marshal.SizeOf<WindowsRawInputReceiver.Device>()), "Foreign registration cleanup failed");
            }
            target.Dispose();
        }
        await raw.DisposeAsync(); hook.Dispose();
        Check(raw.Status.Hwnd == 0 && (!rawEntered || raw.Status.ThreadExited) && hook.Status.ThreadExited && target.Status.ThreadExited
            && hook.NativeKeyboard!.Status.Attached == 0 && !WindowsRawInputReceiver.Registrations().Any(d => d.Page == 1 && d.Usage is 2 or 6),
            "Ordered lifecycle leaked resources");
        try { await hook.InstallDeferredKeyboardHookAsync(); throw new Exception("Stopped Hook revived"); } catch (ObjectDisposedException) { }
    }
}
