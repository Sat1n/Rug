using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Rug.UI.Core.Native;

internal static class TraceReviewUi
{
    public static async Task RunAsync()
    {
        DirectoryInfo? repo=new(AppContext.BaseDirectory);while(repo is not null&&!File.Exists(Path.Combine(repo.FullName,"Rug.slnx")))repo=repo.Parent;
        string root=Path.Combine(AppContext.BaseDirectory,"trace-review-ui-"+Guid.NewGuid().ToString("N"));
        string plugin=Path.Combine(root,"plugins","trace_review_probe");Directory.CreateDirectory(plugin);Directory.CreateDirectory(Path.Combine(root,"incidents"));
        await File.WriteAllTextAsync(Path.Combine(plugin,"manifest.json"),JsonSerializer.Serialize(new{id="trace_review_probe",name="Trace review probe",version="1",targetProcess="test",permissions=new[]{"vision.capture","timer","agent"}}));
        await File.WriteAllTextAsync(Path.Combine(plugin,"main.lua"),"""
            function on_init()
              rug.log('[trace-review] started')
              local c=rug.capture();assert(c~=nil)
              rug.agent.resolve_anomaly('controlled-review','actual UI incident archive')
              error('manual-only recovery must not continue in viewer test')
            end
            function on_tick() rug.sleep(60000) end
            function on_stop() rug.log('[trace-review] stopped') end
            """);
        await File.WriteAllTextAsync(Path.Combine(plugin,"ui.json"),"""{"type":"Page","id":"review","children":[{"type":"InfoBanner","id":"info","title":"Trace review probe","message":"Read-only viewer test; no business input."}]}""");
        using var target=new RecoveryDiagnosticWindow();nint hwnd=await target.Ready;target.SetReady(true);
        var exitFailures=new List<string>();
        string config=AppContext.BaseDirectory.Contains("Release",StringComparison.OrdinalIgnoreCase)?"Release":"Debug";
        string executable=Path.Combine(repo!.FullName,"Rug.UI","bin","x64",config,"net10.0-windows10.0.22621.0","win-x64","Rug.UI.exe");
        try
        {
            foreach(string phase in new[]{"Baseline","Live","History"})
            {
                var logs=new ConcurrentQueue<string>();
                var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                start.Environment["RUG_PLUGINS_ROOT"]=Path.Combine(root,"plugins");start.Environment["RUG_ANOMALY_ROOT"]=Path.Combine(root,"incidents");
                start.Environment["RUG_TRACE_ROOT"]=Path.Combine(root,"traces");start.Environment["RUG_CHECKPOINT_ROOT"]=Path.Combine(root,"checkpoints");
                start.Environment["Logging__LogLevel__Rug.UI.Core.Services.LuaRuntime"]="Information";
                using var app=Process.Start(start)!;
                async Task Forward(StreamReader reader){while(await reader.ReadLineAsync() is {} line){logs.Enqueue(line);while(logs.Count>150)logs.TryDequeue(out _);}}
                var stdout=Forward(app.StandardOutput);var stderr=Forward(app.StandardError);
                try
                {
                    using var ready=new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    while(true)
                    {
                        app.Refresh();if(app.HasExited)throw new Exception("Viewer host exited");if(app.MainWindowHandle!=0)break;
                        await Task.Delay(40,ready.Token);
                    }
                    var probe=new ProcessStartInfo("pwsh"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                    foreach(string arg in new[]{"-NoProfile","-File",Path.Combine(repo.FullName,"Rug.UI.Tests","TraceReviewUiSmoke.ps1"),"-AppProcessId",app.Id.ToString(),"-TargetHwnd",hwnd.ToString(),"-Phase",phase,"-IncidentRoot",Path.Combine(root,"incidents")})probe.ArgumentList.Add(arg);
                    using var ps=Process.Start(probe)!;var output=ps.StandardOutput.ReadToEndAsync();var errors=ps.StandardError.ReadToEndAsync();
                    try{await ps.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(80));}catch{if(!ps.HasExited)ps.Kill(true);throw;}
                    Console.Write(await output);if(ps.ExitCode!=0)throw new Exception(await errors);
                }
                catch{app.Refresh();Console.WriteLine($"Trace UI host exited={app.HasExited}"+(app.HasExited?" code="+app.ExitCode:""));foreach(string line in logs)Console.WriteLine(line);throw;}
                finally
                {
                    if(!app.HasExited){app.CloseMainWindow();try{await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));}catch{app.Kill(true);await app.WaitForExitAsync();}}
                    await Task.WhenAll(stdout,stderr);
                }
                if(app.ExitCode!=0){exitFailures.Add(phase+" exit="+app.ExitCode);Console.WriteLine("UI SHUTDOWN FAILED "+phase+": "+string.Join('\n',logs.Where(l=>l.Contains("Assert failure")||l.Contains("ceemain"))));}
                int starts=logs.Count(l=>l.Contains("[trace-review] started"));
                if(starts!=(phase=="Live"?1:0))throw new Exception("History viewer started/resurrected a run");
            }
            if(exitFailures.Count!=0)throw new Exception("Viewer interactions completed; shutdown acceptance failed: "+string.Join("; ",exitFailures));
            Console.WriteLine("Trace review actual WinUI/WGC PASS: active and stopped trace, actual incident PNG/JSON, boundary jump, host restart history without run/grant revival; no human/SendInput acceptance");
        }
        finally
        {
            if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory)+"trace-review-ui-",StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe test cleanup");
            Directory.Delete(root,true);
        }
    }
}
