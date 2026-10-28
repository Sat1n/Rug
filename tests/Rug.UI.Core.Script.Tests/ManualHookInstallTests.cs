using System.Diagnostics;
using System.Text.RegularExpressions;

internal static class ManualHookInstallTests
{
    private static void Check(bool value, string message) => RawInputTests.Check(value, message);
    private static readonly string[] BArguments = ["--hook-isolation-stage", "keyboard-native-callback", "--hook-module", "callback", "--manual-hook-install"];
    internal static async Task RunAsync()
    {
        foreach (var (stage, module, order) in new[] { ("both", "callback", (string?)null),
            ("keyboard-native-callback", "original", (string?)null), ("keyboard-native-callback", "callback", "raw-first") })
        {
            try { HookRawStartup.Validate(stage, module, order, manualInstall: true); throw new Exception("Invalid manual mode accepted"); }
            catch (ArgumentException) { }
        }
        HookRawStartup.Validate("keyboard-native-callback", "callback", null, manualInstall: true);
        if (!OperatingSystem.IsWindows() || !File.Exists(NativeKeyboardHookTests.LibraryPath)
            || !File.Exists(DiagnosticNativeRawInputPump.LibraryPath)
            || !File.Exists(Path.Combine(Path.GetDirectoryName(NativeKeyboardHookTests.LibraryPath)!, "Rug.Core.dll")))
        { Console.WriteLine("NOT RUN: manual B/C Hook order requires Windows and native diagnostic/Core DLLs"); return; }
        foreach (string order in new[] { "B-C", "C-B", "B-C" }) await PairedAsync(order);
        foreach (var (install, eof) in new[] { (false, false), (false, true), (true, true) })
        {
            await using var b = new ConsoleChild(BArguments);
            await b.UntilAsync("MANUAL HOOK COMMANDS:");
            if (install) { await b.WriteAsync("install"); await b.UntilAsync("MANUAL HOOK INSTALLED:"); }
            await b.EndAsync(eof);
            Check(b.Process.ExitCode == 2 && b.Lines.Any(l => l.StartsWith("NOT RUN:")), "Incomplete B session incorrectly accepted");
            CheckBCleanup(b);
        }
        foreach (string[] invalid in new[] { new[] { "--manual-hook-install" },
            new[] { "--hook-isolation-stage", "both", "--manual-hook-install" },
            new[] { "--hook-isolation-stage", "keyboard-native-callback", "--hook-module", "original", "--manual-hook-install" },
            BArguments.Concat(["--startup-order", "raw-first"]).ToArray() })
        {
            await using var rejected = new ConsoleChild(invalid);
            await rejected.EndAsync(eof: true);
            Check(rejected.Process.ExitCode == 1 && rejected.Lines.Any(l => l.StartsWith("ISOLATION FAILED:"))
                && !rejected.Lines.Any(l => l.StartsWith("CORE LOADED:") || l.StartsWith("MANAGED TARGET:")), "Invalid CLI entered native startup");
        }
        Console.WriteLine("Manual Hook install: three real B/C order pairs, single install, Raw identity preservation, pre-install refusal, three incomplete-session cleanups and four fail-before-load CLI checks passed (no input emission/AUTO acceptance)");
    }
    // @shape All windows and B Raw registration ready before either one-shot Hook install; QPC proves both orders.
    // @source RawInputDiagnostics.cs#class:RawInputDiagnostics
    private static async Task PairedAsync(string order)
    {
        await using var b = new ConsoleChild(BArguments);
        await b.UntilAsync("MANUAL HOOK COMMANDS:");
        string prepared = b.Lines.Single(l => l.StartsWith("MANUAL HOOK PREPARED:"));
        Check(prepared.Contains("hookInstalled=False") && Clock(prepared, "hookActualBeforeTicks") == 0
            && Clock(prepared, "rawReadyObservedTicks") > 0
            && b.Lines.Any(l => l.Contains("PREPARED KEYBOARD REGISTRATIONS: count=1 keyboardOwned=True mousePresent=False")), "B did not await actual registration before PREPARED");
        string rawIdentity = Identity(b.Lines.Single(l => l.StartsWith("PREPARED NATIVE KEYBOARD MESSAGES PUMP:")));
        await using var c = new ConsoleChild(["--hook-observer"]);
        await c.UntilAsync("OBSERVER COMMANDS:");
        string cPrepared = c.Lines.Single(l => l.StartsWith("OBSERVER PREPARED: processId="));
        Check(cPrepared.Contains("hookInstalled=False target=False rawRegistrations=0") && b.Process.Id != c.Process.Id,
            "C prepared with a Hook/target/Raw ownership or shares B process");
        await b.WriteAsync("baseline"); await b.UntilAsync("MANUAL HOOK NOT READY:");
        await c.WriteAsync("baseline"); await c.UntilAsync("OBSERVER NOT READY:");
        async Task<string> Install(ConsoleChild child, bool isB)
        {
            await child.WriteAsync("install");
            return await child.UntilAsync(isB ? "MANUAL HOOK INSTALLED:" : "OBSERVER INSTALLED: processId=");
        }
        string installedB, installedC;
        if (order == "B-C") { installedB = await Install(b, true); installedC = await Install(c, false); }
        else { installedC = await Install(c, false); installedB = await Install(b, true); }
        long bStart = Clock(installedB, "hookActualBeforeTicks"), bEnd = Clock(installedB, "hookActualAfterTicks");
        long cStart = Clock(installedC, "hookActualBeforeTicks"), cEnd = Clock(installedC, "hookActualAfterTicks");
        long allReady = Math.Max(Clock(prepared, "observedTicks"), Clock(cPrepared, "afterTicks"));
        Check(bStart > allReady && cStart > allReady && bEnd >= bStart && cEnd >= cStart
            && (order == "B-C" ? bEnd < cStart : cEnd < bStart), "Hook installed before both processes prepared or requested order not reflected in QPC");
        string repeatedB = await Install(b, true), repeatedC = await Install(c, false);
        Check(Clock(repeatedB, "hookActualBeforeTicks") == bStart && Clock(repeatedB, "hookActualAfterTicks") == bEnd
            && Clock(repeatedC, "hookActualBeforeTicks") == cStart && Clock(repeatedC, "hookActualAfterTicks") == cEnd,
            "Duplicate command reinstalled Hook");
        await b.WriteAsync("snapshot"); await b.UntilAsync("MANUAL HOOK snapshot:");
        await c.WriteAsync("snapshot"); await c.UntilAsync("OBSERVER snapshot tagged-key DELTA:");
        Check(Identity(b.Lines.Last(l => l.StartsWith("snapshot NATIVE KEYBOARD MESSAGES PUMP:"))) == rawIdentity
            && b.Lines.Last(l => l.StartsWith("MANUAL HOOK snapshot:")).Contains("hookInstalled=True")
            && b.Lines.Any(l => l.Contains("snapshot KEYBOARD REGISTRATIONS: count=1 keyboardOwned=True mousePresent=False")),
            "Install changed Raw window/thread/registration");
        await b.EndAsync(); await c.EndAsync();
        Check(b.Process.ExitCode == 0 && c.Process.ExitCode == 0, "Prepared/installed roles failed cleanup");
        CheckBCleanup(b);
        Check(c.Lines.Any(l => l.StartsWith("OBSERVER CLEANUP:") && l.Contains("threadExited=True attached=0 rawRegistrations=0")), "C leaked resources");
    }
    private static string Identity(string line)
    {
        var match = Regex.Match(line, @"ThreadId = \d+, Hwnd = \d+");
        Check(match.Success, "Missing Raw thread/window identity"); return match.Value;
    }
    private static long Clock(string line, string name)
    {
        var match = Regex.Match(line, @"\b" + Regex.Escape(name) + @"=(\d+)");
        Check(match.Success, "Missing clock field " + name); return long.Parse(match.Groups[1].Value);
    }
    private static void CheckBCleanup(ConsoleChild b)
    {
        Check(b.Lines.Any(l => l.StartsWith("STOPPED NATIVE KEYBOARD MESSAGES PUMP:") && l.Contains("Hwnd = 0") && l.Contains("ThreadExited = True") && l.Contains("Failure = ,"))
            && b.Lines.Any(l => l.StartsWith("ISOLATION HOOK STOPPED:") && l.Contains("threadExited=True failure=None"))
            && b.Lines.Any(l => l.StartsWith("ISOLATION HOOK STOPPED NATIVE CALLBACK:") && l.Contains("attached=0 threadMismatch=0"))
            && b.Lines.Contains("STOPPED RAW INPUT OMITTED: keyboard/mouse registrations in this process=0"), "B did not release native receiver/Hook/window/thread");
    }
    private sealed class ConsoleChild : IAsyncDisposable
    {
        internal Process Process { get; }
        internal List<string> Lines { get; } = [];
        private readonly Task<string> _errors;
        internal ConsoleChild(string[] args)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(ManualHookInstallTests).Assembly.Location);
            foreach (string arg in args) start.ArgumentList.Add(arg);
            Process = System.Diagnostics.Process.Start(start)!; _errors = Process.StandardError.ReadToEndAsync();
        }
        private async Task<string?> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string? line = await Process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is not null) { Check(Lines.Count < 400, "Child output exceeded diagnostic bound"); Lines.Add(line); }
            return line;
        }
        internal async Task<string> UntilAsync(string prefix)
        {
            while (true)
            {
                string? line = await ReadAsync();
                if (line is null) throw new Exception("Child exited before " + prefix + "; " + await _errors);
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
            }
        }
        internal async Task WriteAsync(string value) { await Process.StandardInput.WriteLineAsync(value); await Process.StandardInput.FlushAsync(); }
        internal async Task EndAsync(bool eof = false)
        {
            if (eof) Process.StandardInput.Close(); else await WriteAsync("done");
            while (await ReadAsync() is not null) { }
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(string.IsNullOrEmpty(await _errors), "Child failed: " + await _errors);
        }
        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited)
            {
                Process.StandardInput.Close();
                try { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync(); }
            }
            Process.Dispose();
        }
    }
}
