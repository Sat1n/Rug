using System.Diagnostics;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;

/// <summary>Opt-in: target and fixture outlive each separately restarted WinUI host. No automatic grants or human simulation.</summary>
internal static class ManualCheckpointRestore
{
    public static async Task RunAsync(bool automated = false)
    {
        DirectoryInfo? repo = new(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Rug.UI", "Rug.UI.csproj"))) repo = repo.Parent;
        if (repo is null) throw new DirectoryNotFoundException("Repository not found.");
        string appPath = Path.Combine(repo.FullName, "Rug.UI", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "Rug.UI.exe");
        if (!File.Exists(appPath)) throw new FileNotFoundException("Build Debug/x64 WinUI first.", appPath);
        string root = Path.Combine(AppContext.BaseDirectory, "checkpoint-manual-" + Guid.NewGuid().ToString("N"));
        string plugin = Path.Combine(root, "plugins", "checkpoint_probe"); Directory.CreateDirectory(plugin);
        foreach (string file in new[] { "manifest.json", "main.lua", "ui.json" }) File.Copy(Path.Combine(repo.FullName, "plugins", "checkpoint_probe", file), Path.Combine(plugin, file));
        using var target = new RecoveryDiagnosticWindow(); var hwnd = await target.Ready; target.SetReady(true);
        Process? app = null; Task output = Task.CompletedTask, errors = Task.CompletedTask;
        async Task CloseHost()
        {
            if (app is null) return;
            if (!app.HasExited)
            {
                app.CloseMainWindow();
                try { await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { app.Kill(entireProcessTree: true); await app.WaitForExitAsync(); }
            }
            await Task.WhenAll(output, errors); app.Dispose(); app = null;
        }
        async Task OpenHost()
        {
            await CloseHost();
            var start = new ProcessStartInfo(appPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["RUG_PLUGINS_ROOT"] = Path.Combine(root, "plugins");
            start.Environment["RUG_CHECKPOINT_ROOT"] = Path.Combine(root, "records");
            start.Environment["Logging__LogLevel__Rug.UI.Core.Services.LuaRuntime"] = "Information";
            start.Environment["Logging__Console__FormatterOptions__SingleLine"] = "true";
            app = Process.Start(start) ?? throw new InvalidOperationException("Host launch failed.");
            output = Forward(app.StandardOutput); errors = Forward(app.StandardError);
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true) { app.Refresh(); if (app.HasExited) throw new InvalidOperationException("Host exited before readiness"); if (app.MainWindowHandle != 0) break; await Task.Delay(50, ready.Token); }
            Console.WriteLine($"HOST process={app.Id}; target=0x{hwnd:X} remains independently alive; grants reset on restart.");
        }
        async Task UiPhase(string phase)
        {
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-NoProfile", "-File", Path.Combine(repo.FullName, "Rug.UI.Tests", "CheckpointRestoreUiSmoke.ps1"),
                "-AppProcessId", app!.Id.ToString(), "-TargetHwnd", hwnd.ToString(), "-Phase", phase }) start.ArgumentList.Add(arg);
            using var probe = Process.Start(start)!; var stdout = probe.StandardOutput.ReadToEndAsync(); var stderr = probe.StandardError.ReadToEndAsync();
            try { await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50)); }
            catch { if (!probe.HasExited) { probe.Kill(true); await probe.WaitForExitAsync(); } throw; }
            Console.Write(await stdout); string error = await stderr;
            if (probe.ExitCode != 0) throw new InvalidOperationException("Checkpoint UI phase " + phase + " failed: " + error);
        }
        try
        {
            await OpenHost();
            if (automated)
            {
                await UiPhase("Save");
                if (target.KeyCounts != (1, 1) || target.AfterKeyCounts != (0, 0)) throw new InvalidOperationException("UI save action counts incorrect");
                await CloseHost();
                // A second host-owned record only exercises selector A/B; it is not another Lua/native-save claim.
                await using (var files = new FileCheckpointStore(Path.Combine(root, "records")))
                {
                    var record = (await files.QueryAsync(new PluginInstallation("checkpoint_probe", plugin))).Single().Record!;
                    await files.PublishAsync(record with { CheckpointId = Guid.NewGuid(), SourceInstanceId = Guid.NewGuid() }, () => true, default);
                    if ((await files.QueryAsync(new PluginInstallation("checkpoint_probe", plugin))).Count != 2) throw new InvalidOperationException("Selector regression requires two records");
                    Console.WriteLine("SELECTOR FIXTURE: two distinct host-owned checkpoint records; second record is selector-only evidence.");
                }
                await OpenHost(); await UiPhase("Restore");
                if (target.KeyCounts != (1, 1) || target.AfterKeyCounts != (1, 1)) throw new InvalidOperationException("UI restart replayed BEFORE or omitted AFTER");
                Console.WriteLine("Checkpoint WinUI PASS: two actual host processes, independent persistent target, fresh WGC red rejection/green on_restore, explicit consent/rebinding and B/C once. Human acceptance NOT RUN.");
                return;
            }
            Console.WriteLine("CHECKPOINT MANUAL: use English input; do not manually press B/C. Bind GREEN target in DevTools, select checkpoint_probe, approve and start bound task. Hands off during 5s script wait (detector stays active). Expect BEFORE B=(1,1), C=(0,0), checkpoint_saved.");
            Console.WriteLine("Then type restart here (closes/restarts only host, preserves target/files). Re-grant permissions and REBIND target. 查看检查点 -> select Ready -> 从此检查点恢复, hands off 5s. Expect C=(1,1), B unchanged; new instance ID/on_restore, no on_init replay.");
            Console.WriteLine("Rejection: restart, type red, rebind/re-grant/select/restore. Expect SceneRejected and no additional B/C. Type green for a valid scene. Host never automatically returns manual control.");
            Console.WriteLine("Commands: status, restart, red, green, done. Human/WGC acceptance is NOT RUN until performed and confirmed; counts are passive aggregates.");
            string? command;
            while ((command = await Console.In.ReadLineAsync()) is not null && command.Trim() != "done")
            {
                switch (command.Trim())
                {
                    case "restart": await OpenHost(); break;
                    case "red": target.SetReady(false); break;
                    case "green": target.SetReady(true); break;
                }
                Console.WriteLine($"TARGET BEFORE B={target.KeyCounts} AFTER C={target.AfterKeyCounts}; same target across host restarts, no inferred human verdict.");
            }
        }
        finally
        {
            await CloseHost();
            Console.WriteLine($"FINAL BEFORE B={target.KeyCounts} AFTER C={target.AfterKeyCounts}; user confirmation required.");
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory) + "checkpoint-manual-", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup.");
            Directory.Delete(root, true);
        }
    }
    private static async Task Forward(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
            if (line.Contains("checkpoint", StringComparison.OrdinalIgnoreCase) || line.Contains("faulted", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("CHECKPOINT LOG: " + line[..Math.Min(1200, line.Length)]);
    }
}
