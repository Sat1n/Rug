using System.Diagnostics;
using System.Text.Json;
using Rug.UI.Core.Native;

/// <summary>Opt-in real WinUI fixture. No synthesized human input, grants or Hook experiment.</summary>
internal static class ManualRecovery
{
    internal static string CreateScript(string pluginId) => pluginId == "recovery_probe" ? """
        function on_init()
          local retained = 42
          rug.log('[recovery-probe] waiting 5s; detector remains active')
          rug.sleep(5000)
          rug.press_key(66,80)
          rug.set_state('steps.before', 1)
          rug.log('[recovery-probe] BEFORE B completed; entering controlled-ready recovery')
          local result = rug.agent.resolve_anomaly('controlled-ready', 'red panel must become green')
          assert(type(result) == 'table' and result.status == 'resolved' and retained == 42)
          assert(rug.get_state('steps.before') == 1)
          rug.press_key(66,80)
          rug.set_state('steps.after', 1)
          rug.log('[recovery-probe] AFTER B completed; original on_init continued once')
        end
        function on_tick() rug.sleep(60000) end
        function on_stop() rug.log('[recovery-probe] on_stop') end
        """ : ManualInputIntervention.CreateScript(pluginId);

    private static async Task ForwardEvidenceAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
            if (line.Contains("[recovery-probe]", StringComparison.Ordinal) ||
                line.Contains("Automation execution failed", StringComparison.Ordinal) ||
                line.Contains("Automation instance", StringComparison.Ordinal) || line.Contains("Recovery failed", StringComparison.Ordinal))
                Console.WriteLine("PROBE LOG: " + line[..Math.Min(line.Length, 1000)]);
    }

    private static async Task ObserveTargetAsync(RecoveryDiagnosticWindow target, CancellationToken token)
    {
        (long Down, long Up)? previous = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var counts = target.KeyCounts;
            if (counts != previous)
            {
                Console.WriteLine($"TARGET observed B down/up={counts}; passive aggregate, no human/source verdict inferred");
                previous = counts;
            }
            await Task.Delay(250, token);
        }
    }

    /// <summary>@shape fixture: isolated temporary plugin root, owned target/UI processes, aggregate target counters.
    /// @source policy: ../../Rug.UI.Core/InputIntervention.md</summary>
    public static async Task RunAsync(bool automated = false)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Rug.UI", "Rug.UI.csproj"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Workspace not found");
        string appPath = Path.Combine(root.FullName, "Rug.UI", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "Rug.UI.exe");
        if (!File.Exists(appPath)) throw new FileNotFoundException("Build Debug/x64 WinUI first", appPath);
        string plugins = Path.Combine(AppContext.BaseDirectory, "manual-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plugins);
        Process? app = null;
        RecoveryDiagnosticWindow? target = null;
        using var evidenceStop = new CancellationTokenSource();
        Task output = Task.CompletedTask, errors = Task.CompletedTask, counters = Task.CompletedTask;
        try
        {
            foreach (string id in new[] { "recovery_probe", "timer_probe", "vision_probe" })
            {
                string dir = Path.Combine(plugins, id); Directory.CreateDirectory(dir);
                string[] permissions = id == "recovery_probe" ? ["input", "timer", "agent", "vision.capture"] : id == "vision_probe" ? ["vision.capture", "timer"] : ["timer"];
                await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new
                { id, name = id, version = "1", entry = "main.lua", targetProcess = "isolated-probe", permissions, inputDelivery = "Win32PostMessage" }));
                await File.WriteAllTextAsync(Path.Combine(dir, "ui.json"), ManualInputIntervention.CreateSchema(id));
                await File.WriteAllTextAsync(Path.Combine(dir, "main.lua"), CreateScript(id));
                await new Rug.UI.Core.Schema.SchemaParser().ParseFileAsync(Path.Combine(dir, "ui.json"));
            }
            target = new RecoveryDiagnosticWindow();
            nint hwnd = await target.Ready;
            var start = new ProcessStartInfo(appPath) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["RUG_PLUGINS_ROOT"] = plugins;
            start.Environment["Logging__LogLevel__Default"] = "Warning";
            start.Environment["Logging__LogLevel__Rug.UI.Core.Services.LuaRuntime"] = "Information";
            start.Environment["Logging__Console__FormatterName"] = "simple";
            start.Environment["Logging__Console__FormatterOptions__SingleLine"] = "true";
            app = Process.Start(start) ?? throw new InvalidOperationException("WinUI launch failed");
            output = ForwardEvidenceAsync(app.StandardOutput);
            errors = ForwardEvidenceAsync(app.StandardError);
            using (var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while (true)
                {
                    app.Refresh();
                    if (app.HasExited) throw new InvalidOperationException("WinUI exited before fixture readiness: " + app.ExitCode);
                    if (app.MainWindowHandle != 0) break;
                    await Task.Delay(100, readyTimeout.Token);
                }
            }
            Console.WriteLine($"RECOVERY MANUAL READY: target=0x{hwnd:X} UIprocess={app.Id}; red/green controlled target, no fixture Hooks/Raw input.");
            Console.WriteLine("1. Bind red target in DevTools; optionally start timer/vision. Select recovery_probe, start bound task and explicitly grant permissions.");
            Console.WriteLine("2. Hands off during 5s wait and BEFORE B. Expect B=(1,1), AwaitingHuman and ManualControl; original instance must remain.");
            Console.WriteLine("3. Click target or press Space to turn green. Return control in host: Lua must remain waiting. Explicitly click 验证并继续.");
            Console.WriteLine("4. Fresh WGC validation must pass, AFTER B makes (2,2); init locals/state preserved. Do not press B yourself. No automatic recovery approval.");
            Console.WriteLine("5. Normal human input now stops the input run; timer/vision remain. Restart red scenario to test Stop from AwaitingHuman and failed red validation.");
            Console.WriteLine("status: aggregate counts only; done: clean owned UI/target/temp plugins. Human acceptance NOT RUN until user confirmation.");
            counters = ObserveTargetAsync(target, evidenceStop.Token);
            if (automated)
            {
                var script = Path.Combine(root.FullName, "Rug.UI.Tests", "RecoveryUiSmoke.ps1");
                var probe = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root.FullName };
                foreach (string argument in new[] { "-NoProfile", "-File", script, "-AppProcessId", app.Id.ToString(), "-TargetHwnd", hwnd.ToInt64().ToString() }) probe.ArgumentList.Add(argument);
                using var test = Process.Start(probe) ?? throw new InvalidOperationException("Recovery UI automation failed to launch");
                var stdout = test.StandardOutput.ReadToEndAsync(); var stderr = test.StandardError.ReadToEndAsync();
                using var automationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try { await test.WaitForExitAsync(automationTimeout.Token); }
                catch { test.Kill(entireProcessTree: true); await test.WaitForExitAsync(); throw; }
                Console.WriteLine(await stdout);
                if (test.ExitCode != 0) throw new InvalidOperationException(await stderr);
                if (target.KeyCounts != (2, 2)) throw new InvalidOperationException("UI recovery target delivery was not exactly (2,2)");
                return;
            }
            while (Console.ReadLine() is { } line && line.Trim() != "done")
                if (line.Trim() == "status") Console.WriteLine($"TARGET aggregate B down/up={target.KeyCounts}; no human/source verdict inferred");
        }
        finally
        {
            evidenceStop.Cancel();
            try { await counters; } catch (OperationCanceledException) when (evidenceStop.IsCancellationRequested) { }
            if (app is not null)
            {
                if (!app.HasExited)
                {
                    app.CloseMainWindow();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await app.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) { app.Kill(entireProcessTree: true); await app.WaitForExitAsync(); }
                }
                await Task.WhenAll(output, errors);
                app.Dispose();
            }
            if (target is not null) Console.WriteLine($"TARGET final B down/up={target.KeyCounts}; no human/source verdict inferred");
            target?.Dispose();
            if (!Path.GetFullPath(plugins).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary plugin directory escaped test output.");
            Directory.Delete(plugins, recursive: true);
        }
    }
}
