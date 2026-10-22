using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;
using Scheduler=Rug.UI.Core.Services.TaskScheduler;

/// <summary>Trusted isolated sample host: production perception/input, test-only scheduling barriers.
/// @shape outputs: bounded per-scene reports and normal production traces; never pixels.
/// @source plugin: samples/controlled-visual/plugin/main.lua</summary>
internal static class ControlledVisualSample
{
    private const string Plugin="controlled_visual";
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private sealed record MatchProof(string Template,int Hits,long? Render100ns);
    private sealed class MatchBarrier : ITemplateMatchService
    {
        private readonly TemplateMatchService _real=new();
        internal readonly TaskCompletionSource Located=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<MatchProof> Proofs=new();
        internal NativeFrameTime? SourceTime;
        public Task<IReadOnlyList<TemplateMatchResult>> MatchAsync(string target,string template,float threshold=.8f,CancellationToken cancellationToken=default)
            =>_real.MatchAsync(target,template,threshold,cancellationToken);
        /// <summary>@shape frame: actual held BGRA; result: unmodified native matches, proofs <=128.
        /// @source perception: Rug.UI.Core/Services/TemplateMatchService.cs#class:TemplateMatchService</summary>
        public async Task<IReadOnlyList<TemplateMatchResult>> MatchFrameAsync(CapturedFrame frame,string template,float threshold=.8f,CancellationToken cancellationToken=default)
        {
            var hits=await _real.MatchFrameAsync(frame,template,threshold,cancellationToken);
            if(Proofs.Count>=128)throw new InvalidOperationException("Sample proof capacity exceeded");
            string name=Path.GetFileName(template);Proofs.Enqueue(new(name,hits.Count,frame.CaptureFacts?.NativeTime?.SystemRelative100ns));
            if(name=="button.bmp")
            {
                SourceTime=frame.CaptureFacts?.NativeTime;
                Located.TrySetResult();
                // Deliberately non-cooperative logical barrier: Stop must drain the real task/lease.
                await Release.Task;
            }
            return hits;
        }
    }
    public static async Task RunAsync(string output)
    {
        var repo=Repo();string config=AppContext.BaseDirectory.Contains("Release",StringComparison.OrdinalIgnoreCase)?"Release":"Debug";
        string root=Path.Combine(Path.GetFullPath(output),"sample-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var assembly=typeof(InputService).Assembly;
        nint module=NativeLibrary.Load(Path.Combine(repo,"x64",config,"Rug.Core.dll"),assembly,DllImportSearchPath.UseDllDirectoryForDependencies);
        NativeLibrary.SetDllImportResolver(assembly,(name,_,_)=>name is "Rug.Core" or "Rug.Core.dll"?module:0);
        Console.WriteLine("CONTROLLED SAMPLE: explicit trusted test-host grants; exact-window WGC, real OpenCV and PostMessage only. Output="+root);
        var reports=new List<object>();bool functional=true;
        foreach(string scene in new[]{"stable","changed","resize","stale","invalidated","stop","revoked","no-completion"})
        {
            try{reports.Add(await Scene(repo,root,scene,reports.Count));}
            catch(Exception error){functional=false;string detail=error.ToString();reports.Add(new{scene,passed=false,error=detail[..Math.Min(2048,detail.Length)]});Console.WriteLine($"SCENE {scene} FAILED: {error.Message}");}
        }
        string work=Path.GetFullPath(Path.Combine(root,"work"));
        if(!work.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe sample cleanup");
        if(Directory.Exists(work))Directory.Delete(work,true); // Only this invocation's copied installations.
        // Read-only viewer uses the same completed files, with an empty installation root.
        var stable=JsonSerializer.SerializeToElement(reports[0]);
        var viewer=await ViewAsync(repo,root,config,stable.TryGetProperty("traceId",out var stableId)?stableId.GetGuid():Guid.Empty);
        await File.WriteAllTextAsync(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new
        {schemaVersion=1,configuration=config,functionalPassed=functional,scenes=reports,viewer,sendInput="NOT RUN",humanTakeover="NOT RUN"},Json));
        Console.WriteLine("REPORT "+Path.Combine(root,"report.json"));
        if(!functional||!viewer.InteractionsPassed||viewer.ExitCode!=0)
            throw new InvalidOperationException("Sample acceptance incomplete: see independent functional/viewer/shutdown results. Abnormal UI exit is retained as failure.");
    }
    /// <summary>@shape scene: one of eight fixed cases; export: <=2048 checked events plus bounded IDs/counts.
    /// @source business: samples/controlled-visual/plugin/main.lua#function:on_init</summary>
    private static async Task<object> Scene(string repo,string root,string scene,int ordinal)
    {
        string scratch=Path.Combine(root,"work",scene),install=Path.Combine(scratch,"plugins",Plugin);Directory.CreateDirectory(install);
        string source=Path.Combine(repo,"samples","controlled-visual","plugin");
        foreach(string file in Directory.GetFiles(source))File.Copy(file,Path.Combine(install,Path.GetFileName(file)));
        int x=145+ordinal*11,y=125+ordinal*7;
        using var target=new CaptureGeometryDiagnosticWindow(install,x,y,completeOnClick:scene!="no-completion");nint hwnd=await target.Ready;
        var matcher=new MatchBarrier();var pool=new ObservationFramePool();var arbiter=new InputArbiter();
        var authorization=new PluginAuthorization();TestAuthorizationHost.ApproveCatalog(authorization,Path.Combine(scratch,"plugins"));
        var state=new StateStore();using var bus=new CommandBus(state);
        var captures=new List<CaptureService>();
        await using var traces=new ExecutionTraceRecorder(Path.Combine(root,"traces"),new(FlushMilliseconds:2000));
        Guid traceId=Guid.Empty,instance=Guid.Empty;long generation=0;Guid? binding=null;
        string api="NOT RUN",reason="NOT RUN",business="NOT RUN";Guid? sourceFrame=null,confirmationFrame=null;
        await using(var host=new Scheduler(new PluginManager(NullLogger<PluginManager>.Instance),Path.Combine(scratch,"plugins"),
            ()=>{var capture=new CaptureService(exactBoundWindow:true);captures.Add(capture);return capture;},()=>new InputService(),
            (c,i)=>new LuaRuntime(c,new OcrService(),i,NullLogger<LuaRuntime>.Instance,templateMatcher:matcher,commandBus:bus,observationFrames:pool),
            NullLogger<Scheduler>.Instance,new AnomalyLogger(NullLogger<AnomalyLogger>.Instance,Path.Combine(scratch,"incidents")),
            commandBus:bus,authorization:authorization,inputArbiter:arbiter,traces:traces))
        {
            try
            {
                instance=await host.StartTaskAsync(Plugin,hwnd);var run=host.Instances.Single(r=>r.InstanceId==instance);
                traceId=run.Trace!.TraceId;generation=run.RunGeneration;binding=run.TargetBindingId;
                await matcher.Located.Task.WaitAsync(TimeSpan.FromSeconds(12));
                Check(matcher.Proofs.Single().Hits>0,"real button template not found");
                if(scene=="changed")target.ChangeMarker();
                if(scene=="resize")target.Resize(760,510);
                if(scene=="invalidated"){using var input=pool.BeginExternalInput();}
                if(scene=="stale")
                {
                    var native=matcher.SourceTime??throw new Exception("Native render time missing");var environment=new ObservedInputEnvironment();
                    await Until(()=>{var q=environment.ReadQpc();return native.AgeAt(q.Ticks,q.Frequency,"Windows boot QPC")>TimeSpan.FromMilliseconds(1050);});
                }
                Task? stopping=null;
                if(scene=="stop")stopping=host.StopTaskAsync(instance);
                if(scene=="revoked")authorization.Revoke(new(Plugin,install),[Permission.ControlInput]);
                if(scene is "stop" or "revoked")
                {
                    await Until(()=>run.IsBusinessClosed);
                    stopping??=host.StopTaskAsync(instance);
                    Check(!stopping.IsCompleted&&pool.Usage.Operations>0,"Stop disposed held recognition before actual task finished");
                }
                matcher.Release.TrySetResult();
                if(stopping is not null)await stopping.WaitAsync(TimeSpan.FromSeconds(12));
                else
                {
                    await Until(()=>state.GetState(run.InstanceScope,"done",false)||run.IsBusinessClosed);
                    Check(!run.IsBusinessClosed,"sample Lua fault: "+run.LastError);
                    api=state.GetState(run.InstanceScope,"input_status","");reason=state.GetState(run.InstanceScope,"input_reason","");business=state.GetState(run.InstanceScope,"business","");
                    sourceFrame=Guid.Parse(state.GetState(run.InstanceScope,"source_frame",""));
                    string confirm=state.GetState(run.InstanceScope,"confirmation_frame","");if(confirm.Length>0)confirmationFrame=Guid.Parse(confirm);
                    await host.StopTaskAsync(instance);
                }
                await run.DrainOperationsAsync().WaitAsync(TimeSpan.FromSeconds(8));
                Check(host.Instances.Count==0,"instance retained after Stop");
            }
            finally{matcher.Release.TrySetResult();}
        }
        Check(pool.Usage==new ObservationUsage(0,0,0,0,0)&&pool.ActionUsage.ActiveInputs==0&&captures.All(c=>!c.IsCapturing)&&arbiter.Snapshot.Active==0&&arbiter.Snapshot.Waiting==0&&arbiter.Snapshot.State==InputControlState.AutomationAvailable,"resources did not drain");
        var store=new TraceFileStore(Path.Combine(root,"traces"));var events=new List<ExecutionTraceEvent>();TracePage page;long after=0;
        do{page=store.Read(traces.HostSessionId,traceId,afterSequence:after,limit:256);events.AddRange(page.Events);after=page.NextSequence??0;
            Check(events.Count<=2048,"sample event export capacity exceeded");}while(page.NextSequence is not null);
        Check(page.Status==TraceReadStatus.Valid&&page.Summary is {RunCleanupCompleted:true,CleanupSucceeded:true,ActualWorkOutstanding:0},"trace/cleanup incomplete: "+page.Status);
        var click=events.Where(e=>e.Operation=="ClickObserved").ToArray();
        if(scene is "stable" or "no-completion")
        {
            Check(api=="completed"&&target.ClickCounts==(1,1)&&target.LastDown==(x+16,y+16)&&target.LastUp==target.LastDown,"input API/actual receiver disagreement");
            if(scene=="stable")
            {
                Check(business=="confirmed"&&confirmationFrame is not null&&confirmationFrame!=sourceFrame,"no new visual business confirmation");
                Check(matcher.Proofs.Any(p=>p.Template=="complete.bmp"&&p.Hits>0),"completion was not real template matching");
                Check(events.Any(e=>e.Reason=="template-match:result"&&e.Status=="Matched"&&e.Observation?.FrameId==confirmationFrame),"new-frame confirmation absent from structured trace");
            }
            else Check(business=="unconfirmed_timeout"&&confirmationFrame is null&&
                matcher.Proofs.Where(p=>p.Template=="complete.bmp").All(p=>p.Hits==0)&&
                events.Any(e=>e.Reason=="template-match:result"&&e.Status=="NoMatch"&&e.Observation?.FrameId!=sourceFrame),"API completion inferred business success");
            Check(click.Any(e=>e.Kind==TraceEventKind.Validation&&e.Reason?.EndsWith(":roi-passed")==true)&&click.Any(e=>e.Kind==TraceEventKind.InputSideEffect),"ROI/input execution evidence absent");
        }
        else
        {
            Check(target.ClickCounts==(0,0)&&!matcher.Proofs.Any(p=>p.Template=="complete.bmp"),"rejected/stopped run clicked, retried or claimed confirmation");
            if(scene is not ("stop" or "revoked"))Check(api=="rejected"&&reason==(scene switch{"changed"=>"SceneChanged","resize"=>"GeometryChanged","stale"=>"Stale",_=>"InvalidReference"})&&business=="not_confirmed","wrong rejection branch: "+api+"/"+reason);
        }
        Check(click.Count(e=>e.Kind==TraceEventKind.WorkStarted)==(scene is "stop" or "revoked"?0:1),"sample click execution count differs from expected admission");
        var outcome=click.SingleOrDefault(e=>e.Kind==TraceEventKind.CallerReturned);
        var located=events.FirstOrDefault(e=>e.Reason=="template-match:result"&&e.Status=="Matched")??
            events.FirstOrDefault(e=>e.Reason=="template input leased");
        if(sourceFrame is not null)
        {
            Check(located?.Observation?.FrameId==sourceFrame,"recognition source association mismatch");
            if(reason!="InvalidReference")Check(outcome?.Observation?.ObservationId==located?.Observation?.ObservationId,"resolved click source association mismatch");
        }
        Console.WriteLine($"SCENE {scene}: API={api}/{reason}; target={target.ClickCounts}; business={business}; trace=Valid {traces.HostSessionId}/{traceId}; instance={instance} g{generation}; resources=drained");
        return new{scene,passed=true,hostSessionId=traces.HostSessionId,traceId,instanceId=instance,runGeneration=generation,targetBindingId=binding,
            inputApi=api,reason,businessConfirmation=business,targetDown=target.ClickCounts.Down,targetUp=target.ClickCounts.Up,
            lastDown=target.ClickCounts.Down==0?(PointInt?)null:new PointInt(target.LastDown.X,target.LastDown.Y),
            lastUp=target.ClickCounts.Up==0?(PointInt?)null:new PointInt(target.LastUp.X,target.LastUp.Y),
            sourceObservationId=located?.Observation?.ObservationId,sourceFrameId=located?.Observation?.FrameId,sourceEvidence=located?.Reason,
            confirmationFrameId=confirmationFrame,confirmationObservationId=events.FirstOrDefault(e=>confirmationFrame is not null&&e.Reason=="template-match:result"&&e.Observation?.FrameId==confirmationFrame)?.Observation?.ObservationId,
            resolvedClickObservationId=outcome?.Observation?.ObservationId,
            executionId=outcome?.ExecutionId,operationId=outcome?.OperationId,outcomeUncertain=outcome?.OutcomeUncertain,validationFrames=outcome?.Observation?.ValidationFrames,
            traceIntegrity=page.Status.ToString(),actualWorkOutstanding=page.Summary!.ActualWorkOutstanding,cleanupSucceeded=page.Summary.CleanupSucceeded,resourcesDrained=true};
    }
    private sealed record ViewerResult(bool InteractionsPassed,int? ExitCode,string? Diagnostic);
    private static async Task<ViewerResult> ViewAsync(string repo,string root,string config,Guid stableTraceId)
    {
        Directory.CreateDirectory(Path.Combine(root,"empty-plugins"));
        var start=new ProcessStartInfo(Path.Combine(repo,"Rug.UI","bin","x64",config,"net10.0-windows10.0.22621.0","win-x64","Rug.UI.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.Environment["RUG_PLUGINS_ROOT"]=Path.Combine(root,"empty-plugins");start.Environment["RUG_TRACE_ROOT"]=Path.Combine(root,"traces");
        start.Environment["RUG_ANOMALY_ROOT"]=Path.Combine(root,"incidents");start.Environment["RUG_CHECKPOINT_ROOT"]=Path.Combine(root,"checkpoints");
        using var app=Process.Start(start)!;var stdout=app.StandardOutput.ReadToEndAsync();var stderr=app.StandardError.ReadToEndAsync();bool passed=false;string? diagnostic=null;
        try
        {
            var ready=Stopwatch.StartNew();
            while(true)
            {
                app.Refresh();if(app.HasExited)throw new InvalidOperationException("Viewer host exited during startup");
                if(app.MainWindowHandle!=0)break; // Cache the owned MAIN window before the viewer exists.
                if(ready.Elapsed>TimeSpan.FromSeconds(25))throw new TimeoutException("Viewer host startup");
                await Task.Delay(40);
            }
            var ps=new ProcessStartInfo("pwsh"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"-NoProfile","-File",Path.Combine(repo,"Rug.UI.Tests","ControlledVisualSampleUi.ps1"),"-AppProcessId",app.Id.ToString(),"-TraceId",stableTraceId.ToString("N")})ps.ArgumentList.Add(arg);
            using var probe=Process.Start(ps)!;var output=probe.StandardOutput.ReadToEndAsync();var errors=probe.StandardError.ReadToEndAsync();
            try{await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50));}
            catch{if(!probe.HasExited){probe.Kill(true);await probe.WaitForExitAsync();}Console.Write(await output);Console.Write(await errors);throw;}
            Console.Write(await output);passed=probe.ExitCode==0;if(!passed)diagnostic=await errors;
        }
        catch(Exception error){diagnostic=error.GetType().Name+": "+error.Message;}
        finally
        {
            if(!app.HasExited){app.CloseMainWindow();try{await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));}catch{diagnostic=(diagnostic??"")+"; Host close timed out; forced cleanup";app.Kill(true);await app.WaitForExitAsync();}}
        }
        await stdout;string errorLog=await stderr;
        if(app.ExitCode!=0)diagnostic=(diagnostic??"")+" UI shutdown exit="+app.ExitCode+"; "+errorLog[..Math.Min(1024,errorLog.Length)];
        Console.WriteLine($"VIEWER interactions={passed}; processExit={app.ExitCode}; {diagnostic}");return new(passed,app.ExitCode,diagnostic);
    }
    private static string Repo(){DirectoryInfo? d=new(AppContext.BaseDirectory);while(d is not null&&!File.Exists(Path.Combine(d.FullName,"Rug.slnx")))d=d.Parent;return d?.FullName??throw new DirectoryNotFoundException();}
    private static async Task Until(Func<bool> condition){var watch=Stopwatch.StartNew();while(!condition()){if(watch.Elapsed>TimeSpan.FromSeconds(12))throw new TimeoutException("Sample barrier/state wait");await Task.Delay(1);}}
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
