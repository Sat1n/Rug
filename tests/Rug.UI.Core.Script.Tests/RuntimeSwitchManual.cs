using System.Diagnostics;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;

internal static partial class RuntimeUpdateUi
{
    public static async Task RunSwitchManualAsync()
    {
        DirectoryInfo? repo = new(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName,"Rug.UI","Rug.UI.csproj"))) repo = repo.Parent;
        if (repo is null) throw new DirectoryNotFoundException("Repository not found.");
        string root = Path.Combine(AppContext.BaseDirectory,"switch-manual-"+Guid.NewGuid().ToString("N"));
        string plugin = Path.Combine(root,"plugins","update_probe"); Directory.CreateDirectory(plugin);
        foreach (string file in new[]{"manifest.json","main.lua","ui.json"})
            File.Copy(Path.Combine(repo.FullName,"plugins","update_probe",file),Path.Combine(plugin,file));
        using var target = new RecoveryDiagnosticWindow(); var hwnd = await target.Ready; target.SetReady(true);
        string appPath = Path.Combine(repo.FullName,"Rug.UI","bin","x64","Debug","net10.0-windows10.0.22621.0","win-x64","Rug.UI.exe");
        var start = new ProcessStartInfo(appPath) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.Environment["RUG_PLUGINS_ROOT"] = Path.Combine(root,"plugins");
        start.Environment["RUG_CHECKPOINT_ROOT"] = Path.Combine(root,"records");
        start.Environment["Logging__LogLevel__Rug.UI.Core.Services.LuaRuntime"] = "Information";
        using var app = Process.Start(start) ?? throw new InvalidOperationException("WinUI launch failed.");
        async Task Forward(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
                if (line.Contains("[update-probe]") || line.Contains("[update-v2]") || line.Contains("faulted") || line.Contains("failed"))
                    Console.WriteLine("PROBE LOG: "+line[..Math.Min(1200,line.Length)]);
        }
        var output=Forward(app.StandardOutput); var errors=Forward(app.StandardError);
        Console.WriteLine($"SWITCH MANUAL READY: target=0x{hwnd:X} UIprocess={app.Id}; isolated sample, fresh WGC, no synthetic human acceptance.");
        Console.WriteLine("1. Bind the green target in DevTools, select Update freeze probe, Start bound task, explicitly grant permissions.");
        Console.WriteLine("2. Here type update once to install the fixed embedded v2 candidate; in UI Prepare runtime update -> Frozen (not switched).");
        Console.WriteLine("3. Optional: Pause instance while Frozen. Switch runtime update -> explicit confirmation. Old STOP once; new identity/generation; on_init never reruns.");
        Console.WriteLine("4. Inherited pause: Committed stays pending and no [update-v2] AFTER until explicit Resume. Then exactly one AFTER-only; old frozen tail never runs.");
        Console.WriteLine("5. Separate fresh session for failure: green -> Freeze -> here red -> Switch. Expect SceneRejected, no v2 AFTER; here green immediately, old run may continue. Stop it normally.");
        Console.WriteLine("Commands: update (once), red/green (controlled scene), done (close only owned UI/target/temp files). No automatic return-control or permission approval.");
        Console.WriteLine("Human confirmation is NOT RUN until you perform and report these steps; existing input/Hook acceptance is unchanged.");
        bool updated=false;
        try
        {
            while (Console.ReadLine() is { } command && command.Trim()!="done")
            {
                switch (command.Trim())
                {
                    case "update" when !updated:
                        await RuntimeUpdateSamples.WriteTargetAsync(plugin); updated=true;
                        Console.WriteLine("Fixed v2 files installed; wait for pending status, then explicitly Prepare/Freeze/Switch in UI."); break;
                    case "red": target.SetReady(false); Console.WriteLine("Target red; fresh-scene continuation must reject."); break;
                    case "green": target.SetReady(true); Console.WriteLine("Target green; no automatic restart/continue requested."); break;
                    default: Console.WriteLine("Use update once, red, green or done."); break;
                }
            }
        }
        finally
        {
            if (!app.HasExited) { app.CloseMainWindow(); try { await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { app.Kill(true); await app.WaitForExitAsync(); } }
            await Task.WhenAll(output,errors);
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory)+"switch-manual-",StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup.");
            Directory.Delete(root,true);
        }
    }
}
