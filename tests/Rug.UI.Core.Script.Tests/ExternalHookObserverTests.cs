using System.Diagnostics;
using System.Text.RegularExpressions;
using Rug.UI.Core.Native;

internal static class ExternalHookObserverTests
{
    private static void Check(bool value, string message) => RawInputTests.Check(value, message);
    internal static async Task RunAsync()
    {
        foreach (string[] invalid in new[] { new[] { "--hook-observer", "--hook-isolation-stage", "both" },
            new[] { "--hook-observer", "--startup-order", "raw-first" }, Array.Empty<string>(), new[] { "--hook-observer", "--raw-input-diagnostic" } })
        {
            try { ExternalHookObserver.ValidateArguments(invalid); throw new Exception("Mixed observer role accepted"); }
            catch (ArgumentException) { }
        }
        ExternalHookObserver.ValidateArguments(["--hook-observer"]);
        if (!OperatingSystem.IsWindows() || !File.Exists(NativeKeyboardHookTests.LibraryPath) || !File.Exists(DiagnosticNativeRawInputPump.LibraryPath))
        { Console.WriteLine("NOT RUN: external Hook observer requires Windows and diagnostic DLLs"); return; }
        for (int run = 0; run < 3; run++) await SeparateProcessAsync(install: true);
        await SeparateProcessAsync(install: false);
        await SeparateProcessAsync(install: true, eof: true);
        Console.WriteLine("External observer: real separate-process Hook ownership, parent Raw/target preservation, single install, pre-install refusal, EOF and full cleanup passed (5 lifecycles; no software AUTO or human acceptance)");
    }
    // @shape B target/Raw remain in this process; C owns only its native Hook and message thread.
    // @source ExternalHookObserver.cs#class:ExternalHookObserver
    private static async Task SeparateProcessAsync(bool install, bool eof = false)
    {
        using var target = new RawInputDiagnosticWindow(false, false);
        using var idle = new RawInputDiagnosticWindow(false, false, messageOnly: true);
        await Task.WhenAll(target.Ready.Task, idle.Ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await using var raw = new DiagnosticNativeRawInputPump();
        await raw.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var before = raw.Status;
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ExternalHookObserver).Assembly.Location);
        start.ArgumentList.Add("--hook-observer");
        using var child = Process.Start(start)!;
        Task<string> errors = child.StandardError.ReadToEndAsync();
        async Task<string> ReadUntil(string prefix)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                string? line = await child.StandardOutput.ReadLineAsync(deadline.Token);
                if (line is null) throw new Exception("Observer exited before " + prefix + "; stderr=" + await errors);
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
            }
        }
        async Task Write(string command) { await child.StandardInput.WriteLineAsync(command); await child.StandardInput.FlushAsync(); }
        void CheckParent()
        {
            var registrations = WindowsRawInputReceiver.Registrations().Where(d => d.Page == 1 && d.Usage is 0 or 2 or 6).ToArray();
            Check(registrations.Length == 1 && registrations[0].Usage == 6 && registrations[0].Target == raw.Status.Hwnd
                && raw.Status is { PumpReady: true, ThreadExited: false, Failure: null, PacketReadingEnabled: false }
                && raw.Status.Hwnd == before.Hwnd && raw.Status.ThreadId == before.ThreadId
                && !target.Status.KeyboardInstalled && !idle.Status.KeyboardInstalled && !idle.Status.MouseInstalled,
                "C affected B's target/registration/threads");
        }
        try
        {
            string prepared = await ReadUntil("OBSERVER PREPARED: processId=");
            Check(prepared.Contains($"processId={child.Id} ") && child.Id != Environment.ProcessId
                && prepared.Contains("hookInstalled=False target=False rawRegistrations=0 coreLoaded=False rawReceiverLoaded=False emitsInput=False"), "Observer role not isolated");
            await ReadUntil("OBSERVER COMMANDS:");
            await Write("baseline");
            await ReadUntil("OBSERVER NOT READY:");
            CheckParent();
            if (install)
            {
                await Write("install");
                string installed = await ReadUntil("OBSERVER INSTALLED: processId=");
                Check(installed.Contains("hookInstalled=True") && !installed.Contains("hookActualBeforeTicks=0"), "Window readiness was mistaken for Hook readiness");
                string binding = await ReadUntil("OBSERVER INSTALLED NATIVE CALLBACK:");
                var fields = Regex.Matches(binding, @"(callbackModule|hookInstallHmod)=0x([0-9A-F]+)");
                Check(fields.Count == 2 && fields[0].Groups[2].Value == fields[1].Groups[2].Value
                    && binding.Contains("attached=1 threadMismatch=0"), "C callback module/thread binding incorrect");
                await Write("install");
                string duplicate = await ReadUntil("OBSERVER INSTALLED: processId=");
                Check(Regex.Match(installed, @"hookActualBeforeTicks=\d+ hookActualAfterTicks=\d+").Value
                    == Regex.Match(duplicate, @"hookActualBeforeTicks=\d+ hookActualAfterTicks=\d+").Value, "Repeated command reinstalled Hook");
                await Write("baseline");
                await ReadUntil("OBSERVER baseline tagged-key DELTA:");
                // Target protocol only: posted messages do not substitute for real SendInput/Raw classification.
                target.PostTestKeyMessages(0x77); target.PostTestKeyMessages(0x42, system: true);
                await target.SynchronizeAsync();
                Check(target.QueryTargetMetricForTest(6) == 1 && target.QueryTargetMetricForTest(7) == 1
                    && target.QueryTargetMetricForTest(8) == 1 && target.QueryTargetMetricForTest(9) == 1, "B target protocol changed");
                await Write("snapshot");
                string evidence = await ReadUntil("OBSERVER snapshot tagged-key DELTA:");
                Check(evidence.Contains("raw software/candidate NOT COLLECTED"), "Passive counters falsely claimed Raw acceptance");
                CheckParent();
            }
            if (eof) child.StandardInput.Close(); else await Write("done");
            string cleanup = await ReadUntil("OBSERVER CLEANUP:");
            Check(cleanup.Contains("threadExited=True attached=0 rawRegistrations=0"), "C did not clean up its Hook thread/counter");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(child.ExitCode == (install && !eof ? 0 : 2), "Incomplete observer session incorrectly passed; " + await errors);
            CheckParent();
        }
        finally
        {
            if (!child.HasExited)
            {
                child.StandardInput.Close();
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            }
        }
        await raw.DisposeAsync(); target.Dispose(); idle.Dispose();
        Check(raw.Status.ThreadExited && target.Status.ThreadExited && idle.Status.ThreadExited
            && !WindowsRawInputReceiver.Registrations().Any(d => d.Page == 1 && d.Usage is 0 or 2 or 6), "External role lifecycle leaked B resources");
    }
}
