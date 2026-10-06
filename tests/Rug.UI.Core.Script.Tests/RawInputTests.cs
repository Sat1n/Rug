using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;

internal static class RawInputTests
{
    internal static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }
    public static async Task RunAsync()
    {
        Parse();
        ProbeCounters();
        await ProbeLifecycleAsync();
        await SplitProbeLifecycleAsync();
        await UnregisteredPumpLifecycleAsync();
        await DirectPumpLifecycleAsync();
        await MessagePumpLifecycleAsync();
        await MessagePumpLifecycleAsync(registerKeyboard: false);
        await MessagePumpLifecycleAsync(registerMouse: false);
        await MessagePumpLifecycleAsync(registerMouse: false, native: true);
        await MessagePumpLifecycleAsync(registerMouse: false, native: true, managedKeyboard: false);
        await LifecycleAsync();
        await ConsumersAsync();
        await NativeLifecycleAsync();
        Console.WriteLine("Raw Input parsing, generations, bounded delivery, consumers and registration ownership tests passed");
    }
    private static void ProbeCounters()
    {
        foreach (bool keyboard in new[] { true, false })
        {
            var counter = new RawInputDiagnosticWindow.HookCounter();
            counter.Observe(-1, (nint)1, keyboard); // An unreadable sentinel must never be dereferenced.
            Check(counter.Snapshot is { CallbackTotal: 1, ActionableTotal: 0, ParseErrors: 0 }, "negative nCode read native memory");
            counter.Observe(1, (nint)1, keyboard);
            Check(counter.Snapshot.ActionableTotal == 0, "non-action code was parsed");
            nint data = Marshal.AllocHGlobal(32);
            try
            {
                int flagsOffset = keyboard ? 8 : 12, extraOffset = keyboard ? 16 : IntPtr.Size == 8 ? 24 : 20;
                uint injectedFlag = keyboard ? 0x10u : 1u;
                var baseline = counter.Snapshot;
                foreach (bool injected in new[] { false, true }) foreach (bool tagged in new[] { false, true })
                {
                    Marshal.WriteInt32(data, flagsOffset, injected ? (int)injectedFlag : 0);
                    Marshal.WriteIntPtr(data, extraOffset, tagged ? (nint)InputObservation.RugSoftwareInputTag : 0);
                    counter.Observe(0, data, keyboard);
                }
                counter.Observe(0, 0, keyboard);
                var delta = counter.Snapshot.Delta(baseline);
                Check(delta is { CallbackTotal: 5, ActionableTotal: 5, Injected: 2, Unflagged: 2,
                    TaggedTotal: 2, TaggedInjected: 1, TaggedUnflagged: 1, ParseErrors: 1 }, "hook intersections or parse errors lost");
                Check(baseline.CallbackTotal == 2 && baseline.ActionableTotal == 0, "snapshot mutated");
                counter.Timing(12, false); counter.Timing(4, false); counter.Timing(18, true);
                Check(counter.Snapshot is { OwnTicks: 16, OwnMaxTicks: 12, ChainTicks: 18, ChainMaxTicks: 18 }, "timing aggregates incorrect");
            }
            finally { Marshal.FreeHGlobal(data); }
        }
        try { RawInputDiagnosticWindow.CheckHookInstallation(0, 5, "Keyboard hook"); throw new Exception("install error hidden"); }
        catch (System.ComponentModel.Win32Exception e) { Check(e.NativeErrorCode == 5 && e.Message.Contains("win32Error=5"), "registration error code lost"); }
        Check(RawInputDiagnosticWindow.CheckHookInstallation(1, 0, "Mouse hook") == 1, "successful installation rejected");
        Check(RawInputDiagnostics.CompareKeyCallbacks(0, 2).Contains("key-specific/hotkey"), "selective F8 failure branch incorrect");
        Check(RawInputDiagnostics.CompareKeyCallbacks(0, 0).Contains("independent x64 native"), "both missing comparison branch incorrect");
        Check(RawInputDiagnostics.CompareKeyCallbacks(null, 2).StartsWith("INCONCLUSIVE"), "unrun/interfered round inferred a cause");
        Check(RawInputDiagnostics.CompareKeyCallbacks(2, 2).Contains("each round"), "callback presence bypassed per-round acceptance");
        var raw = new RawInputDiagnostics.RawEvidence(3, 4, 2, 5, 1, 1);
        Check(raw.Delta(new(1, 1, 0, 0, 0, 0)) == new RawInputDiagnostics.RawEvidence(2, 3, 2, 5, 1, 1), "phase raw evidence delta incorrect");
    }
    private static async Task ProbeLifecycleAsync()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("NOT RUN: native diagnostic probe lifecycle requires Windows"); return; }
        long generation;
        Check(RawInputDiagnostics.IsolationStage("load") == (false, false, false), "load stage creates receiver");
        Check(RawInputDiagnostics.IsolationStage("target") == (true, false, false), "target stage enables hooks");
        Check(RawInputDiagnostics.IsolationStage("keyboard") == (true, true, false), "keyboard stage enables mouse hook");
        Check(RawInputDiagnostics.IsolationStage("both") == (true, true, true), "both stage mapping incorrect");
        Check(RawInputDiagnostics.IsolationStage("both-raw") == (true, true, true), "both-raw changed hook selection");
        Check(RawInputDiagnostics.IsolationStage("both-raw-split") == (true, true, true), "split stage changed requested components");
        Check(RawInputDiagnostics.IsolationStage("target-raw") == (true, false, false), "target-raw requested managed Hooks");
        Check(RawInputDiagnostics.IsolationStage("keyboard-raw") == (true, true, false), "keyboard-raw requested wrong Hooks");
        Check(RawInputDiagnostics.IsolationStage("keyboard-split") == (true, true, false)
            && RawInputDiagnostics.UsesSplitProbe("keyboard-split") && !RawInputDiagnostics.UsesRawInput("keyboard-split")
            && RawInputDiagnostics.UsesSplitProbe("keyboard-raw") && RawInputDiagnostics.UsesRawInput("keyboard-raw"),
            "no-source control changed target/Hook placement or enables Raw Input");
        Check(RawInputDiagnostics.IsolationStage("keyboard-pump") == (true, true, false)
            && RawInputDiagnostics.UsesSplitProbe("keyboard-pump") && !RawInputDiagnostics.UsesRawInput("keyboard-pump"),
            "unregistered pump changed Hook selection or registers Raw Input");
        Check(RawInputDiagnostics.IsolationStage("keyboard-direct") == (true, true, false)
            && RawInputDiagnostics.UsesSplitProbe("keyboard-direct") && !RawInputDiagnostics.UsesRawInput("keyboard-direct"),
            "direct pump changed Hook selection or creates an event-source queue");
        Check(RawInputDiagnostics.IsolationStage("keyboard-messages") == (true, true, false)
            && RawInputDiagnostics.UsesSplitProbe("keyboard-messages") && !RawInputDiagnostics.UsesRawInput("keyboard-messages"),
            "message-only registered control changed Hook selection or creates an event source");
        Check(RawInputDiagnostics.IsolationStage("keyboard-messages-mouse") == (true, true, false)
            && RawInputDiagnostics.UsesSplitProbe("keyboard-messages-mouse") && !RawInputDiagnostics.UsesRawInput("keyboard-messages-mouse"),
            "mouse-only registration changed Hook selection or creates an event source");
        try { _ = new DiagnosticRawInputPump(registerDevices: true, registerKeyboard: false); throw new Exception("mouse-only packet mode accepted"); } catch (ArgumentException) { }
        Check(RawInputDiagnostics.IsolationStage("keyboard-messages-keyboard") == (true, true, false)
            && RawInputDiagnostics.UsesSplitProbe("keyboard-messages-keyboard") && !RawInputDiagnostics.UsesRawInput("keyboard-messages-keyboard"),
            "keyboard-only registration changed Hook selection or creates an event source");
        try { _ = new DiagnosticRawInputPump(registerDevices: true, registerMouse: false); throw new Exception("keyboard-only packet mode accepted"); } catch (ArgumentException) { }
        try { _ = new DiagnosticRawInputPump(registerDevices: true, readPackets: false, registerKeyboard: false, registerMouse: false); throw new Exception("empty device selection accepted"); } catch (ArgumentException) { }
        Check(RawInputDiagnostics.IsolationStage("keyboard-native-messages") == (true, true, false)
            && RawInputDiagnostics.UsesSplitProbe("keyboard-native-messages") && !RawInputDiagnostics.UsesRawInput("keyboard-native-messages"),
            "native receiver changed Hook selection or creates a production source");
        Check(RawInputDiagnostics.IsolationStage("target-native-messages") == (true, false, false)
            && RawInputDiagnostics.UsesSplitProbe("target-native-messages") && !RawInputDiagnostics.UsesRawInput("target-native-messages"),
            "no-Hook native receiver changed target/thread selection or creates a production source");
        var aggregate = new RawInputDiagnostics.RawAggregate();
        var baseline = aggregate.Read();
        aggregate.Observe(new(1, 1, 1, new(InputEventKind.Keyboard, 1, false, ExtraInformation: InputObservation.RugSoftwareInputTag)));
        aggregate.Observe(new(1, 2, 2, new(InputEventKind.Keyboard, 1, false, KeyFlags: 1, ExtraInformation: InputObservation.RugSoftwareInputTag)));
        aggregate.Observe(new(1, 3, 3, new(InputEventKind.Mouse, 1, false, ExtraInformation: InputObservation.RugSoftwareInputTag), 5));
        aggregate.Observe(new(1, 4, 4, new(InputEventKind.Keyboard, 1, true)));
        aggregate.Observe(new(1, 5, 5, new(InputEventKind.Mouse, 1, true), 3));
        aggregate.Observe(new(1, 6, 6, new(InputEventKind.DeviceArrived, 1, false)));
        Check(aggregate.Read().Delta(baseline) == new RawInputDiagnostics.RawEvidence(1, 3, 2, 5, 1, 1),
            "isolation aggregate mixed candidate/software, dropped merged samples or counted device change");
        Parallel.For(0, 1000, _ => aggregate.Observe(new(1, 7, 7, new(InputEventKind.Keyboard, 1, true))));
        Check(aggregate.Read().CandidateKeyboard == 1001 && baseline == new RawInputDiagnostics.RawEvidence(0, 0, 0, 0, 0, 0),
            "concurrent isolation counts or detached snapshot incorrect");
        var copied = new RawInputDiagnostics.RawAggregate();
        copied.ObserveCopied(new(InputEventKind.Keyboard, 1, false, ExtraInformation: InputObservation.RugSoftwareInputTag));
        copied.ObserveCopied(new(InputEventKind.Keyboard, 1, false, KeyFlags: 1, ExtraInformation: InputObservation.RugSoftwareInputTag));
        copied.ObserveCopied(new(InputEventKind.Mouse, 1, false, ExtraInformation: InputObservation.RugSoftwareInputTag), 5);
        copied.ObserveCopied(new(InputEventKind.Keyboard, 1, true));
        copied.ObserveCopied(new(InputEventKind.Mouse, 1, true), 3);
        copied.ObserveCopied(new(InputEventKind.DeviceArrived, 1, false));
        Check(copied.Read() == new RawInputDiagnostics.RawEvidence(1, 3, 2, 5, 1, 1), "direct copied aggregate lost classification or releases");
        var detached = copied.Read();
        Parallel.For(0, 1000, _ => copied.ObserveCopied(new(InputEventKind.Keyboard, 1, true)));
        Check(copied.Read().CandidateKeyboard == 1001 && detached.CandidateKeyboard == 1, "direct copied snapshot mutated or concurrent counts lost");
        try { RawInputDiagnostics.IsolationStage("bad"); throw new Exception("unknown isolation stage accepted"); } catch (ArgumentException) { }
        foreach (var selection in new[] { (false, false), (true, false), (true, true) })
        {
            using var isolated = new RawInputDiagnosticWindow(selection.Item1, selection.Item2);
            await isolated.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(isolated.QueryTargetMetricForTest(0) == RawInputDiagnosticWindow.TargetMetricMagic, "target protocol unavailable");
            var beforeMetrics = Enumerable.Range(1, 5).Select(i => isolated.QueryTargetMetricForTest((uint)i)).ToArray();
            isolated.PostTargetMessagesForTest(); await isolated.SynchronizeAsync();
            for (int i = 1; i <= 5; i++) Check(isolated.QueryTargetMetricForTest((uint)i) > beforeMetrics[i - 1], "target message metric missing");
            Check(isolated.Status.KeyboardInstalled == selection.Item1 && isolated.Status.MouseInstalled == selection.Item2,
                "isolation receiver installed unrequested hooks");
            await isolated.SynchronizeAsync(); isolated.Dispose();
            Check(isolated.Status is { ThreadExited: true, Failure: null, ExitReason: "WM_QUIT" }, "isolation cleanup failed");
        }
        using (var probe = new RawInputDiagnosticWindow())
        {
            await probe.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var status = probe.Status; generation = status.Generation;
            Check(status.InstallThreadId != 0 && status.KeyboardInstalled && status.MouseInstalled && !status.ThreadExited && status.Failure is null, "probe installed status incomplete");
            probe.InjectThreadFailureForTest();
            var stopped = await probe.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(probe.Ready.Task.IsCompletedSuccessfully && stopped.ThreadExited && stopped.ExitReason == "Fault" && stopped.Failure!.Contains("Controlled"), "post-ready fault was swallowed");
            try { await probe.SynchronizeAsync(); throw new Exception("dead message thread accepted barrier"); } catch (InvalidOperationException) { }
            probe.Dispose(); probe.Dispose();
        }
        using var next = new RawInputDiagnosticWindow();
        await next.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(next.Status.Generation > generation, "probe generation reused");
        await next.SynchronizeAsync(); // Proves pump/barrier only, not keyboard hook health.
        next.Dispose();
        Check(next.Status is { ThreadExited: true, Failure: null, ExitReason: "WM_QUIT" }, "normal probe cleanup not recorded");
    }
    // @shape Three real lifecycles per selection; independent window/Hook threads, optional Raw Input.
    // @source Rug.UI.Core/Native/RawInputDiagnosticWindow.cs#class:RawInputDiagnosticWindow
    private static async Task SplitProbeLifecycleAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var hooks in new[] { (Keyboard: true, Mouse: true, Raw: true), (Keyboard: false, Mouse: false, Raw: true),
            (Keyboard: true, Mouse: false, Raw: true), (Keyboard: true, Mouse: false, Raw: false) })
        for (int run = 0; run < 3; run++)
        {
            using var target = new RawInputDiagnosticWindow(false, false);
            using var probe = new RawInputDiagnosticWindow(hooks.Keyboard, hooks.Mouse, messageOnly: true);
            await Task.WhenAll(target.Ready.Task, probe.Ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Check(target.Status.InstallThreadId != probe.Status.InstallThreadId, "split target and Hook share a thread");
            Check(target.Status is { KeyboardInstalled: false, MouseInstalled: false }
                && probe.Status.KeyboardInstalled == hooks.Keyboard && probe.Status.MouseInstalled == hooks.Mouse,
                "split Hook ownership incorrect");
            await using var source = hooks.Raw ? new RawInputEventSource() : null;
            if (source is not null)
            {
                source.InputReceived += (_, _) => { };
                await source.StartAsync();
                Check(source.Snapshot is { State: InputEventSourceState.Ready, Subscribers: 1 }, "split Raw Input did not become ready");
            }
            else Check(!WindowsRawInputReceiver.Registrations().Any(d => d.Page == 1 && d.Usage is 2 or 6),
                "no-source control has Raw Input registration");
            Check(target.QueryTargetMetricForTest(0) == RawInputDiagnosticWindow.TargetMetricMagic, "split target protocol changed");
            var before = target.Counts;
            target.PostTargetMessagesForTest();
            await target.SynchronizeAsync(); await probe.SynchronizeAsync();
            if (source is not null) await source.SynchronizeAsync();
            Check(target.Counts.KeyDown > before.KeyDown && target.Counts.KeyUp > before.KeyUp
                && target.Counts.MouseDown > before.MouseDown && target.Counts.MouseUp > before.MouseUp
                && target.Counts.Move > before.Move, "split target lost its message counters");
            Check(target.Counts.Keyboard.CallbackTotal == 0 && target.Counts.Mouse.CallbackTotal == 0,
                "target unexpectedly owns Hook counters");
            if (!hooks.Keyboard) Check(probe.Counts.Keyboard.CallbackTotal == 0, "uninstalled keyboard Hook collected callbacks");
            if (!hooks.Mouse) Check(probe.Counts.Mouse.CallbackTotal == 0, "uninstalled mouse Hook collected callbacks");
            if (run == 2)
            {
                probe.InjectThreadFailureForTest();
                var failed = await probe.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(failed is { ThreadExited: true, ExitReason: "Fault" } && failed.Failure!.Contains("Controlled"),
                    "split Hook post-start failure hidden");
                await target.SynchronizeAsync();
                if (source is not null) await source.SynchronizeAsync();
                Check(!target.Status.ThreadExited && (source is null || source.Snapshot.State == InputEventSourceState.Ready),
                    "split Hook fault incorrectly used as target/source health");
            }
            if (source is not null)
            {
                await source.StopAsync();
                Check(source.Snapshot is { Subscribers: 0, Buffered: 0, Fault: InputEventSourceFault.None }, "split source cleanup failed");
            }
            probe.Dispose(); target.Dispose();
            Check(target.Status is { ThreadExited: true, Failure: null, ExitReason: "WM_QUIT" }, "split target cleanup failed");
            Check(probe.Status.ThreadExited && (run == 2 || probe.Status is { Failure: null, ExitReason: "WM_QUIT" }), "split Hook cleanup failed");
            Check(!WindowsRawInputReceiver.Registrations().Any(d => d.Page == 1 && d.Usage is 2 or 6), "split registration leaked");
        }
        Console.WriteLine("Split target/receiver threads with both/no/keyboard-only Hooks, optional Raw Input, target protocol, observable thread fault and repeated cleanup passed (12 lifecycles)");
    }
    // @shape Three real target/Hook/unregistered-receiver lifecycles; faults and foreign ownership are observable.
    // @source Rug.UI.Core/Native/WindowsRawInputReceiver.cs#class:WindowsRawInputReceiver
    private static async Task UnregisteredPumpLifecycleAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        long generation = 0;
        for (int run = 0; run < 3; run++)
        {
            using var target = new RawInputDiagnosticWindow(false, false);
            using var probe = new RawInputDiagnosticWindow(true, false, messageOnly: true);
            await Task.WhenAll(target.Ready.Task, probe.Ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await using var pump = new DiagnosticRawInputPump();
            await Task.WhenAll(pump.StartAsync(), pump.StartAsync()).WaitAsync(TimeSpan.FromSeconds(5));
            var ready = pump.Status;
            Check(ready is { PumpReady: true, RegistrationEnabled: false, ThreadExited: false, Failure: null, Evidence: null }
                && ready.Hwnd != 0 && ready.Generation > generation, "unregistered pump startup identity incorrect");
            generation = ready.Generation;
            Check(new[] { ready.ThreadId, target.Status.InstallThreadId, probe.Status.InstallThreadId }.Distinct().Count() == 3,
                "diagnostic receiver shares target/Hook thread");
            Check(!WindowsRawInputReceiver.Registrations().Any(d => d.Page == 1 && d.Usage is 2 or 6), "unregistered pump installed devices");
            var before = target.Counts;
            target.PostTargetMessagesForTest(); await target.SynchronizeAsync(); await probe.SynchronizeAsync();
            Check(target.Counts.KeyDown > before.KeyDown && target.Counts.KeyUp > before.KeyUp,
                "pump control lost target delivery");
            await Until(() => pump.Status.TimerMessages > 0); // Actual SetTimer delivery, not just a posted barrier.
            long timer = pump.Status.TimerMessages;
            Check(WindowsRawInputReceiver.PostMessageW(ready.Hwnd, 0x113, 1, 0), "could not post diagnostic timer");
            await pump.SynchronizeAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(pump.Status.TimerMessages > timer && pump.Status is { UnexpectedEvents: 0, InputMessages: 0, DeviceMessages: 0 },
                "diagnostic pump timer/messages incorrect");
            await using var other = new RawInputEventSource();
            if (run == 2)
            {
                await other.StartAsync();
                var owned = WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).ToArray();
                Check(WindowsRawInputReceiver.PostMessageW(ready.Hwnd, 0xff, 0, 0), "could not inject diagnostic receiver fault");
                await Until(() => pump.Status.Failure is not null);
                await pump.DisposeAsync();
                Check(pump.Status is { ThreadExited: true, PumpReady: false, InputMessages: 1 }
                    && pump.Status.Failure!.Contains("GetRawInputData"), "post-ready pump failure hidden");
                Check(other.Snapshot.State == InputEventSourceState.Ready && WindowsRawInputReceiver.Registrations()
                    .Where(d => d.Page == 1 && d.Usage is 2 or 6).SequenceEqual(owned),
                    "unregistered cleanup disturbed another owner's registrations");
                await other.StopAsync();
            }
            await pump.DisposeAsync(); await pump.DisposeAsync();
            Check(pump.Status is { ThreadExited: true, PumpReady: false, Hwnd: 0 }
                && (run == 2 || pump.Status.Failure is null), "unregistered receiver cleanup failed");
            try { await pump.StartAsync(); throw new Exception("disposed pump restarted"); } catch (ObjectDisposedException) { }
            probe.Dispose(); target.Dispose();
            Check(probe.Status is { ThreadExited: true, Failure: null } && target.Status is { ThreadExited: true, Failure: null }
                && !WindowsRawInputReceiver.Registrations().Any(d => d.Page == 1 && d.Usage is 2 or 6), "pump comparison leaked resources");
        }
        Console.WriteLine("Unregistered receiver window/thread/timer, repeated startup/disposal, observable fault and foreign registration preservation passed (3 lifecycles)");
    }
    // @shape Three registered target/Hook/receiver lifecycles; direct scalar aggregates and no source queue/consumer.
    // @source Rug.UI.Core/Native/WindowsRawInputReceiver.cs#class:WindowsRawInputReceiver
    private static async Task DirectPumpLifecycleAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        long generation = 0;
        for (int run = 0; run < 3; run++)
        {
            using var target = new RawInputDiagnosticWindow(false, false);
            using var probe = new RawInputDiagnosticWindow(true, false, messageOnly: true);
            await Task.WhenAll(target.Ready.Task, probe.Ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await using var pump = new DiagnosticRawInputPump(registerDevices: true);
            await Task.WhenAll(pump.StartAsync(), pump.StartAsync()).WaitAsync(TimeSpan.FromSeconds(5));
            var ready = pump.Status;
            var owned = WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).ToArray();
            Check(ready is { PumpReady: true, RegistrationEnabled: true, ThreadExited: false, Failure: null, UnexpectedEvents: 0 }
                && ready.Evidence is not null && ready.Hwnd != 0 && ready.Generation > generation, "direct registered pump readiness incorrect");
            generation = ready.Generation;
            Check(owned.Length == 2 && owned.All(d => d.Target == ready.Hwnd && (d.Flags & ~0x2000u) == 0x100),
                "direct pump registration flags/owner incorrect");
            Check(new[] { ready.ThreadId, target.Status.InstallThreadId, probe.Status.InstallThreadId }.Distinct().Count() == 3,
                "direct pump changed thread placement");
            target.PostTargetMessagesForTest(); await target.SynchronizeAsync(); await probe.SynchronizeAsync();
            Check(target.Counts.KeyDown > 0 && target.Counts.KeyUp > 0 && !probe.Status.ThreadExited,
                "direct pump lost target/Hook protocol");
            await Until(() => pump.Status.TimerMessages > 0);
            await pump.SynchronizeAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (run == 1)
            {
                await using var conflict = new DiagnosticRawInputPump(registerDevices: true);
                try { await conflict.StartAsync(); throw new Exception("direct pump silently replaced registration"); } catch (RawInputException) { }
                await conflict.DisposeAsync();
                Check(conflict.Status is { PumpReady: false, ThreadExited: true, Hwnd: 0 } && conflict.Status.Failure is not null,
                    "direct pump startup failure hidden");
                Check(WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).SequenceEqual(owned),
                    "failed direct pump removed live owner's registrations");
            }
            if (run == 2)
            {
                Check(WindowsRawInputReceiver.PostMessageW(ready.Hwnd, 0xff, 0, 0), "could not inject direct receiver fault");
                await Until(() => pump.Status.Failure is not null);
                await pump.DisposeAsync();
                Check(pump.Status is { ThreadExited: true, PumpReady: false, Hwnd: 0 }
                    && pump.Status.Failure!.Contains("GetRawInputData"), "direct pump post-ready fault hidden");
                await using var replacement = new DiagnosticRawInputPump(registerDevices: true);
                await replacement.StartAsync();
                var nextOwned = WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).ToArray();
                await pump.DisposeAsync();
                Check(replacement.Status.Generation > ready.Generation && nextOwned.Length == 2
                    && WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).SequenceEqual(nextOwned),
                    "old direct pump disposal disturbed new registrations");
            }
            await pump.DisposeAsync(); await pump.DisposeAsync();
            Check(pump.Status is { ThreadExited: true, PumpReady: false, Hwnd: 0 }
                && (run == 2 || pump.Status.Failure is null), "registered direct pump cleanup failed");
            try { await pump.StartAsync(); throw new Exception("disposed direct pump restarted"); } catch (ObjectDisposedException) { }
            probe.Dispose(); target.Dispose();
            Check(!WindowsRawInputReceiver.Registrations().Any(d => d.Page == 1 && d.Usage is 2 or 6)
                && probe.Status is { ThreadExited: true, Failure: null } && target.Status is { ThreadExited: true, Failure: null },
                "direct pump comparison leaked registrations/threads");
        }
        Console.WriteLine("Registered direct receiver readiness, ownership conflict, generations, faults and cleanup passed (3 lifecycles; software AUTO not inferred)");
    }
    // @shape Three count-only lifecycles per registration selection; no packet parsing, tracking or observations.
    // @source Rug.UI.Core/Native/WindowsRawInputReceiver.cs#class:WindowsRawInputReceiver
    private static async Task MessagePumpLifecycleAsync(bool registerKeyboard = true, bool registerMouse = true, bool native = false, bool managedKeyboard = true)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (native && !File.Exists(DiagnosticNativeRawInputPump.LibraryPath))
        { Console.WriteLine("NOT RUN: native count-only lifecycles; build tests/run_native_input_probe.ps1 -Mode Build first."); return; }
        if (native)
        {
            await using var unstarted = new DiagnosticNativeRawInputPump();
            await Task.WhenAll(unstarted.DisposeAsync().AsTask(), unstarted.DisposeAsync().AsTask());
            try { await unstarted.StartAsync(); throw new Exception("disposed native receiver started"); } catch (ObjectDisposedException) { }
            Check(unstarted.Status.Hwnd == 0 && !unstarted.Status.PumpReady, "unstarted native receiver acquired resources");
        }
        IDiagnosticRawInputPump CreatePump() => native ? new DiagnosticNativeRawInputPump()
            : new DiagnosticRawInputPump(registerDevices: true, readPackets: false, registerKeyboard: registerKeyboard, registerMouse: registerMouse);
        long generation = 0;
        for (int run = 0; run < 3; run++)
        {
            using var target = new RawInputDiagnosticWindow(false, false);
            using var probe = new RawInputDiagnosticWindow(managedKeyboard, false, messageOnly: true);
            await Task.WhenAll(target.Ready.Task, probe.Ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await using var pump = CreatePump();
            await Task.WhenAll(pump.StartAsync(), pump.StartAsync()).WaitAsync(TimeSpan.FromSeconds(5));
            var ready = pump.Status;
            var owned = WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).ToArray();
            Check(ready is { RegistrationEnabled: true, PacketReadingEnabled: false, Evidence: null, PumpReady: true, Failure: null }
                && ready.Generation > generation && ready.Hwnd != 0 && ready.KeyboardRegistrationEnabled == registerKeyboard
                && ready.MouseRegistrationEnabled == registerMouse, "count-only readiness, registration selection or evidence incorrect");
            generation = ready.Generation;
            Check(owned.Length == (registerKeyboard && registerMouse ? 2 : 1) && owned.Any(d => d.Usage == 2) == registerMouse
                && owned.Any(d => d.Usage == 6) == registerKeyboard
                && owned.All(d => d.Target == ready.Hwnd && (d.Flags & ~0x2000u) == 0x100)
                && new[] { ready.ThreadId, target.Status.InstallThreadId, probe.Status.InstallThreadId }.Distinct().Count() == 3,
                "count-only registration flags or thread topology changed");
            await Until(() => pump.Status.TimerMessages > 0);
            target.PostTargetMessagesForTest(); await target.SynchronizeAsync(); await probe.SynchronizeAsync();
            Check(target.QueryTargetMetricForTest(0) == RawInputDiagnosticWindow.TargetMetricMagic
                && target.Counts.KeyDown > 0 && target.Counts.KeyUp > 0
                && probe.Status.KeyboardInstalled == managedKeyboard && !probe.Status.MouseInstalled,
                "count-only target protocol or Hook selection changed");
            if (!managedKeyboard) Check(probe.Counts.Keyboard.CallbackTotal == 0 && probe.Counts.Mouse.CallbackTotal == 0,
                "idle no-Hook thread unexpectedly collected callbacks");
            long inputs = pump.Status.InputMessages, cleanups = pump.Status.InputCleanupCalls, devices = pump.Status.DeviceMessages;
            // Invalid native handles would fail ordinary parsing. Both foreground and sink messages
            // must reach DefWindowProc without reads; invalid device payload must not create observations.
            Check(WindowsRawInputReceiver.PostMessageW(ready.Hwnd, 0xff, 0, 0)
                && WindowsRawInputReceiver.PostMessageW(ready.Hwnd, 0xff, 1, 0)
                && WindowsRawInputReceiver.PostMessageW(ready.Hwnd, 0xfe, 99, 0), "could not post count-only messages");
            await pump.SynchronizeAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => pump.Status.InputCleanupCalls >= cleanups + 2 && pump.Status.DeviceMessages >= devices + 1);
            Check(pump.Status.InputMessages >= inputs + 2
                && pump.Status is { PumpReady: true, Failure: null, UnexpectedEvents: 0, Evidence: null },
                "count-only parsed malformed data, skipped cleanup or published fabricated raw evidence");
            if (run == 1)
            {
                await using var conflict = CreatePump();
                Exception? rejected = null;
                try { await conflict.StartAsync(); } catch (Exception error) { rejected = error; }
                Check(native ? rejected is InvalidOperationException && conflict.Status.Failure!.Contains("code=1")
                    : rejected is RawInputException, "count-only conflict did not report registration rejection");
                await conflict.DisposeAsync();
                Check(conflict.Status is { ThreadExited: true, PumpReady: false, Hwnd: 0 } && conflict.Status.Failure is not null
                    && WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).SequenceEqual(owned),
                    "count-only conflict fault/cleanup disturbed existing owner");
            }
            if (run == 2)
            {
                // Simulate a later component replacing registrations; failed old receiver must not remove them.
                var replacement = owned.Select(d => new WindowsRawInputReceiver.Device { Page = d.Page, Usage = d.Usage, Flags = d.Flags, Target = target.Ready.Task.Result }).ToArray();
                try
                {
                    Check(WindowsRawInputReceiver.RegisterRawInputDevices(replacement, (uint)replacement.Length, (uint)Marshal.SizeOf<WindowsRawInputReceiver.Device>()), "could not replace registrations");
                    Check(WindowsRawInputReceiver.PostMessageW(ready.Hwnd, 0x113, 1, 0), "could not post ownership check");
                    await Until(() => pump.Status.Failure is not null);
                    await pump.DisposeAsync();
                    Check(pump.Status is { ThreadExited: true, PumpReady: false, Hwnd: 0, Evidence: null }
                        && pump.Status.Failure!.Contains("ownership changed")
                        && WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).SequenceEqual(replacement),
                        "count-only ownership loss hidden or removed replacement registrations");
                }
                finally
                {
                    var remove = replacement.Select(d => new WindowsRawInputReceiver.Device { Page = d.Page, Usage = d.Usage, Flags = 1 }).ToArray();
                    Check(WindowsRawInputReceiver.RegisterRawInputDevices(remove, (uint)remove.Length, (uint)Marshal.SizeOf<WindowsRawInputReceiver.Device>()), "replacement cleanup failed");
                }
            }
            if (run == 0 && (!registerKeyboard || !registerMouse))
            {
                ushort omittedUsage = registerKeyboard ? (ushort)2 : (ushort)6;
                WindowsRawInputReceiver.Device[] foreign = [new() { Page = 1, Usage = omittedUsage, Flags = 0x100, Target = target.Ready.Task.Result }];
                try
                {
                    Check(WindowsRawInputReceiver.RegisterRawInputDevices(foreign, 1, (uint)Marshal.SizeOf<WindowsRawInputReceiver.Device>()), "could not register unexpected device class");
                    Check(WindowsRawInputReceiver.PostMessageW(ready.Hwnd, 0x113, 1, 0), "could not post registration selection check");
                    await Until(() => pump.Status.Failure is not null);
                    await pump.DisposeAsync();
                    Check(pump.Status.Failure!.Contains("ownership changed")
                        && WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).SequenceEqual(foreign),
                        "unexpected device registration was accepted or removed during selected-class cleanup");
                }
                finally
                {
                    WindowsRawInputReceiver.Device[] remove = [new() { Page = 1, Usage = omittedUsage, Flags = 1 }];
                    Check(WindowsRawInputReceiver.RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf<WindowsRawInputReceiver.Device>()), "foreign device cleanup failed");
                }
            }
            await pump.DisposeAsync(); await pump.DisposeAsync();
            Check(pump.Status is { ThreadExited: true, PumpReady: false, Hwnd: 0, Evidence: null }
                && pump.Status.InputCleanupCalls == pump.Status.InputMessages
                && (run == 2 || ((!registerKeyboard || !registerMouse) && run == 0) || pump.Status.Failure is null),
                "count-only cleanup failed or WM_INPUT missed DefWindowProc");
            try { await pump.StartAsync(); throw new Exception("disposed count-only control restarted"); } catch (ObjectDisposedException) { }
            probe.Dispose(); target.Dispose();
            Check(!WindowsRawInputReceiver.Registrations().Any(d => d.Page == 1 && d.Usage is 2 or 6)
                && probe.Status is { ThreadExited: true, Failure: null } && target.Status is { ThreadExited: true, Failure: null },
                "count-only lifecycle leaked registrations or threads");
        }
        Console.WriteLine($"Registered message-only receiver native={native} keyboard={registerKeyboard} mouse={registerMouse} managedKeyboardHook={managedKeyboard}, omitted parsing/evidence, WM_INPUT cleanup, conflicts, ownership loss and generations passed (3 lifecycles; AUTO not inferred)");
    }
    private static byte[] Packet(uint type, int pointerSize, int body)
    {
        int header = 8 + 2 * pointerSize;
        var packet = new byte[header + body];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, type);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 42);
        return packet;
    }
    private static void Parse()
    {
        nint hook = Marshal.AllocHGlobal(32);
        try { Marshal.WriteInt32(hook, 8, 0x10); Marshal.WriteInt32(hook, 12, 1);
            Check(RawInputDiagnosticWindow.ReadHookFlags(hook, true) == 0x10 && RawInputDiagnosticWindow.ReadHookFlags(hook, false) == 1, "hook flags layout incorrect");
            Marshal.WriteIntPtr(hook, 16, (nint)InputObservation.RugSoftwareInputTag);
            Marshal.WriteIntPtr(hook, IntPtr.Size == 8 ? 24 : 20, (nint)InputObservation.RugSoftwareInputTag);
            Check(RawInputDiagnosticWindow.ReadHookExtraInformation(hook, true) == InputObservation.RugSoftwareInputTag && RawInputDiagnosticWindow.ReadHookExtraInformation(hook, false) == InputObservation.RugSoftwareInputTag, "hook extra-info layout incorrect"); }
        finally { Marshal.FreeHGlobal(hook); }
        var devices = new RawInputDeviceIds();
        Check(devices.Get(0) == 0 && devices.Get(0xabcdef) == 1 && devices.Get(0xabcdef) == 1, "native handles were not mapped to stable detached IDs");
        devices.Remove(0xabcdef); Check(devices.Get(0xabcdef) == 2, "reused native device inherited ID");
        devices.Clear(); Check(devices.Get(0x123456) == 1, "new receiver device map was not reset");
        foreach (int pointers in new[] { 4, 8 })
        {
            var key = Packet(1, pointers, 16); int h = 8 + pointers * 2;
            BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(h), 30);
            BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(h + 6), 65);
            BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(h + 8), 0x100);
            var tracker = new RawInputKeyboardTracker();
            var down = RawInputPacketParser.Parse(key, true, pointers)!;
            Check(down.IsCandidateInput && (down with { DeviceId = 0 }).IsCandidateInput, "unattributed human input excluded");
            BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(h + 12), InputObservation.RugSoftwareInputTag);
            var softwareKey = RawInputPacketParser.Parse(key, true, pointers)!;
            Check(softwareKey.IsRugSoftwareInput && !softwareKey.IsCandidateInput && (softwareKey with { ExtraInformation = 123 }).IsCandidateInput, "keyboard tag classification incorrect");
            BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(h + 12), 0);
            Check(down.VirtualKey == 65 && down.ScanCode == 30 && down.DeviceId == 42 && down.Foreground && !down.KeyReleased, "keyboard copied layout incorrect");
            Check(!tracker.Apply(down).IsRepeat && tracker.Apply(down).IsRepeat, "repeat tracking failed");
            Check(!tracker.Apply(down with { DeviceId = 43 }).IsRepeat, "device key identity collided");
            BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(h + 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(h + 8), 0x101);
            var up = tracker.Apply(RawInputPacketParser.Parse(key, false, pointers)!);
            Check(up.KeyReleased && !up.IsRepeat && !up.Foreground && !tracker.Apply(down).IsRepeat, "release was lost");
            tracker.Apply(new(InputEventKind.DeviceRemoved, 42, false));
            Check(!tracker.Apply(down).IsRepeat, "removed device retained held key");
            tracker.Clear(); Check(!tracker.Apply(down).IsRepeat, "new tracker session retained repeat");
            Array.Clear(key, 8, pointers);
            Check(RawInputPacketParser.Parse(key, false, pointers)!.DeviceId == 0, "zero-device observation was discarded");
            var mouse = Packet(0, pointers, 24);
            BinaryPrimitives.WriteInt32LittleEndian(mouse.AsSpan(h + 12), -12);
            BinaryPrimitives.WriteInt32LittleEndian(mouse.AsSpan(h + 16), 7);
            var movement = RawInputPacketParser.Parse(mouse, false, pointers)!;
            BinaryPrimitives.WriteUInt32LittleEndian(mouse.AsSpan(h + 20), InputObservation.RugSoftwareInputTag);
            var softwareMouse = RawInputPacketParser.Parse(mouse, false, pointers)!;
            Check(softwareMouse.IsRugSoftwareInput && !softwareMouse.IsCandidateInput && (movement with { DeviceId = 0 }).IsCandidateInput, "mouse tag classification incorrect");
            BinaryPrimitives.WriteUInt32LittleEndian(mouse.AsSpan(h + 20), 0);
            Check(movement.IsPureMovement && movement.X == -12 && movement.Y == 7, "mouse displacement incorrect");
            foreach (ushort buttons in new ushort[] { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048 })
            {
                BinaryPrimitives.WriteUInt16LittleEndian(mouse.AsSpan(h + 4), buttons);
                BinaryPrimitives.WriteInt16LittleEndian(mouse.AsSpan(h + 6), -120);
                var observed = RawInputPacketParser.Parse(mouse, true, pointers)!;
                Check(observed.ButtonFlags == buttons && !observed.IsPureMovement && observed.WheelDelta == (buttons >= 1024 ? -120 : 0), "button/wheel was lost");
            }
            Array.Clear(mouse); Check(movement.X == -12, "event leaked mutable packet reference");
            foreach (var invalid in new[] { new byte[2], Packet(7, pointers, 24), Packet(1, pointers, 3), Packet(0, pointers, 2), key[..^1], mouse })
            {
                try { RawInputPacketParser.Parse(invalid, false, pointers); throw new Exception("invalid packet accepted"); }
                catch (RawInputException e) { Check(e.Fault == InputEventSourceFault.InvalidData, "wrong parse fault"); }
            }
            BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(h), 255);
            try { RawInputPacketParser.Parse(key, false, pointers); throw new Exception("overrun accepted"); } catch (RawInputException) { }
        }
    }
    private sealed class Receiver : IRawInputReceiver
    {
        public readonly ManualResetEventSlim ReadyGate = new(true), Stopped = new(false);
        public Action<InputObservation>? Emit;
        public bool FailRegistration, FailCleanup;
        public int Runs, Exits;
        public void Run(Action ready, Action<InputObservation> receive, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs); Emit = receive;
            try
            {
                ReadyGate.Wait(cancellationToken);
                if (FailRegistration) throw new RawInputException(InputEventSourceFault.Registration, "test registration failed");
                ready(); Stopped.Wait(cancellationToken);
            }
            finally { Interlocked.Increment(ref Exits); if (FailCleanup) throw new RawInputException(InputEventSourceFault.CleanupFailure, "test cleanup failed"); }
        }
        public void RequestStop() => Stopped.Set();
        public Task SynchronizeAsync() => Task.CompletedTask;
    }
    private static async Task LifecycleAsync()
    {
        var receivers = new List<Receiver>();
        await using var source = new RawInputEventSource(() => { var r = new Receiver(); receivers.Add(r); return r; });
        Check(source.Snapshot.State == InputEventSourceState.Uninitialized, "source started implicitly");
        await source.StartAsync(); await source.StartAsync();
        Check(receivers.Count == 1 && source.Snapshot.State == InputEventSourceState.Ready, "duplicate receiver created");
        long generation = source.Snapshot.Generation;
        var delivered = new ConcurrentQueue<InputSourceEvent>();
        source.InputReceived += (_, e) => delivered.Enqueue(e);
        receivers[0].Emit!(new(InputEventKind.Keyboard, 1, true, 65, 30));
        await Until(() => delivered.Count == 1);
        var first = delivered.Single();
        Check(first.Generation == generation && first.Sequence == 1 && first.MonotonicTicks > 0, "identity metadata missing");
        await source.StopAsync(); await source.StopAsync();
        Check(receivers[0].Exits == 1 && source.Snapshot is { State: InputEventSourceState.Stopped, Buffered: 0, Subscribers: 0 }, "stop leaked resources or subscribers");
        await source.StartAsync();
        source.InputReceived += (_, e) => delivered.Enqueue(e);
        receivers[0].Emit!(new(InputEventKind.Keyboard, 1, true, 66));
        receivers[1].Emit!(new(InputEventKind.Keyboard, 1, true, 67));
        await Until(() => delivered.Count == 2);
        Check(delivered.Last().Generation > generation && delivered.Last().Sequence == 1 && source.Snapshot.Keyboard == 1, "old generation entered restarted session");
        await source.DisposeAsync(); await source.DisposeAsync();
        Check(source.Snapshot.Subscribers == 0 && receivers.All(r => r.Exits == 1), "dispose leaked registration");
        try { await source.StartAsync(); throw new Exception("disposed source restarted"); } catch (ObjectDisposedException) { }
        var failing = new Receiver { FailRegistration = true };
        await using var failed = new RawInputEventSource(() => failing);
        try { await failed.StartAsync(); throw new Exception("registration failure reported ready"); } catch (RawInputException) { }
        Check(failed.Snapshot is { State: InputEventSourceState.Faulted, Fault: InputEventSourceFault.Registration } && failing.Exits == 1, "registration fault hidden");
        var cleanupReceiver = new Receiver { FailCleanup = true };
        await using var cleanupSource = new RawInputEventSource(() => cleanupReceiver);
        await cleanupSource.StartAsync(); await cleanupSource.StopAsync();
        Check(cleanupSource.Snapshot is { State: InputEventSourceState.Faulted, Fault: InputEventSourceFault.CleanupFailure, Subscribers: 0 }, "cleanup failure reported successful stop");
        var pending = new Receiver(); pending.ReadyGate.Reset();
        await using var starting = new RawInputEventSource(() => pending);
        using var cancel = new CancellationTokenSource();
        Task startup = starting.StartAsync(cancel.Token);
        await Until(() => pending.Runs == 1);
        Check(!startup.IsCompleted && starting.Snapshot.State == InputEventSourceState.Starting, "thread creation was reported as readiness");
        cancel.Cancel();
        try { await startup; throw new Exception("cancelled startup succeeded"); } catch (OperationCanceledException) { }
        Check(pending.Exits == 1 && starting.Snapshot.State == InputEventSourceState.Stopped, "cancelled startup leaked receiver");
        var stopPending = new Receiver(); stopPending.ReadyGate.Reset();
        await using var stopStarting = new RawInputEventSource(() => stopPending);
        var startingTask = stopStarting.StartAsync(); await Until(() => stopPending.Runs == 1);
        await stopStarting.StopAsync();
        try { await startingTask; throw new Exception("stopped startup became ready"); } catch (OperationCanceledException) { }
        Check(stopPending.Exits == 1 && stopStarting.Snapshot.State == InputEventSourceState.Stopped, "stop during startup leaked");
    }
    private static async Task ConsumersAsync()
    {
        var receiver = new Receiver();
        await using var source = new RawInputEventSource(() => receiver, capacity: 2);
        var events = new ConcurrentQueue<InputSourceEvent>();
        using ManualResetEventSlim entered = new(), release = new();
        int producer, consumer = 0;
        source.InputReceived += (_, e) => { Interlocked.CompareExchange(ref consumer, Environment.CurrentManagedThreadId, 0); entered.Set(); release.Wait(); events.Enqueue(e); };
        source.InputReceived += (_, _) => throw new Exception("isolated subscriber");
        await source.StartAsync();
        producer = Environment.CurrentManagedThreadId;
        receiver.Emit!(new(InputEventKind.Keyboard, 1, true, 65));
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "consumer did not enter");
        try
        {
            for (int i = 0; i < 10000; i++) receiver.Emit!(new(InputEventKind.Mouse, 1, true, X: 1, Y: -1));
            receiver.Emit!(new(InputEventKind.Mouse, 1, true, ButtonFlags: 1));
            Check(source.Snapshot is { State: InputEventSourceState.Ready, Buffered: 2, Coalesced: 9999, Mouse: 10001 }, "movement coalescing or button preservation failed");
        }
        finally { release.Set(); }
        await Until(() => events.Count == 3 && source.Snapshot.ConsumerErrors == 3);
        var all = events.ToArray();
        Check(consumer != producer && all[1].Samples == 10000 && all[1].Observation.X == 10000 && all[1].Observation.Y == -10000
            && all[2].Observation.ButtonFlags == 1 && all.Zip(all.Skip(1)).All(p => p.First.Sequence < p.Second.Sequence && p.First.MonotonicTicks <= p.Second.MonotonicTicks), "merged motion/order/thread incorrect");
        await source.StopAsync();
        var overflowReceiver = new Receiver();
        await using var overflow = new RawInputEventSource(() => overflowReceiver, capacity: 2);
        using ManualResetEventSlim blocked = new(), unblock = new();
        overflow.InputReceived += (_, _) => { blocked.Set(); unblock.Wait(); };
        await overflow.StartAsync(); overflowReceiver.Emit!(new(InputEventKind.Keyboard, 1, true, 65));
        Check(blocked.Wait(TimeSpan.FromSeconds(5)), "overflow barrier missing");
        try
        {
            overflowReceiver.Emit!(new(InputEventKind.Keyboard, 1, true, 66));
            overflowReceiver.Emit!(new(InputEventKind.Mouse, 1, true, ButtonFlags: 2));
            overflowReceiver.Emit!(new(InputEventKind.Keyboard, 1, true, 67));
            Check(overflow.Snapshot is { State: InputEventSourceState.Faulted, Fault: InputEventSourceFault.Overflow, Overflows: 1, Buffered: 0 }, "critical overflow silently lost input");
            Task stopping = overflow.StopAsync();
            Check(!stopping.IsCompleted, "stop did not await entered consumer");
            unblock.Set(); await stopping;
            Check(overflow.Snapshot.Subscribers == 0 && overflowReceiver.Exits == 1, "overflow cleanup leaked");
        }
        finally { unblock.Set(); }
    }
    private static async Task NativeLifecycleAsync()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Raw native lifecycle: NOT RUN (Windows required)"); return; }
        var before = WindowsRawInputReceiver.Registrations();
        if (before.Any(d => d.Page == 1 && d.Usage is 0 or 2 or 6)) { Console.WriteLine("Raw native lifecycle: NOT RUN (existing host registration)"); return; }
        await using var first = new RawInputEventSource();
        await first.StartAsync();
        var owned = WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).ToArray();
        Check(owned.Length == 2 && owned.All(d => (d.Flags & ~0x2000u) == 0x100 && d.Target != 0), "native flags/readiness incorrect");
        await using var other = new RawInputEventSource();
        try { await other.StartAsync(); throw new Exception("conflict silently replaced registration"); } catch (RawInputException) { }
        Check(WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).All(d => d.Target == owned[0].Target), "conflict disturbed first receiver");
        await first.StopAsync();
        Check(!WindowsRawInputReceiver.Registrations().Any(d => d.Page == 1 && d.Usage is 2 or 6), "stop retained registrations");
        for (int i = 0; i < 4; i++)
        {
            await first.StartAsync(); first.InputReceived += (_, _) => { };
            await first.StopAsync();
            Check(first.Snapshot is { Subscribers: 0, Buffered: 0 } && !WindowsRawInputReceiver.Registrations().Any(d => d.Page == 1 && d.Usage is 2 or 6), "repeated native stop accumulated subscriptions/registrations");
        }
        long generation = first.Snapshot.Generation;
        await first.StartAsync(); Check(first.Snapshot.Generation > generation, "native restart retained generation");
        nint receiverHwnd = WindowsRawInputReceiver.Registrations().First(d => d.Page == 1 && d.Usage == 2).Target;
        Check(WindowsRawInputReceiver.PostMessageW(receiverHwnd, 0xff, 0, 0), "invalid WM_INPUT test could not post");
        await Until(() => first.Snapshot.State == InputEventSourceState.Faulted);
        Check(first.Snapshot.Fault == InputEventSourceFault.InvalidData, "malformed native input did not fault");
        await first.StopAsync(); await first.StartAsync();
        using var replacement = new NativeInputTakeoverTests.IsolatedWindow();
        nint hwnd = await replacement.Ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var devices = new[] { new WindowsRawInputReceiver.Device { Page = 1, Usage = 2, Flags = 0x2100, Target = hwnd }, new WindowsRawInputReceiver.Device { Page = 1, Usage = 6, Flags = 0x2100, Target = hwnd } };
        Check(WindowsRawInputReceiver.RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<WindowsRawInputReceiver.Device>()), "could not install diagnostic replacement");
        try
        {
            await Until(() => first.Snapshot.State == InputEventSourceState.Faulted);
            Check(first.Snapshot.Fault == InputEventSourceFault.OwnershipLost, "lost receiver ownership not diagnosed");
            await first.StopAsync();
            var replacementOwned = WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 2 or 6).ToArray();
            Check(replacementOwned.Length == 2 && replacementOwned.All(d => d.Target == hwnd), "stop removed replacement owner's registration");
        }
        finally
        {
            foreach (ref var d in devices.AsSpan()) { d.Flags = 1; d.Target = 0; }
            Check(WindowsRawInputReceiver.RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<WindowsRawInputReceiver.Device>()), "replacement cleanup failed");
        }
    }
}
