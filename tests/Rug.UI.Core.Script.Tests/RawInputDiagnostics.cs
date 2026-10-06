using System.Runtime.InteropServices;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;

/// <summary>Opt-in, aggregate-only desktop diagnostic; independent hook counters detect interference.</summary>
internal static class RawInputDiagnostics
{
    internal sealed record RawEvidence(long CandidateKeyboard, long CandidateMouse, long SoftwareKeyboard,
        long SoftwareMouse, long SoftwareKeyDown, long SoftwareKeyUp)
    {
        public RawEvidence Delta(RawEvidence before) => new(CandidateKeyboard - before.CandidateKeyboard,
            CandidateMouse - before.CandidateMouse, SoftwareKeyboard - before.SoftwareKeyboard,
            SoftwareMouse - before.SoftwareMouse, SoftwareKeyDown - before.SoftwareKeyDown, SoftwareKeyUp - before.SoftwareKeyUp);
    }
    private static void PrintHook(string label, RawInputDiagnosticWindow.HookCounts c)
        => Console.WriteLine($"{label}: callbackTotal={c.CallbackTotal} actionableTotal={c.ActionableTotal} injected={c.Injected} unflagged={c.Unflagged} taggedTotal={c.TaggedTotal} taggedInjected={c.TaggedInjected} taggedUnflagged={c.TaggedUnflagged} parseErrors={c.ParseErrors} ownTicks={c.OwnTicks} ownMaxTicks={c.OwnMaxTicks} chainTicks={c.ChainTicks} chainMaxTicks={c.ChainMaxTicks}");
    private static void PrintProbe(RawInputDiagnosticWindow window, string label)
    {
        var p = window.Status;
        Console.WriteLine($"{label}: probeGeneration={p.Generation} installThreadId={p.InstallThreadId} architecture={p.Architecture} keyboardInstalled={p.KeyboardInstalled} keyboardError={p.KeyboardInstallError} mouseInstalled={p.MouseInstalled} mouseError={p.MouseInstallError} threadExited={p.ThreadExited} failure={p.Failure ?? "None"} exitReason={p.ExitReason ?? "None"} stopwatchFrequency={System.Diagnostics.Stopwatch.Frequency}");
    }
    internal static string CompareKeyCallbacks(long? f8, long? b)
    {
        if (f8 is null || b is null) return "INCONCLUSIVE: missing/interfered F8 or B round; no key-specific inference.";
        if (f8 == 0 && b > 0) return "BRANCH: B callbacks present, F8 absent; investigate key-specific/hotkey handling.";
        if (f8 == 0 && b == 0) return "BRANCH: both automatic F8/B callbacks absent; compare an independent x64 native probe. Human phase evidence is required to distinguish general hook failure.";
        if (f8 > 0 && b == 0) return "BRANCH: F8 callbacks present, B absent; investigate key-specific handling or between-round probe lifetime.";
        return "F8/B callbacks both present; use each round's tagged/injected, raw and target evidence for acceptance.";
    }
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
        // Diagnostic-only: inspect the loaded module, rather than infer it from the search path.
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var module = process.Modules.Cast<System.Diagnostics.ProcessModule>().Single(m => m.BaseAddress == library);
        using var file = File.OpenRead(module.FileName);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
        Console.WriteLine($"CORE LOADED: processId={Environment.ProcessId} path={module.FileName} bytes={file.Length} sha256={hash}");
        return true; // Cached generated imports require this module until process exit.
    }
    internal sealed class RawAggregate
    {
        private long _candidateKey, _candidateMouse, _softwareKey, _softwareMouse, _down, _up;
        internal void Observe(InputSourceEvent e)
            => ObserveCopied(e.Observation, e.Samples);
        internal void ObserveCopied(InputObservation observation, long samples = 1)
        {
            if (observation.IsRugSoftwareInput)
            {
                if (observation.Kind == InputEventKind.Keyboard)
                {
                    Interlocked.Add(ref _softwareKey, samples);
                    if (observation.KeyReleased) Interlocked.Add(ref _up, samples);
                    else Interlocked.Add(ref _down, samples);
                }
                else Interlocked.Add(ref _softwareMouse, samples);
            }
            else if (observation.IsCandidateInput)
            {
                if (observation.Kind == InputEventKind.Keyboard) Interlocked.Add(ref _candidateKey, samples);
                else Interlocked.Add(ref _candidateMouse, samples);
            }
        }
        internal RawEvidence Read() => new(Interlocked.Read(ref _candidateKey), Interlocked.Read(ref _candidateMouse),
            Interlocked.Read(ref _softwareKey), Interlocked.Read(ref _softwareMouse), Interlocked.Read(ref _down), Interlocked.Read(ref _up));
    }
    internal static (bool Target, bool Keyboard, bool Mouse) IsolationStage(string stage) => stage switch
    {
        "load" => (false, false, false),
        "target" => (true, false, false),
        "keyboard" => (true, true, false),
        "both" => (true, true, true),
        "both-raw" => (true, true, true),
        "both-raw-split" => (true, true, true),
        "target-raw" => (true, false, false),
        "keyboard-raw" => (true, true, false),
        "keyboard-split" => (true, true, false),
        "keyboard-pump" => (true, true, false),
        "keyboard-direct" => (true, true, false),
        "keyboard-messages" => (true, true, false),
        "keyboard-messages-mouse" => (true, true, false),
        "keyboard-messages-keyboard" => (true, true, false),
        "keyboard-native-messages" => (true, true, false),
        "target-native-messages" => (true, false, false),
        _ => throw new ArgumentException("Use --hook-isolation-stage load|target|keyboard|both|both-raw|both-raw-split|target-raw|keyboard-raw|keyboard-split|keyboard-pump|keyboard-direct|keyboard-messages|keyboard-messages-mouse|keyboard-messages-keyboard|keyboard-native-messages|target-native-messages")
    };
    internal static bool UsesSplitProbe(string stage) => stage is "both-raw-split" or "target-raw" or "keyboard-raw" or "keyboard-split" or "keyboard-pump" or "keyboard-direct" or "keyboard-messages" or "keyboard-messages-mouse" or "keyboard-messages-keyboard" or "keyboard-native-messages" or "target-native-messages";
    internal static bool UsesRawInput(string stage) => stage is "both-raw" or "both-raw-split" or "target-raw" or "keyboard-raw";
    // @shape Fresh process per stage; raw modes register Raw Input, never emit/reinstall Hooks.
    // @source Existing native sender stays unchanged: tests/NativeInputProbe/main.cpp#class:CoreInput
    internal static async Task RunIsolationAsync(string stage)
    {
        var selection = IsolationStage(stage); // Validate before DLL/window side effects.
        if (!LoadNative()) { Environment.ExitCode = 2; return; }
        bool split = UsesSplitProbe(stage);
        using var window = selection.Target ? new RawInputDiagnosticWindow(selection.Keyboard && !split, selection.Mouse && !split) : null;
        // Message-only receiver uses the unchanged callback/parser/forwarding implementation.
        using var separateProbe = split ? new RawInputDiagnosticWindow(selection.Keyboard, selection.Mouse, messageOnly: true) : null;
        var probe = separateProbe ?? window;
        await using var source = UsesRawInput(stage) ? new RawInputEventSource() : null;
        bool direct = stage == "keyboard-direct";
        bool mouseOnly = stage == "keyboard-messages-mouse";
        bool nativeMessages = stage is "keyboard-native-messages" or "target-native-messages";
        bool keyboardOnly = stage is "keyboard-messages-keyboard" or "keyboard-native-messages" or "target-native-messages";
        bool messages = stage is "keyboard-messages" or "keyboard-messages-mouse" or "keyboard-messages-keyboard" or "keyboard-native-messages" or "target-native-messages";
        bool registeredPump = direct || messages;
        string pumpLabel = nativeMessages ? "NATIVE KEYBOARD MESSAGES" : keyboardOnly ? "KEYBOARD MESSAGES" : mouseOnly ? "MOUSE MESSAGES" : messages ? "MESSAGES" : direct ? "DIRECT" : "UNREGISTERED";
        await using IDiagnosticRawInputPump? receiverPump = nativeMessages ? new DiagnosticNativeRawInputPump()
            : stage is "keyboard-pump" or "keyboard-direct" or "keyboard-messages" or "keyboard-messages-mouse" or "keyboard-messages-keyboard"
            ? new DiagnosticRawInputPump(registeredPump, readPackets: !messages, registerKeyboard: !mouseOnly, registerMouse: !keyboardOnly) : null;
        RawInputDiagnosticWindow.Counters? ReadCounts()
        {
            if (window is null) return null;
            var target = window.Counts;
            return separateProbe is null ? target : target with { Keyboard = separateProbe.Counts.Keyboard, Mouse = separateProbe.Counts.Mouse };
        }
        var aggregate = new RawAggregate();
        var previousRaw = aggregate.Read();
        EventHandler<InputSourceEvent> onInput = (_, e) => aggregate.Observe(e);
        try
        {
            if (window is not null) await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (separateProbe is not null) await separateProbe.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (source is not null || split)
                Console.WriteLine($"MANAGED TARGET: hwnd=0x{(nuint)window!.Ready.Task.Result:X} processId={Environment.ProcessId} (diagnostic window only)");
            if (split)
                Console.WriteLine($"THREAD TOPOLOGY: targetThreadId={window!.Status.InstallThreadId} hookThreadId={probe!.Status.InstallThreadId} split=True");
            if (stage == "target-raw")
                Console.WriteLine("MANAGED HOOKS OMITTED: dedicated message-only receiver/thread retained; keyboard/mouse hooks intentionally not installed. Use A's native Hook evidence.");
            if (stage == "target-native-messages")
                Console.WriteLine("MANAGED HOOKS OMITTED: only keyboard Hook installation omitted from keyboard-native-messages; idle message-only Hook window/thread retained. Same native keyboard count receiver/target/sender; use A's native Hook evidence. B's zero Hook counters are expected, not acceptance.");
            if (stage == "keyboard-raw")
                Console.WriteLine("MANAGED HOOK SELECTION: keyboard=True mouse=False; same dedicated receiver/thread as target-raw. Preserve A's native and B's keyboard Hook evidence.");
            if (stage == "keyboard-split")
            {
                Console.WriteLine("MANAGED HOOK SELECTION: keyboard=True mouse=False; same target/Hook receiver threads as keyboard-raw. RAW INPUT OMITTED: no source, registration, consumer or raw counters; not zero-event evidence.");
                PrintRawRegistrations("READY");
            }
            if (source is not null)
            {
                source.InputReceived += onInput;
                // Preserve startup order: selected diagnostic receivers ready before Raw Input.
                await source.StartAsync();
            }
            if (receiverPump is not null)
            {
                await receiverPump.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (mouseOnly) Console.WriteLine("RAW REGISTRATION SELECTION: mouse=True keyboard=False; only keyboard device registration omitted from keyboard-messages. Same sender/target/Hook threads and count-only handling; no raw software/candidate classification collected.");
                if (keyboardOnly && !nativeMessages) Console.WriteLine("RAW REGISTRATION SELECTION: mouse=False keyboard=True; only mouse device registration omitted from keyboard-messages. Same sender/target/Hook threads and count-only handling; no raw software/candidate classification collected.");
                if (nativeMessages)
                {
                    string path = ((DiagnosticNativeRawInputPump)receiverPump).LoadedPath;
                    using var file = File.OpenRead(path);
                    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
                    Console.WriteLine($"NATIVE RECEIVER: sameProcessId={Environment.ProcessId} path={path} sha256={hash} bytes={file.Length}; native registration/window callback/message loop, no managed receive callbacks. Same dedicated Thread topology, keyboard-only 0x2100, timer=250ms; sender/target/native Hooks unchanged, managed selection reported separately.");
                }
                Console.WriteLine(messages
                    ? "RAW INPUT MESSAGE CONTROL: normal registration/ownership/timer/WM_INPUT cleanup retained. Packet reading/parsing/tracking/classification and observation callbacks OMITTED. Raw software/candidate evidence NOT COLLECTED; message counts do not certify keyboard delivery or software attribution."
                    : direct
                    ? "RAW INPUT DIRECT CONTROL: normal device registration/ownership/reading/cleanup retained; detached observations counted in receiver callback. No RawInputEventSource queue, consumer or subscriptions. Preserve Hook/raw/target evidence."
                    : "RAW INPUT UNREGISTERED CONTROL: original receiver window/timer/message loop retained; no device registration or event-source consumer. Pump readiness is not capture readiness.");
                if (!registeredPump) PrintRawRegistrations("READY");
            }
            var previous = ReadCounts();
            long previousInput = 0, previousDevice = 0, previousCleanup = 0;
            async Task Snapshot(string label)
            {
                if (receiverPump is not null)
                {
                    await receiverPump.SynchronizeAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    var pumpStatus = receiverPump.Status;
                    Console.WriteLine($"{label} {pumpLabel} PUMP: {pumpStatus}");
                    if (pumpStatus.Failure is not null || pumpStatus.ThreadExited || !pumpStatus.PumpReady
                        || pumpStatus.RegistrationEnabled != registeredPump || pumpStatus.PacketReadingEnabled == messages
                        || pumpStatus.KeyboardRegistrationEnabled != (registeredPump && !mouseOnly) || pumpStatus.MouseRegistrationEnabled != (registeredPump && !keyboardOnly)
                        || pumpStatus.UnexpectedEvents != 0
                        || (!registeredPump && (pumpStatus.InputMessages != 0 || pumpStatus.DeviceMessages != 0)))
                        throw new InvalidOperationException("INCONCLUSIVE: diagnostic receiver pump failed or received unexpected input");
                    if (registeredPump) PrintDirectRegistrations(label, pumpStatus.Hwnd, registerKeyboard: !mouseOnly, registerMouse: !keyboardOnly);
                    if (messages)
                    {
                        if (pumpStatus.Evidence is not null) throw new InvalidOperationException("INCONCLUSIVE: count-only control exposed raw classification evidence");
                        Console.WriteLine($"{label} MESSAGE DELTA: input={pumpStatus.InputMessages - previousInput} device={pumpStatus.DeviceMessages - previousDevice} cleanupCalls={pumpStatus.InputCleanupCalls - previousCleanup}; raw software/candidate NOT COLLECTED");
                        previousInput = pumpStatus.InputMessages; previousDevice = pumpStatus.DeviceMessages; previousCleanup = pumpStatus.InputCleanupCalls;
                    }
                    else if (direct)
                    {
                        var currentRaw = pumpStatus.Evidence ?? throw new InvalidOperationException("INCONCLUSIVE: direct raw evidence unavailable");
                        Console.WriteLine($"{label} direct raw SNAPSHOT: {currentRaw}");
                        Console.WriteLine($"{label} direct raw DELTA: {currentRaw.Delta(previousRaw)}");
                        previousRaw = currentRaw;
                    }
                    else PrintRawRegistrations(label);
                }
                if (source is not null)
                {
                    Print(source, label + " RAW STATUS");
                    if (source.Snapshot.State != InputEventSourceState.Ready || source.Snapshot.Fault != InputEventSourceFault.None
                        || source.Snapshot.Overflows != 0 || source.Snapshot.ConsumerErrors != 0)
                        throw new InvalidOperationException("INCONCLUSIVE: Raw Input source is not reliable; see RAW STATUS");
                    await source.SynchronizeAsync();
                    await RawInputTests.Until(() =>
                    {
                        var raw = aggregate.Read(); var counts = source.Snapshot;
                        return raw.CandidateKeyboard + raw.SoftwareKeyboard == counts.Keyboard
                            && raw.CandidateMouse + raw.SoftwareMouse == counts.Mouse;
                    });
                    var currentRaw = aggregate.Read();
                    Console.WriteLine($"{label} raw SNAPSHOT: {currentRaw}");
                    Console.WriteLine($"{label} raw DELTA: {currentRaw.Delta(previousRaw)}");
                    previousRaw = currentRaw;
                }
                Console.WriteLine($"ISOLATION {label}: stage={stage} processId={Environment.ProcessId} target={selection.Target} keyboardRequested={selection.Keyboard} mouseRequested={selection.Mouse} rawInputRegistered={registeredPump || source?.Snapshot.State == InputEventSourceState.Ready} emitsInput=False");
                if (window is null) return;
                await window.SynchronizeAsync();
                if (separateProbe is not null) { await separateProbe.SynchronizeAsync(); PrintProbe(window, label + " TARGET"); }
                PrintProbe(probe!, label);
                if (stage == "target-native-messages")
                {
                    var idle = probe!.Status; var idleCounts = probe.Counts;
                    if (idle.KeyboardInstalled || idle.MouseInstalled || idle.ThreadExited || idle.Failure is not null
                        || idleCounts.Keyboard.CallbackTotal != 0 || idleCounts.Mouse.CallbackTotal != 0)
                        throw new InvalidOperationException("INCONCLUSIVE: omitted-Hook receiver is not the expected healthy idle thread");
                }
                var now = ReadCounts()!;
                PrintHook(label + " keyboard DELTA", now.Keyboard.Delta(previous!.Keyboard));
                PrintHook(label + " mouse DELTA", now.Mouse.Delta(previous.Mouse));
                Console.WriteLine($"{label} target DELTA: move={now.Move - previous.Move} mouseDown/Up={now.MouseDown - previous.MouseDown}/{now.MouseUp - previous.MouseUp} keyDown/Up={now.KeyDown - previous.KeyDown}/{now.KeyUp - previous.KeyUp}");
                previous = now;
            }
            await Snapshot("READY");
            Console.WriteLine("Keep the selected A target foreground for its AUTO (native Human: native window; ManagedTarget: this managed window). Here use baseline/snapshot/done; this process sends no input. Fresh processes for each stage.");
            while (true)
            {
                string? command = await Task.Run(Console.ReadLine);
                if (command == "done" || command is null)
                {
                    if (command is null) { Console.WriteLine("NOT RUN: isolation session ended without done"); Environment.ExitCode = 2; }
                    break;
                }
                if (command is "baseline" or "snapshot") await Snapshot(command);
                else Console.WriteLine("Use baseline, snapshot or done.");
            }
            await Snapshot("END");
        }
        finally
        {
            if (receiverPump is not null)
            {
                await receiverPump.DisposeAsync();
                Console.WriteLine($"STOPPED {pumpLabel} PUMP: {receiverPump.Status}");
                if (receiverPump.Status.Failure is not null) Environment.ExitCode = 2;
            }
            if (source is not null)
            {
                try { await source.StopAsync(); Print(source, "ISOLATION RAW STOPPED"); }
                finally { source.InputReceived -= onInput; try { separateProbe?.Dispose(); } finally { window?.Dispose(); } }
                if (source.Snapshot.Fault != InputEventSourceFault.None) Environment.ExitCode = 2;
                Console.WriteLine($"Keyboard/mouse registrations remaining in this process: {WindowsRawInputReceiver.Registrations().Count(d => d.Page == 1 && d.Usage is 2 or 6)}");
            }
            if (window is not null)
            {
                window.Dispose(); PrintProbe(window, "ISOLATION STOPPED");
                if (window.Status.Failure is not null) Environment.ExitCode = 2;
            }
            if (separateProbe is not null)
            {
                separateProbe.Dispose(); PrintProbe(separateProbe, "ISOLATION HOOK STOPPED");
                if (separateProbe.Status.Failure is not null) Environment.ExitCode = 2;
            }
            if (stage is "keyboard-split" or "keyboard-pump" or "keyboard-direct" or "keyboard-messages" or "keyboard-messages-mouse" or "keyboard-messages-keyboard" or "keyboard-native-messages" or "target-native-messages") PrintRawRegistrations("STOPPED");
            Console.WriteLine("ISOLATION finished; no software/human acceptance inferred from pump health.");
        }
    }
    private static void PrintRawRegistrations(string label)
    {
        int count = WindowsRawInputReceiver.Registrations().Count(d => d.Page == 1 && d.Usage is 2 or 6);
        Console.WriteLine($"{label} RAW INPUT OMITTED: keyboard/mouse registrations in this process={count}");
        if (count != 0) throw new InvalidOperationException("INCONCLUSIVE: Raw Input registration exists in no-source control");
    }
    private static void PrintDirectRegistrations(string label, nint hwnd, bool registerKeyboard = true, bool registerMouse = true)
    {
        var devices = WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 0 or 2 or 6).ToArray();
        ushort[] usages = registerMouse ? (registerKeyboard ? [2, 6] : [2]) : [6];
        bool owned = devices.Length == usages.Length && usages.All(usage => devices.Any(d => d.Usage == usage
            && d.Target == hwnd && (d.Flags & ~0x2000u) == 0x100));
        Console.WriteLine(!registerMouse ? $"{label} KEYBOARD REGISTRATIONS: count={devices.Length} keyboardOwned={owned} mousePresent={devices.Any(d => d.Usage == 2)}"
            : registerKeyboard ? $"{label} DIRECT REGISTRATIONS: count={devices.Length} keyboardMouseOwned={owned}"
            : $"{label} MOUSE REGISTRATIONS: count={devices.Length} mouseOwned={owned} keyboardPresent={devices.Any(d => d.Usage == 6)}");
        if (!owned) throw new InvalidOperationException("INCONCLUSIVE: direct receiver registrations changed");
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
        nint hwnd;
        try { hwnd = await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { PrintProbe(window, "PROBE START FAILURE"); throw; }
        long keyDown = 0, keyUp = 0, repeats = 0, foreground = 0, background = 0, zeroDevice = 0, tagged = 0, candidateCount = 0, softwareCount = 0;
        long candidateKey = 0, candidateMouse = 0, softwareKey = 0, softwareMouse = 0, softwareDown = 0, softwareUp = 0;
        long movement = 0, buttonsDown = 0, buttonsUp = 0, wheels = 0;
        source.InputReceived += (_, e) =>
        {
            if (e.Observation.IsRugSoftwareInput)
            {
                if (e.Observation.Kind == InputEventKind.Keyboard)
                {
                    Interlocked.Add(ref softwareKey, e.Samples);
                    if (e.Observation.KeyReleased) Interlocked.Add(ref softwareUp, e.Samples); else Interlocked.Add(ref softwareDown, e.Samples);
                }
                else Interlocked.Add(ref softwareMouse, e.Samples);
                Interlocked.Add(ref softwareCount, e.Samples);
            }
            if (e.Observation.IsCandidateInput)
            {
                if (e.Observation.Kind == InputEventKind.Keyboard) Interlocked.Add(ref candidateKey, e.Samples); else Interlocked.Add(ref candidateMouse, e.Samples);
                Interlocked.Add(ref candidateCount, e.Samples);
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
        RawEvidence ReadRaw() => new(Interlocked.Read(ref candidateKey), Interlocked.Read(ref candidateMouse),
            Interlocked.Read(ref softwareKey), Interlocked.Read(ref softwareMouse), Interlocked.Read(ref softwareDown), Interlocked.Read(ref softwareUp));
        var previousProbe = window.Counts;
        var previousRaw = new RawEvidence(0, 0, 0, 0, 0, 0);
        async Task Phase(string label)
        {
            try { await source.SynchronizeAsync(); await window.SynchronizeAsync(); }
            catch { PrintProbe(window, label + " SYNCHRONIZE FAILURE"); throw; }
            await RawInputTests.Until(() => Interlocked.Read(ref candidateCount) + Interlocked.Read(ref softwareCount) == source.Snapshot.Keyboard + source.Snapshot.Mouse);
            var probe = window.Counts; var raw = ReadRaw();
            Print(source, label); PrintProbe(window, label);
            PrintHook(label + " keyboard SNAPSHOT", probe.Keyboard); PrintHook(label + " mouse SNAPSHOT", probe.Mouse);
            PrintHook(label + " keyboard DELTA", probe.Keyboard.Delta(previousProbe.Keyboard));
            PrintHook(label + " mouse DELTA", probe.Mouse.Delta(previousProbe.Mouse));
            Console.WriteLine($"{label} raw SNAPSHOT: {raw}"); Console.WriteLine($"{label} raw DELTA: {raw.Delta(previousRaw)}");
            if (label == "HUMAN BEFORE END" && probe.Keyboard.ActionableTotal == 0)
                Console.WriteLine("BRANCH: no actionable keyboard callback during BEFORE; investigate installation/thread/lifetime/hook chain, not injection flags alone.");
            if (previousProbe.Keyboard.ActionableTotal > 0 && probe.Keyboard.ActionableTotal == previousProbe.Keyboard.ActionableTotal
                && raw.Delta(previousRaw) is var interval && interval.CandidateKeyboard + interval.SoftwareKeyboard > 0)
                Console.WriteLine("BRANCH: earlier keyboard callbacks existed but this phase has Raw Input keys without hook callbacks; investigate interval blocking/fault/lifetime; timeout removal remains a hypothesis.");
            if (probe.Keyboard.TaggedUnflagged > 0)
                Console.WriteLine("BRANCH: taggedUnflagged keyboard callbacks exist; investigate classification/layout, not callback absence.");
            if (probe.Keyboard.ParseErrors != 0 || probe.Mouse.ParseErrors != 0 || window.Status.Failure is not null || window.Status.ThreadExited)
            { Console.WriteLine("INCONCLUSIVE: phase probe fault; no acceptance"); Environment.ExitCode = 2; }
            previousProbe = probe; previousRaw = raw;
        }
        PrintProbe(window, "PROBE INSTALLED");
        await source.StartAsync(); Print(source, "READY"); await Phase("STARTED");
        using var displayStop = new CancellationTokenSource();
        Task display = human ? Task.Run(async () =>
        {
            try
            {
                while (true) { await Task.Delay(1000, displayStop.Token); Print(source, "COUNTS");
                    if (window.Status.ThreadExited || window.Status.Failure is not null) PrintProbe(window, "PROBE FAULT");
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
                await Phase("HUMAN BEFORE END");
                if (source.Snapshot.Keyboard == 0 || source.Snapshot.Mouse == 0)
                { Console.WriteLine("NOT RUN: missing human keyboard/mouse evidence BEFORE automatic input"); Environment.ExitCode = 2; return; }
                Console.WriteLine("AUTO: hands off all keyboard/mouse now. Countdown 5 seconds; activate the isolated window now, then hands off; listener stays active throughout.");
                await Task.Delay(5000);
            }
            await Phase("AUTO BEFORE");
            bool verified = await SelfInputAsync(source, window, hwnd, () => Interlocked.Read(ref candidateCount), () => Interlocked.Read(ref softwareCount), ReadRaw);
            await Phase("AUTO AFTER");
            Console.WriteLine($"Raw diagnostic aggregates: zeroDevice={Interlocked.Read(ref zeroDevice)}, nonzeroExtraInfo={Interlocked.Read(ref tagged)}");
            if (!verified) Environment.ExitCode = 2;
            if (human)
            {
                var before = source.Snapshot;
                Console.WriteLine("HUMAN AFTER: repeat keyboard, mouse, foreground/background and shortcut checks in this SAME running session. Return here and type done.");
                string? answer = await Task.Run(Console.ReadLine);
                await source.SynchronizeAsync();
                await Phase("HUMAN AFTER END");
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
            window.Dispose(); PrintProbe(window, "PROBE STOPPED");
            if (window.Status.Failure is not null) Environment.ExitCode = 2;
            Console.WriteLine($"Keyboard/mouse registrations remaining in this process: {WindowsRawInputReceiver.Registrations().Count(d => d.Page == 1 && d.Usage is 2 or 6)}");
        }
    }
    private static async Task<bool> SelfInputAsync(RawInputEventSource source, RawInputDiagnosticWindow window, nint hwnd, Func<long> candidateCount, Func<long> softwareCount, Func<RawEvidence> rawEvidence)
    {
        await using var native = new InputService();
        await native.SetHumanizeConfigAsync(new HumanizeConfig { EnableJitter = false, MinClickHoldMs = 10, MaxClickHoldMs = 10, MinKeyHoldMs = 10, MaxKeyHoldMs = 10 });
        bool success = true;
        long? f8Callbacks = null, bCallbacks = null;
        foreach (bool post in new[] { false, true })
        foreach (var key in new[] { (Code: 119, Name: "F8"), (Code: 66, Name: "B") })
        {
            if (!post && !window.Activate()) { Console.WriteLine($"SendInput/{key.Name} NOT RUN: Windows foreground policy blocked isolated window activation. Activate it manually and rerun."); success = false; continue; }
            await source.SynchronizeAsync(); await window.SynchronizeAsync();
            await RawInputTests.Until(() => candidateCount() + softwareCount() == source.Snapshot.Keyboard + source.Snapshot.Mouse);
            long beforeCandidates = candidateCount(), beforeSoftware = softwareCount();
            var rawBefore = rawEvidence();
            var legacy = window.Counts;
            await native.SetTargetAsync(hwnd, post);
            // Two distinct points ensure an unchanged cursor cannot make legacy move evidence disappear.
            await native.MoveMouseAsync(60, 60, smooth: false);
            await native.MoveMouseAsync(80, 80, smooth: false);
            await native.ClickAsync(holdMs: 10);
            await native.KeyDownAsync(key.Code);
            try { } finally { await native.KeyUpAsync(key.Code); await native.MouseUpAsync(MouseButton.Left); }
            bool delivered = true;
            try
            {
                await RawInputTests.Until(() => window.Counts.KeyDown > legacy.KeyDown && window.Counts.KeyUp > legacy.KeyUp
                    && window.Counts.MouseDown > legacy.MouseDown && window.Counts.MouseUp > legacy.MouseUp && window.Counts.Move > legacy.Move);
            }
            catch (OperationCanceledException) { delivered = false; }
            await window.SynchronizeAsync(); await source.SynchronizeAsync();
            await RawInputTests.Until(() => candidateCount() + softwareCount() == source.Snapshot.Keyboard + source.Snapshot.Mouse);
            var end = source.Snapshot; var probe = window.Counts;
            string mode = (post ? "PostMessage" : "SendInput") + "/" + key.Name;
            long candidates = candidateCount() - beforeCandidates;
            long software = softwareCount() - beforeSoftware;
            var rawDelta = rawEvidence().Delta(rawBefore);
            var keyDelta = probe.Keyboard.Delta(legacy.Keyboard); var mouseDelta = probe.Mouse.Delta(legacy.Mouse);
            // Marked-but-unflagged software must not be called human interference.
            long physicalLike = keyDelta.Unflagged - keyDelta.TaggedUnflagged + mouseDelta.Unflagged - mouseDelta.TaggedUnflagged;
            Console.WriteLine($"{mode}: candidates={candidates}, rugSoftware={software}, unflaggedHookActivity={physicalLike}, injectedKeyboard={probe.InjectedKeyboard - legacy.InjectedKeyboard}, injectedMouse={probe.InjectedMouse - legacy.InjectedMouse}, taggedHookKey/Mouse={probe.TaggedKeyboard - legacy.TaggedKeyboard}/{probe.TaggedMouse - legacy.TaggedMouse}, legacyMove={probe.Move - legacy.Move}, mouseDown/Up={probe.MouseDown - legacy.MouseDown}/{probe.MouseUp - legacy.MouseUp}, keyDown/Up={probe.KeyDown - legacy.KeyDown}/{probe.KeyUp - legacy.KeyUp}");
            PrintHook(mode + " keyboard DELTA", keyDelta); PrintHook(mode + " mouse DELTA", mouseDelta);
            Console.WriteLine($"{mode} raw DELTA: {rawDelta}");
            if (!post && rawDelta.SoftwareKeyDown >= 1 && rawDelta.SoftwareKeyUp >= 1 && keyDelta.ActionableTotal == 0)
                Console.WriteLine("BRANCH: tagged Raw Input keyboard down/up complete, independent keyboard hook absent; source evidence and probe failure are separate.");
            if (keyDelta.TaggedUnflagged > 0)
                Console.WriteLine("BRANCH: tagged keyboard callbacks were unflagged; investigate flag/layout classification, not callback absence.");
            bool roundVerified = delivered;
            if (!delivered) { Console.WriteLine("NOT RUN: incomplete actual target-message evidence; round cannot pass."); success = false; }
            if (physicalLike > 0) { Console.WriteLine("INTERFERED: unflagged user-like activity during this round; no self-input verdict. NOT RUN / rerun hands off."); success = false; continue; }
            if (!post && delivered && keyDelta.ParseErrors == 0 && mouseDelta.ParseErrors == 0
                && window.Status.Failure is null && !window.Status.ThreadExited
                && rawDelta.SoftwareKeyDown >= 1 && rawDelta.SoftwareKeyUp >= 1)
            {
                if (key.Code == 119) f8Callbacks = keyDelta.ActionableTotal; else bCallbacks = keyDelta.ActionableTotal;
            }
            RawInputTests.Check(end.State == InputEventSourceState.Ready && end.Overflows == 0, "Listener lost readiness/continuity during software verification");
            if (!post && (probe.TaggedKeyboard - legacy.TaggedKeyboard < 2 || probe.TaggedMouse - legacy.TaggedMouse < 5))
            { Console.WriteLine("INCONCLUSIVE: independent injected-input probe did not observe the complete sequence; preserve Raw Input evidence, do not label as human interference."); success = false; roundVerified = false; }
            if (keyDelta.ParseErrors != 0 || mouseDelta.ParseErrors != 0 || window.Status.Failure is not null || window.Status.ThreadExited)
            { Console.WriteLine("INCONCLUSIVE: probe parse/thread fault"); success = false; roundVerified = false; }
            if (!post && (rawDelta.SoftwareKeyboard < 2 || rawDelta.SoftwareMouse < 5 || rawDelta.SoftwareKeyDown < 1 || rawDelta.SoftwareKeyUp < 1)) { Console.WriteLine("INCONCLUSIVE: native input tag did not survive the complete Raw Input sequence; do not pass candidate exclusion."); success = false; roundVerified = false; }
            if (candidates != 0)
            { Console.WriteLine("FAIL / candidate observations during software round: unattributed Raw Input remains a candidate. No time filtering or listener pause applied."); success = false; }
            else if (roundVerified) Console.WriteLine($"{mode}: PASS (real Rug.Core input including releases, live Raw Input source)");
        }
        Console.WriteLine("F8/B COMPARISON: " + CompareKeyCallbacks(f8Callbacks, bCallbacks));
        return success;
    }

}
