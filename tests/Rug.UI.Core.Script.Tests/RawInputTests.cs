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
        var raw = new RawInputDiagnostics.RawEvidence(3, 4, 2, 5, 1, 1);
        Check(raw.Delta(new(1, 1, 0, 0, 0, 0)) == new RawInputDiagnostics.RawEvidence(2, 3, 2, 5, 1, 1), "phase raw evidence delta incorrect");
    }
    private static async Task ProbeLifecycleAsync()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("NOT RUN: native diagnostic probe lifecycle requires Windows"); return; }
        long generation;
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
