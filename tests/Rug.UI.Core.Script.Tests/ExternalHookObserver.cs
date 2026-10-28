using System.Diagnostics;
using System.Security.Cryptography;
using Rug.UI.Core.Native;

/// <summary>Process C: the existing native keyboard probe, without a target, Raw receiver or Core load.</summary>
internal sealed class ExternalHookObserver : IDisposable
{
    internal sealed record Sample(long BeforeTicks, long AfterTicks, RawInputDiagnosticWindow.ProbeStatus Status,
        NativeKeyboardHookDiagnostic.Snapshot Native, int Registrations);
    internal RawInputDiagnosticWindow Probe { get; } = new(true, false, messageOnly: true,
        nativeKeyboardPath: NativeKeyboardHookTests.LibraryPath, deferKeyboardHook: true);
    private NativeKeyboardHookDiagnostic.Snapshot _previous;

    internal static void ValidateArguments(string[] args)
    {
        if (args.Length != 1 || args[0] != "--hook-observer")
            throw new ArgumentException("Use --hook-observer alone in a fresh process C; no isolation/source/input options allowed.");
    }
    internal async Task ReadyAsync()
    {
        await Probe.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var process = Process.GetCurrentProcess();
        if (process.Modules.Cast<ProcessModule>().Any(m => Path.GetFileName(m.FileName) is "Rug.Core.dll" or "Rug.NativeRawReceiver.dll"))
            throw new InvalidOperationException("Observer must not load Core or the Raw receiver; use a fresh process.");
        if (Registrations() != 0) throw new InvalidOperationException("Observer process already owns Raw Input registrations.");
    }
    internal Task InstallAsync() => Probe.InstallDeferredKeyboardHookAsync(); // Existing single-install gate; never rehooks.
    private static int Registrations() => WindowsRawInputReceiver.Registrations().Count(d => d.Page == 1 && d.Usage is 0 or 2 or 6);
    // @shape Detached aggregate evidence and QPC bounds; synchronization certifies only pump health.
    // @source ../../Rug.UI.Core/Native/RawInputDiagnosticWindow.cs#class:RawInputDiagnosticWindow
    internal async Task<Sample> SampleAsync()
    {
        long before = Stopwatch.GetTimestamp();
        await Probe.SynchronizeAsync();
        var status = Probe.Status;
        var native = Probe.NativeKeyboard!.Status;
        int registrations = Registrations();
        if (status.ThreadExited || status.Failure is not null || status.MouseInstalled || registrations != 0
            || native.ThreadMismatch != 0 || native.Attached != (status.KeyboardInstalled ? 1ul : 0ul)
            || status.KeyboardInstalled && (native.ThreadId != status.InstallThreadId || Probe.HookInstallModule != Probe.NativeKeyboard.ProcedureModule))
            throw new InvalidOperationException("INCONCLUSIVE: observer thread, binding or registration changed.");
        return new(before, Stopwatch.GetTimestamp(), status, native, registrations);
    }
    internal async Task PrintAsync(string label)
    {
        var sample = await SampleAsync();
        Console.WriteLine($"OBSERVER {label}: processId={Environment.ProcessId} windowReady=True hookInstalled={sample.Status.KeyboardInstalled} target=False rawRegistrations={sample.Registrations} coreLoaded=False rawReceiverLoaded=False emitsInput=False beforeTicks={sample.BeforeTicks} afterTicks={sample.AfterTicks} frequency={Stopwatch.Frequency} hookActualBeforeTicks={Probe.KeyboardInstallStartedTicks} hookActualAfterTicks={Probe.KeyboardInstallCompletedTicks}");
        RawInputDiagnostics.PrintProbe(Probe, "OBSERVER " + label);
        RawInputDiagnostics.PrintHook("OBSERVER " + label + " keyboard SNAPSHOT", sample.Native.Counts);
        RawInputDiagnostics.PrintHook("OBSERVER " + label + " keyboard DELTA", sample.Native.Counts.Delta(_previous.Counts));
        Console.WriteLine($"OBSERVER {label} tagged-key DELTA: F8 down/up={sample.Native.F8Down - _previous.F8Down}/{sample.Native.F8Up - _previous.F8Up} B down/up={sample.Native.BDown - _previous.BDown}/{sample.Native.BUp - _previous.BUp}; raw software/candidate NOT COLLECTED; message barrier is not Hook health evidence.");
        _previous = sample.Native;
    }
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(NativeKeyboardHookTests.LibraryPath))
        { Console.WriteLine("NOT RUN: Windows x64 and native Hook DLL required; run tests/run_native_input_probe.ps1 -Mode Build"); Environment.ExitCode = 2; return; }
        using var observer = new ExternalHookObserver();
        try
        {
            await observer.ReadyAsync();
            string path = observer.Probe.NativeKeyboard!.LoadedPath;
            using (var file = File.OpenRead(path))
                Console.WriteLine($"OBSERVER CALLBACK LOADED: processId={Environment.ProcessId} path={path} bytes={file.Length} sha256={Convert.ToHexString(SHA256.HashData(file))}");
            await observer.PrintAsync("PREPARED");
            Console.WriteLine("OBSERVER COMMANDS: install (once, after B Raw READY), baseline, snapshot, done. C sends no input, owns no target/Raw registration; keep B target foreground during A AUTO. Use fresh A/B/C processes.");
            while (true)
            {
                string? command = await Task.Run(Console.ReadLine);
                if (command is null || command == "done")
                {
                    if (command is null) { Console.WriteLine("NOT RUN: observer ended without done"); Environment.ExitCode = 2; }
                    if (!observer.Probe.Status.KeyboardInstalled) { Console.WriteLine("NOT RUN: observer Hook never installed"); Environment.ExitCode = 2; }
                    break;
                }
                if (command == "install") { await observer.InstallAsync(); await observer.PrintAsync("INSTALLED"); }
                else if (command is "baseline" or "snapshot")
                {
                    if (!observer.Probe.Status.KeyboardInstalled) Console.WriteLine("OBSERVER NOT READY: type install before baseline/snapshot.");
                    else await observer.PrintAsync(command);
                }
                else Console.WriteLine("Use install, baseline, snapshot or done.");
            }
            await observer.PrintAsync("END");
        }
        finally
        {
            observer.Dispose();
            RawInputDiagnostics.PrintProbe(observer.Probe, "OBSERVER STOPPED");
            int registrations = Registrations();
            Console.WriteLine($"OBSERVER CLEANUP: processId={Environment.ProcessId} threadExited={observer.Probe.Status.ThreadExited} attached={observer.Probe.NativeKeyboard!.Status.Attached} rawRegistrations={registrations}; no software/human acceptance inferred.");
            if (observer.Probe.Status.Failure is not null || !observer.Probe.Status.ThreadExited
                || observer.Probe.NativeKeyboard.Status.Attached != 0 || registrations != 0) Environment.ExitCode = 2;
        }
    }
    public void Dispose() => Probe.Dispose();
}
