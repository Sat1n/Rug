using System.Diagnostics;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;

/// <summary>Owned WinUI host with actual consent, planned handoff and explicit verification UI.
/// UIA and test-host scene repair are automation, never evidence of human takeover.</summary>
internal static class ControlledRecoveryUi
{
    internal sealed record Result(bool InteractionsPassed, int? ExitCode, int TargetDown, int TargetUp, Guid? IncidentId, Guid? TraceId, string? Diagnostic);
    internal static async Task<Result> RunAsync(string repo, string root, bool manual = false)
    {
        string install = ControlledRecoverySample.Install(repo, root);
        using var target = new CaptureGeometryDiagnosticWindow(install, 180, 155, recovery: true);
        nint hwnd = await target.Ready;
        var start = new ProcessStartInfo(Path.Combine(repo, "Rug.UI", "bin", "x64", ControlledRecoverySample.Config, "net10.0-windows10.0.22621.0", "win-x64", "Rug.UI.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var setting in new[] { ("RUG_PLUGINS_ROOT", "plugins"), ("RUG_TRACE_ROOT", "traces"), ("RUG_ANOMALY_ROOT", "incidents"), ("RUG_CHECKPOINT_ROOT", "checkpoints") })
            start.Environment[setting.Item1] = Path.Combine(root, setting.Item2);
        start.Environment["RUG_EXACT_BOUND_CAPTURE"] = "1"; // Explicit fixture host policy, never Lua metadata.
        using var app = Process.Start(start)!;
        var stdout = app.StandardOutput.ReadToEndAsync(); var stderr = app.StandardError.ReadToEndAsync();
        bool passed = false; string? diagnostic = null; AnomalyLogModel? incident = null;
        try
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                app.Refresh(); if (app.HasExited) throw new InvalidOperationException("Owned UI exited during startup");
                if (app.MainWindowHandle != 0) break; // Cache MAIN before opening review window.
                if (watch.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("Owned UI startup");
                await Task.Delay(40);
            }
            if (manual)
            {
                Console.WriteLine("MANUAL RECOVERY: bind the patterned target in DevTools; select Controlled visual recovery; start bound task and explicitly approve permissions.");
                Console.WriteLine("Expect BEFORE 1/1 then AwaitingHuman/ManualControl. Return alone must not continue. Red verification rejects. Type repair here (or Space in target), Return control, then Verify recovery. Expect AFTER 1/1 and original Lua continuation.");
                Console.WriteLine("Use Stop after continuation; open trace viewer, refresh incidents, select the actual incident and Locate incident trace. Commands: repair, status, done. Human acceptance remains unconfirmed until you report observations.");
                string? command;
                while ((command = Console.ReadLine()) is not null && !command.Equals("done", StringComparison.OrdinalIgnoreCase))
                {
                    if (command.Equals("repair", StringComparison.OrdinalIgnoreCase)) target.Repair();
                    Console.WriteLine("TARGET actual aggregate down/up=" + target.ClickCounts + "; no human/source verdict inferred");
                }
            }
            else
            {
                var ps = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { "-NoProfile", "-File", Path.Combine(repo, "Rug.UI.Tests", "ControlledRecoveryUi.ps1"), "-AppProcessId", app.Id.ToString(), "-TargetHwnd", hwnd.ToInt64().ToString(), "-TraceRoot", Path.Combine(root,"traces") }) ps.ArgumentList.Add(arg);
                using var probe = Process.Start(ps)!; var output = probe.StandardOutput.ReadToEndAsync(); var errors = probe.StandardError.ReadToEndAsync();
                try { await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
                catch { if (!probe.HasExited) { probe.Kill(true); await probe.WaitForExitAsync(); } Console.Write(await output); Console.Write(await errors); throw; }
                Console.Write(await output); if (probe.ExitCode != 0) throw new InvalidOperationException(await errors);
            }
            ControlledRecoverySample.Check(target.ClickCounts == (2, 2), "UI Lua path did not deliver BEFORE/AFTER pairs");
            string record = Directory.GetFiles(Path.Combine(root, "incidents"), "*.json").Single();
            incident = new IncidentStore(Path.Combine(root, "incidents")).Read(Path.GetFileName(record)).Record;
            ControlledRecoverySample.Check(incident is { SchemaVersion: 1, Verification.Status: "resolved" } && incident.TraceLink?.TraceId is not null, "UI resolved archive/link missing");
            passed = true;
        }
        catch (Exception error) { diagnostic = error.ToString(); }
        finally
        {
            if (!app.HasExited)
            {
                app.CloseMainWindow();
                try { await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                catch { passed = false; diagnostic += "; Host close timed out; forced owned-process cleanup"; app.Kill(true); await app.WaitForExitAsync(); }
            }
        }
        await stdout; string shutdown = await stderr;
        if (app.ExitCode != 0) diagnostic += "; UI exit=" + app.ExitCode + "; " + shutdown[..Math.Min(2048, shutdown.Length)];
        if (incident?.TraceLink is { HostSessionId: { } session, TraceId: { } trace })
        {
            var page = new TraceFileStore(Path.Combine(root, "traces")).Read(session, trace, limit: 256);
            if (page.Status != TraceReadStatus.Valid || page.Summary is not { RunCleanupCompleted: true, CleanupSucceeded: true, ActualWorkOutstanding: 0 })
            { passed = false; diagnostic += "; UI persisted trace/cleanup not complete: " + page.Status; }
        }
        Console.WriteLine($"RECOVERY UI interactions={passed}; target={target.ClickCounts}; exit={app.ExitCode}; {diagnostic}");
        target.Dispose();
        string plugins = Path.GetFullPath(Path.Combine(root,"plugins"));
        ControlledRecoverySample.Check(plugins.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"unsafe UI installation cleanup");
        Directory.Delete(plugins,true); // Owned host has exited; retain incident/trace files, not installation copies.
        return new(passed, app.ExitCode, target.ClickCounts.Down, target.ClickCounts.Up, incident?.IncidentId, incident?.TraceLink?.TraceId, diagnostic);
    }
}
