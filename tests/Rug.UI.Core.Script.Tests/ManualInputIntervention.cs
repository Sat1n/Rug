using System.Diagnostics;
using System.Text.Json;
using Rug.UI.Core.Native;

/// <summary>Opt-in real WinUI fixture. No synthesized human input, grants or Hook experiment.</summary>
internal static class ManualInputIntervention
{
    /// <summary>@shape fixture: isolated temporary plugin root, owned target/UI processes, aggregate target counters.
    /// @source policy: ../../Rug.UI.Core/InputIntervention.md</summary>
    public static async Task RunAsync()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Rug.UI", "Rug.UI.csproj"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Workspace not found");
        string appPath = Path.Combine(root.FullName, "Rug.UI", "bin", "x64", "Debug", "net10.0-windows10.0.22621.0", "win-x64", "Rug.UI.exe");
        if (!File.Exists(appPath)) throw new FileNotFoundException("Build Debug/x64 WinUI first", appPath);
        string plugins = Path.Combine(AppContext.BaseDirectory, "manual-intervention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plugins);
        Process? app = null;
        RawInputDiagnosticWindow? target = null;
        try
        {
            foreach (string id in new[] { "input_probe", "timer_probe", "vision_probe" })
            {
                string dir = Path.Combine(plugins, id); Directory.CreateDirectory(dir);
                string[] permissions = id == "input_probe" ? ["input", "timer"] : id == "vision_probe" ? ["vision.capture", "timer"] : ["timer"];
                await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new
                { id, name = id, version = "1", entry = "main.lua", targetProcess = "isolated-probe", permissions, inputDelivery = "Win32SendInput" }));
                await File.WriteAllTextAsync(Path.Combine(dir, "ui.json"), """
                    {"type":"Page","id":"root","children":[
                    {"type":"ButtonCard","id":"start","title":"启动无窗口计时任务","command":"host.start_windowless"},
                    {"type":"ButtonCard","id":"stop","title":"停止当前实例","command":"host.stop_selected"},
                    {"type":"InfoBanner","id":"help","title":"人工接管验证","description":"请通过顶部运行实例选择器检查任务是否仍在运行。"}]}
                    """);
                string script = id == "input_probe"
                    ? "function on_tick() rug.set_state('probe.ticks', (rug.get_state('probe.ticks') or 0)+1); rug.press_key(66,80); rug.sleep(1500) end"
                    : id == "vision_probe"
                        ? "function on_tick() rug.capture(); rug.set_state('probe.ticks', (rug.get_state('probe.ticks') or 0)+1); rug.sleep(250) end"
                        : "function on_tick() rug.set_state('probe.ticks', (rug.get_state('probe.ticks') or 0)+1); rug.sleep(250) end";
                await File.WriteAllTextAsync(Path.Combine(dir, "main.lua"), script);
                await new Rug.UI.Core.Schema.SchemaParser().ParseFileAsync(Path.Combine(dir, "ui.json"));
            }
            target = new RawInputDiagnosticWindow(installKeyboard: false, installMouse: false);
            nint hwnd = await target.Ready.Task;
            var start = new ProcessStartInfo(appPath) { UseShellExecute = false, CreateNoWindow = true };
            start.Environment["RUG_PLUGINS_ROOT"] = plugins;
            app = Process.Start(start) ?? throw new InvalidOperationException("WinUI launch failed");
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
            Console.WriteLine($"MANUAL READY: isolatedTarget=0x{hwnd:X} UIprocess={app.Id}; no Hooks/Raw source in this fixture, no synthesized human input.");
            Console.WriteLine("Use WinUI DevTools crosshair to bind the white isolated window; start timer, optional bound vision, then bound input. Approve permissions explicitly.");
            Console.WriteLine("Hands off while Rug sends B; human keyboard/mouse in any window must cause takeover. Check cleanup, timer/vision preservation, explicit return and no old-run revival.");
            Console.WriteLine("Use status for aggregate target B down/up only; done closes this fixture's UI/target and removes its temporary plugins. Human acceptance is NOT RUN until you perform/confirm it.");
            while (Console.ReadLine() is { } line && line.Trim() != "done")
                if (line.Trim() == "status") Console.WriteLine($"TARGET aggregate B down/up={target.TargetKeyCounts(66)}; no human/source verdict inferred");
        }
        finally
        {
            if (app is not null)
            {
                if (!app.HasExited)
                {
                    app.CloseMainWindow();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await app.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) { app.Kill(entireProcessTree: true); await app.WaitForExitAsync(); }
                }
                app.Dispose();
            }
            target?.Dispose();
            if (!Path.GetFullPath(plugins).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary plugin directory escaped test output.");
            Directory.Delete(plugins, recursive: true);
        }
    }
}
