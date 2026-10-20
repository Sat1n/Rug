using System.Collections.Concurrent;
using System.Diagnostics;
using Rug.UI.Core.Native;

/// <summary>Real WinUI + WGC, no synthetic human acceptance or automatic permission grant.</summary>
internal static class RuntimeUpdateUi
{
    public static async Task RunAsync()
    {
        DirectoryInfo? repo = new(AppContext.BaseDirectory);
        while(repo is not null && !File.Exists(Path.Combine(repo.FullName,"Rug.UI","Rug.UI.csproj"))) repo=repo.Parent;
        if(repo is null) throw new DirectoryNotFoundException("Repository not found.");
        string root=Path.Combine(AppContext.BaseDirectory,"update-ui-"+Guid.NewGuid().ToString("N"));
        string plugin=Path.Combine(root,"plugins","update_probe");Directory.CreateDirectory(plugin);
        foreach(string file in new[]{"manifest.json","main.lua","ui.json"}) File.Copy(Path.Combine(repo.FullName,"plugins","update_probe",file),Path.Combine(plugin,file));
        using var target=new RecoveryDiagnosticWindow();var hwnd=await target.Ready;target.SetReady(true);
        var appPath=Path.Combine(repo.FullName,"Rug.UI","bin","x64","Debug","net10.0-windows10.0.22621.0","win-x64","Rug.UI.exe");
        var start=new ProcessStartInfo(appPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.Environment["RUG_PLUGINS_ROOT"]=Path.Combine(root,"plugins");start.Environment["RUG_CHECKPOINT_ROOT"]=Path.Combine(root,"records");
        start.Environment["Logging__LogLevel__Rug.UI.Core.Services.LuaRuntime"]="Information";
        var logs=new ConcurrentQueue<string>();
        var diagnostics=new ConcurrentQueue<string>();
        using var app=Process.Start(start)??throw new InvalidOperationException("UI launch failed.");
        async Task Forward(StreamReader reader)
        {while(await reader.ReadLineAsync() is {} line) {
            diagnostics.Enqueue(line); while(diagnostics.Count>80) diagnostics.TryDequeue(out _);
            if(line.Contains("[update-probe]") || line.Contains("faulted")) { logs.Enqueue(line); Console.WriteLine(line[..Math.Min(1000,line.Length)]); }
        }}
        var output=Forward(app.StandardOutput);var errors=Forward(app.StandardError);
        try
        {
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (true)
            {
                app.Refresh(); if (app.HasExited) throw new InvalidOperationException("WinUI exited before readiness.");
                if (app.MainWindowHandle != 0) break;
                await Task.Delay(25, ready.Token);
            }
            var ps=new ProcessStartInfo("pwsh"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"-NoProfile","-File",Path.Combine(repo.FullName,"Rug.UI.Tests","CheckpointRestoreUiSmoke.ps1"),"-AppProcessId",app.Id.ToString(),"-TargetHwnd",hwnd.ToString(),"-Phase","Update","-PluginDirectory",plugin}) ps.ArgumentList.Add(arg);
            using var probe=Process.Start(ps)!;var stdout=probe.StandardOutput.ReadToEndAsync();var stderr=probe.StandardError.ReadToEndAsync();
            try {await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));}
            catch {if(!probe.HasExited)probe.Kill(true);throw;}
            Console.Write(await stdout);if(probe.ExitCode!=0)throw new InvalidOperationException(await stderr);
        }
        catch
        {
            app.Refresh();Console.WriteLine("UPDATE UI DIAGNOSTICS: exited="+app.HasExited+(app.HasExited?" code="+app.ExitCode:""));
            foreach(var line in diagnostics)Console.WriteLine(line);
            throw;
        }
        finally
        {
            if(!app.HasExited){app.CloseMainWindow();try{await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}catch(TimeoutException){app.Kill(true);await app.WaitForExitAsync();}}
            await Task.WhenAll(output,errors);
            if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory)+"update-ui-",StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe fixture cleanup.");
            Directory.Delete(root,true);
        }
        int Count(string token)=>logs.Count(s=>s.Contains(token));
        if(Count("[update-probe] INIT")!=1 || Count("[update-probe] STOP")!=1 ||
            Count("[update-probe] BEFORE")!=Count("[update-probe] AFTER")+1 || logs.Any(s=>s.Contains("faulted")))
            throw new InvalidOperationException("Unexpected real Lua sequence: "+string.Join("\n",logs));
        Console.WriteLine("Runtime update actual WinUI/WGC PASS: one INIT/STOP; cancelled frozen checkpoint returns once; Stop does not release AFTER; human/input detection unchanged.");
    }
}
