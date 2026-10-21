using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;
using Scheduler=Rug.UI.Core.Services.TaskScheduler;

internal static class ObservedClickNative
{
    private sealed class NoAnomaly : IAnomalyLogger
    {public Task<string> LogAnomalyAsync(TaskExecutionContext c,string reason,string context)=>throw new NotSupportedException();}
    public static async Task RunAsync()
    {
        DirectoryInfo? repo=new(AppContext.BaseDirectory);
        while(repo is not null&&!File.Exists(Path.Combine(repo.FullName,"Rug.slnx")))repo=repo.Parent;
        string config=AppContext.BaseDirectory.Contains("Release",StringComparison.OrdinalIgnoreCase)?"Release":"Debug";
        var assembly=typeof(InputService).Assembly;
        nint module=NativeLibrary.Load(Path.Combine(repo!.FullName,"x64",config,"Rug.Core.dll"),assembly,DllImportSearchPath.UseDllDirectoryForDependencies);
        NativeLibrary.SetDllImportResolver(assembly,(name,_,_)=>name is "Rug.Core" or "Rug.Core.dll"?module:0);
        foreach(string mode in new[]{"stable","changed","resize"})await Case(mode);
        Console.WriteLine("Observed Native PASS: actual Lua -> Gateway -> arbiter -> fresh exact-window WGC/ROI -> native PostMessage; stable target down/up=(1,1), changed/resize=(0,0), all resources drained.");
        Console.WriteLine("SendInput and human takeover acceptance NOT RUN here; no Hooks/Raw source in fixture, existing acceptance/independent Hook gap unchanged.");
    }
    private static async Task Case(string mode)
    {
        using var target=new CaptureGeometryDiagnosticWindow();nint hwnd=await target.Ready;
        string root=Path.Combine(AppContext.BaseDirectory,"observed-native-"+Guid.NewGuid().ToString("N"));
        string directory=Path.Combine(root,"plugins","observed");Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory,"manifest.json"),JsonSerializer.Serialize(new
            {id="observed",name="Observed",version="1",targetProcess="test",backgroundInput=true,permissions=new[]{Permission.Capture,Permission.Timer,Permission.ControlInput}}));
            await File.WriteAllTextAsync(Path.Combine(directory,"main.lua"),"""
                function on_init()
                  local c=rug.capture();assert(c~=nil);local o=c.observation
                  rug.set_state('source_dpi',o.captureFacts.dpi)
                  rug.set_state('captured',true)
                  while not rug.get_state('continue') do rug.sleep(1) end
                  local json=string.format('{"frameReference":"%s","observationId":"%s","frameId":"%s","runGeneration":%d,"x":90,"y":80,"coordinateSpace":"client-physical-pixels","button":0,"mode":1,"policyId":"roi-consistency-v1"}',o.frameReference,o.observationId,o.frameId,o.run.runGeneration)
                  local result=rug.click_observed(json)
                  rug.set_state('status',result.status);rug.set_state('reason',result.reason)
                  rug.set_state('done',true)
                end
                function on_tick() rug.sleep(60000) end
                """);
            var grants=new PluginAuthorization();TestAuthorizationHost.ApproveCatalog(grants,Path.Combine(root,"plugins"));
            var state=new StateStore();using var bus=new CommandBus(state);var pool=new ObservationFramePool();
            await using(var scheduler=new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance),Path.Combine(root,"plugins"),
                ()=>new CaptureService(exactBoundWindow:true),()=>new InputService(),
                (c,i)=>new LuaRuntime(c,new FakeOcr(),i,NullLogger<LuaRuntime>.Instance,commandBus:bus,observationFrames:pool),
                NullLogger<Scheduler>.Instance,new NoAnomaly(),commandBus:bus,authorization:grants,inputArbiter:new InputArbiter()))
            {
                var id=await scheduler.StartTaskAsync("observed",hwnd);
                var run=scheduler.Instances.Single(c=>c.InstanceId==id);
                await RecoveryTests.Until(()=>state.GetState(run.InstanceScope,"captured",false)||run.IsBusinessClosed);
                RecoveryTests.Check(!run.IsBusinessClosed,"native capture failed: "+run.LastError);
                if(mode=="changed")target.ChangeMarker();if(mode=="resize")target.Resize(730,470);
                bus.SetState(run.InstanceScope,"continue",true);
                await RecoveryTests.Until(()=>state.GetState(run.InstanceScope,"done",false)||run.IsBusinessClosed);
                string status=state.GetState(run.InstanceScope,"status","");string reason=state.GetState(run.InstanceScope,"reason","");
                double sourceDpi=state.GetState(run.InstanceScope,"source_dpi",0d);
                RecoveryTests.Check(sourceDpi>0,"source DPI metadata missing");
                RecoveryTests.Check(!run.IsBusinessClosed,"native observed Lua fault: "+run.LastError);
                if(mode=="stable")await RecoveryTests.Until(()=>target.ClickCounts==(1,1)||status!="completed");
                RecoveryTests.Check(mode=="stable"?status=="completed"&&target.ClickCounts==(1,1):
                    status=="rejected"&&reason==(mode=="changed"?"SceneChanged":"GeometryChanged")&&target.ClickCounts==(0,0),
                    $"native {mode}: {status}/{reason} target={target.ClickCounts}");
                if(mode=="stable")RecoveryTests.Check(target.LastDown==(90,80)&&target.LastUp==(90,80),$"native observed client point expected (90,80), actual {target.LastDown}/{target.LastUp}");
                await scheduler.StopTaskAsync(id);
                Console.WriteLine($"Observed Native {mode}: {status}/{reason}; actual target down/up={target.ClickCounts}; last client down/up={target.LastDown}/{target.LastUp}; source DPI={sourceDpi}");
            }
            RecoveryTests.Check(pool.Usage==new ObservationUsage(0,0,0,0,0)&&pool.ActionUsage.ActiveInputs==0,"native observation leaked resources");
        }
        finally{Directory.Delete(root,true);}
    }
}
