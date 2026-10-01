using System.Runtime.InteropServices;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;

/// <summary>Opt-in, aggregate-only desktop diagnostic; independent hook counters detect interference.</summary>
internal static class RawInputDiagnostics
{
    private static bool LoadNative()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("NOT RUN: Windows desktop required"); return false; }
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Rug.slnx"))) root = root.Parent;
        string? dll = root is null ? null : Path.Combine(root.FullName, "x64", "Debug", "Rug.Core.dll");
        if (dll is null || !File.Exists(dll)) { Console.WriteLine("NOT RUN: x64/Debug/Rug.Core.dll unavailable"); return false; }
        var assembly = typeof(InputService).Assembly;
        nint library = NativeLibrary.Load(dll, assembly, DllImportSearchPath.UseDllDirectoryForDependencies);
        NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name == "Rug.Core.dll" ? library : 0);
        return true; // Cached generated imports require this module until process exit.
    }
    private static void Print(RawInputEventSource source, string label)
    {
        var s = source.Snapshot;
        Console.WriteLine($"{label}: state={s.State} generation={s.Generation} fault={s.Fault} keyboard={s.Keyboard} mouse={s.Mouse} deviceChanges={s.DeviceChanges} coalesced={s.Coalesced} overflow={s.Overflows} consumerErrors={s.ConsumerErrors} buffered={s.Buffered} subscribers={s.Subscribers}");
        if (s.Error is not null) Console.WriteLine(s.Error);
    }
    public static async Task RunAsync(bool human)
    {
        if (!LoadNative()) { Environment.ExitCode = 2; return; }
        await using var source = new RawInputEventSource();
        using var window = new RawInputDiagnosticWindow();
        nint hwnd = await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long keyDown = 0, keyUp = 0, repeats = 0, foreground = 0, background = 0, zeroDevice = 0, tagged = 0;
        long movement = 0, buttonsDown = 0, buttonsUp = 0, wheels = 0;
        source.InputReceived += (_, e) =>
        {
            if (e.Observation.IsCandidateInput)
            {
                if (e.Observation.DeviceId == 0) Interlocked.Increment(ref zeroDevice);
                if (e.Observation.ExtraInformation != 0) Interlocked.Increment(ref tagged);
                if (e.Observation.Foreground) Interlocked.Increment(ref foreground); else Interlocked.Increment(ref background);
                if (e.Observation.Kind == InputEventKind.Mouse)
                {
                    var mouse = e.Observation;
                    if (mouse.X != 0 || mouse.Y != 0 || (mouse.MouseFlags & 1) != 0) Interlocked.Add(ref movement, e.Samples);
                    Interlocked.Add(ref buttonsDown, System.Numerics.BitOperations.PopCount((uint)(mouse.ButtonFlags & 0x155)));
                    Interlocked.Add(ref buttonsUp, System.Numerics.BitOperations.PopCount((uint)(mouse.ButtonFlags & 0x2aa)));
                    if ((mouse.ButtonFlags & 0xc00) != 0) Interlocked.Increment(ref wheels);
                }
                if (e.Observation.Kind == InputEventKind.Keyboard)
                {
                    if (e.Observation.KeyReleased) Interlocked.Increment(ref keyUp); else Interlocked.Increment(ref keyDown);
                    if (e.Observation.IsRepeat) Interlocked.Increment(ref repeats);
                }
            }
        };
        source.StateChanged += (_, _) => Print(source, "STATUS");
        await source.StartAsync(); Print(source, "READY");
        using var displayStop = new CancellationTokenSource();
        Task display = human ? Task.Run(async () =>
        {
            try
            {
                while (true) { await Task.Delay(1000, displayStop.Token); Print(source, "COUNTS");
                    Console.WriteLine($"down={Interlocked.Read(ref keyDown)} up={Interlocked.Read(ref keyUp)} repeat={Interlocked.Read(ref repeats)} foreground={Interlocked.Read(ref foreground)} background={Interlocked.Read(ref background)} moveSamples={Interlocked.Read(ref movement)} buttonDown/Up={Interlocked.Read(ref buttonsDown)}/{Interlocked.Read(ref buttonsUp)} wheelPackets={Interlocked.Read(ref wheels)}"); }
            }
            catch (OperationCanceledException) { }
        }) : Task.CompletedTask;
        try
        {
            if (human)
            {
                Console.WriteLine("HUMAN BEFORE: activate the Rug Raw Input diagnostic window. Press/release/hold a key, move/click/scroll. Alt+Tab to another app and repeat. Normal input must keep working. Optionally unplug/replug a mouse. Return here and type next. No key contents or trajectories are printed or saved.");
                if (await Task.Run(Console.ReadLine) != "next") { Console.WriteLine("NOT RUN: human session aborted"); Environment.ExitCode = 2; return; }
                if (source.Snapshot.Keyboard == 0 || source.Snapshot.Mouse == 0)
                { Console.WriteLine("NOT RUN: missing human keyboard/mouse evidence BEFORE automatic input"); Environment.ExitCode = 2; return; }
                Console.WriteLine("AUTO: hands off all keyboard/mouse now. Countdown 3 seconds; listener stays active throughout.");
                await Task.Delay(3000);
            }
            bool verified = await SelfInputAsync(source, window, hwnd);
            Print(source, "AFTER AUTO");
            Console.WriteLine($"Raw diagnostic aggregates: zeroDevice={Interlocked.Read(ref zeroDevice)}, nonzeroExtraInfo={Interlocked.Read(ref tagged)}");
            if (!verified) Environment.ExitCode = 2;
            if (human)
            {
                var before = source.Snapshot;
                Console.WriteLine("HUMAN AFTER: repeat keyboard, mouse, foreground/background and shortcut checks in this SAME running session. Return here and type done.");
                string? answer = await Task.Run(Console.ReadLine);
                await source.SynchronizeAsync();
                var after = source.Snapshot;
                Console.WriteLine(answer == "done" && after.State == InputEventSourceState.Ready && after.Keyboard > before.Keyboard && after.Mouse > before.Mouse
                    ? "HUMAN AFTER observed keyboard/mouse. Foreground/background and normal operation require your visual confirmation."
                    : "NOT RUN: incomplete HUMAN AFTER evidence");
                if (answer != "done" || after.State != InputEventSourceState.Ready || after.Keyboard <= before.Keyboard || after.Mouse <= before.Mouse) Environment.ExitCode = 2;
            }
            else Console.WriteLine("HUMAN BEFORE -> AUTO -> HUMAN AFTER: NOT RUN / pending human acceptance (--raw-input-diagnostic)");
        }
        finally
        {
            displayStop.Cancel(); await display; await source.StopAsync(); Print(source, "STOPPED");
            Console.WriteLine($"Keyboard/mouse registrations remaining in this process: {WindowsRawInputReceiver.Registrations().Count(d => d.Page == 1 && d.Usage is 2 or 6)}");
        }
    }
    private static async Task<bool> SelfInputAsync(RawInputEventSource source, RawInputDiagnosticWindow window, nint hwnd)
    {
        await using var native = new InputService();
        await native.SetHumanizeConfigAsync(new HumanizeConfig { EnableJitter = false, MinClickHoldMs = 10, MaxClickHoldMs = 10, MinKeyHoldMs = 10, MaxKeyHoldMs = 10 });
        bool success = true;
        foreach (bool post in new[] { false, true })
        {
            if (!post && !window.Activate()) { Console.WriteLine("SendInput NOT RUN: Windows foreground policy blocked isolated window activation. Activate it manually and rerun."); success = false; continue; }
            await source.SynchronizeAsync(); await window.SynchronizeAsync();
            var raw = source.Snapshot; var legacy = window.Counts;
            await native.SetTargetAsync(hwnd, post);
            // Two distinct points ensure an unchanged cursor cannot make legacy move evidence disappear.
            await native.MoveMouseAsync(60, 60, smooth: false);
            await native.MoveMouseAsync(80, 80, smooth: false);
            await native.ClickAsync(holdMs: 10);
            await native.KeyDownAsync(119);
            try { } finally { await native.KeyUpAsync(119); await native.MouseUpAsync(MouseButton.Left); }
            bool delivered = true;
            try
            {
                await RawInputTests.Until(() => window.Counts.KeyDown > legacy.KeyDown && window.Counts.KeyUp > legacy.KeyUp
                    && window.Counts.MouseDown > legacy.MouseDown && window.Counts.MouseUp > legacy.MouseUp && window.Counts.Move > legacy.Move);
            }
            catch (OperationCanceledException) { delivered = false; }
            await window.SynchronizeAsync(); await source.SynchronizeAsync();
            var end = source.Snapshot; var probe = window.Counts;
            string mode = post ? "PostMessage" : "SendInput";
            long candidates = end.Keyboard - raw.Keyboard + end.Mouse - raw.Mouse;
            long physicalLike = probe.Unflagged - legacy.Unflagged;
            Console.WriteLine($"{mode}: candidates={candidates}, unflaggedHookActivity={physicalLike}, injectedKeyboard={probe.InjectedKeyboard - legacy.InjectedKeyboard}, injectedMouse={probe.InjectedMouse - legacy.InjectedMouse}, legacyMove={probe.Move - legacy.Move}, mouseDown/Up={probe.MouseDown - legacy.MouseDown}/{probe.MouseUp - legacy.MouseUp}, keyDown/Up={probe.KeyDown - legacy.KeyDown}/{probe.KeyUp - legacy.KeyUp}");
            if (!delivered) { Console.WriteLine("NOT RUN: incomplete actual target-message evidence; round cannot pass."); success = false; }
            if (physicalLike > 0) { Console.WriteLine("INTERFERED: unflagged user-like activity during this round; no self-input verdict. NOT RUN / rerun hands off."); success = false; continue; }
            RawInputTests.Check(end.State == InputEventSourceState.Ready && end.Overflows == 0, "Listener lost readiness/continuity during software verification");
            if (!post && (probe.InjectedKeyboard - legacy.InjectedKeyboard < 2 || probe.InjectedMouse - legacy.InjectedMouse < 3))
            { Console.WriteLine("INCONCLUSIVE: independent injected-input probe did not observe the complete sequence; preserve Raw Input evidence, do not label as human interference."); success = false; }
            if (candidates != 0)
            { Console.WriteLine("FAIL / candidate observations during software round: source currently reports all Raw Input, with no authenticated origin or injected flag. No time filtering or listener pause applied."); success = false; }
            else if (delivered) Console.WriteLine($"{mode}: PASS (real Rug.Core input including releases, live Raw Input source)");
        }
        return success;
    }

}
