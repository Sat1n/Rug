using System.Diagnostics;
using System.Text.Json;
using Rug.UI.Core.Native;

/// <summary>Opt-in real WinUI fixture. No synthesized human input, grants or Hook experiment.</summary>
internal static class ManualInputIntervention
{
    internal static string CreateScript(string pluginId) => pluginId switch
    {
        "input_probe" => """
            local attempt = 0
            function on_init()
              rug.log('[intervention-probe] waiting-before-first-B: 5000ms; detection remains active')
              rug.sleep(5000)
            end
            function on_tick()
              attempt = attempt + 1
              rug.set_state('probe.ticks', attempt)
              rug.log('[intervention-probe] issuing-B attempt=' .. attempt)
              rug.press_key(66,80)
              rug.log('[intervention-probe] B-call-completed attempt=' .. attempt .. '; waiting 5000ms')
              rug.sleep(5000)
            end
            function on_stop() rug.log('[intervention-probe] on_stop') end
            """,
        "vision_probe" => "function on_tick() rug.capture(); rug.set_state('probe.ticks', (rug.get_state('probe.ticks') or 0)+1); rug.sleep(250) end",
        _ => "function on_tick() rug.set_state('probe.ticks', (rug.get_state('probe.ticks') or 0)+1); rug.sleep(250) end"
    };

    private static async Task ForwardEvidenceAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
            if (line.Contains("[intervention-probe]", StringComparison.Ordinal) ||
                line.Contains("Automation instance", StringComparison.Ordinal))
                Console.WriteLine("PROBE LOG: " + line[..Math.Min(line.Length, 1000)]);
    }

    private static async Task ObserveTargetAsync(RawInputDiagnosticWindow target, CancellationToken token)
    {
        (long Down, long Up)? previous = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var counts = target.TargetKeyCounts(66);
            if (counts != previous)
            {
                Console.WriteLine($"TARGET observed B down/up={counts}; passive aggregate, no human/source verdict inferred");
                previous = counts;
            }
            await Task.Delay(250, token);
        }
    }

    internal static string CreateSchema(string pluginId)
    {
        object start = pluginId == "timer_probe"
            ? new { type = "ButtonCard", id = "start", title = "启动无窗口计时任务", command = "host.start_windowless" }
            : (object)new { type = "InfoBanner", id = "start", title = "需要绑定目标窗口",
                description = "在开发工具中用准星绑定白色隔离窗口，然后点击底部“运行绑定窗口任务”。此插件不能无窗口启动。" };
        return JsonSerializer.Serialize(new { type = "Page", id = "root", children = new object[]
        {
            start,
            new { type = "ButtonCard", id = "stop", title = "停止当前实例", command = "host.stop_selected" },
            new { type = "InfoBanner", id = "help", title = "人工接管验证",
                description = "请通过底部当前实例选择器检查任务是否仍在运行。" }
        }});
    }

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
        using var evidenceStop = new CancellationTokenSource();
        Task output = Task.CompletedTask, errors = Task.CompletedTask, counters = Task.CompletedTask;
        try
        {
            foreach (string id in new[] { "input_probe", "timer_probe", "vision_probe" })
            {
                string dir = Path.Combine(plugins, id); Directory.CreateDirectory(dir);
                string[] permissions = id == "input_probe" ? ["input", "timer"] : id == "vision_probe" ? ["vision.capture", "timer"] : ["timer"];
                await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new
                { id, name = id, version = "1", entry = "main.lua", targetProcess = "isolated-probe", permissions, inputDelivery = "Win32SendInput" }));
                await File.WriteAllTextAsync(Path.Combine(dir, "ui.json"), CreateSchema(id));
                await File.WriteAllTextAsync(Path.Combine(dir, "main.lua"), CreateScript(id));
                await new Rug.UI.Core.Schema.SchemaParser().ParseFileAsync(Path.Combine(dir, "ui.json"));
            }
            target = new RawInputDiagnosticWindow(installKeyboard: false, installMouse: false);
            nint hwnd = await target.Ready.Task;
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
            Console.WriteLine($"MANUAL READY: isolatedTarget=0x{hwnd:X} UIprocess={app.Id}; no Hooks/Raw source in this fixture, no synthesized human input.");
            Console.WriteLine("Use WinUI DevTools crosshair to bind the white isolated window; start timer, optional bound vision, then bound input. Approve permissions explicitly.");
            Console.WriteLine("Only timer_probe has windowless Start. For vision_probe/input_probe use the bottom host button 运行绑定窗口任务 after binding the target.");
            Console.WriteLine("Input probe waits 5s before first B, then 5s after each B. Detection stays active throughout; these are script waits, not input-ignore windows.");
            Console.WriteLine("Observe PROBE LOG phases and passive TARGET B counters without typing. Completed API calls alone do not prove target delivery; fault lines remain visible.");
            Console.WriteLine("Hands off while Rug sends B; human keyboard/mouse in any window must cause takeover. Check cleanup, timer/vision preservation, explicit return and no old-run revival.");
            Console.WriteLine("Use status for aggregate target B down/up only; done closes this fixture's UI/target and removes its temporary plugins. Human acceptance is NOT RUN until you perform/confirm it.");
            counters = ObserveTargetAsync(target, evidenceStop.Token);
            while (Console.ReadLine() is { } line && line.Trim() != "done")
                if (line.Trim() == "status") Console.WriteLine($"TARGET aggregate B down/up={target.TargetKeyCounts(66)}; no human/source verdict inferred");
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
            if (target is not null) Console.WriteLine($"TARGET final B down/up={target.TargetKeyCounts(66)}; no human/source verdict inferred");
            target?.Dispose();
            if (!Path.GetFullPath(plugins).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary plugin directory escaped test output.");
            Directory.Delete(plugins, recursive: true);
        }
    }
}
