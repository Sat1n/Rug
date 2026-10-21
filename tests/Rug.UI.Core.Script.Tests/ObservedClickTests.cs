using System.Text.Json;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

internal static partial class CheckpointTests
{
    private sealed class ClickEnvironment : IObservedInputEnvironment
    {
        public long Now=100_000_000;
        public ObservedClickReason? Invalid;
        public Action? OnGeometry;
        public QpcReading ReadQpc()=>new(Now,10_000_000);
        public void ValidateGeometry(RunIdentity run,CapturedFrame frame)
        {if(Invalid is {} reason)throw new ObservedClickException(reason);OnGeometry?.Invoke();}
        public CapturedFrame Frame(byte value=80)
        {
            const int stride=272;
            var bytes=new byte[stride*64];
            for(int y=0;y<64;y++)for(int x=0;x<64;x++)
            {int i=y*stride+x*4;bytes[i]=bytes[i+1]=bytes[i+2]=value;bytes[i+3]=255;}
            var bounds=new PixelRegion(0,0,64,64);
            var geometry=new ObservationGeometry("test sampled exact",64,64,true,
                new("capture-image-pixels","client-physical-pixels",bounds,bounds,1,1,0,0));
            return new(bytes,64,64,stride){CaptureFacts=new(new(Now,10_000_000,Now),new("bound","bound",true),64,64,64,64,1,Now,Now,96,geometry)};
        }
    }
    private static object ClickParameters(ObservationEnvelope o,InputDeliveryMode mode=InputDeliveryMode.Win32PostMessage,
        double x=32,double y=32,string space="capture-image-pixels")=>new
        {frameReference=o.FrameReference,observationId=o.ObservationId,frameId=o.FrameId,runGeneration=o.Run.RunGeneration,
            x,y,coordinateSpace=space,button=0,mode=(int)mode,policyId="roi-consistency-v1"};
    private sealed class ClickFixture : IAsyncDisposable
    {
        public required Fixture Host;
        public required TaskExecutionContext Run;
        public required AutomationCallContext Caller;
        public required ObservationEnvelope Source;
                public required ObservationFramePool Pool;
        public FakeInput Input=>Host.Inputs[0];
        public OperationRequest Request(AutomationOperation op,object? parameters=null)=>new(op,
            JsonSerializer.SerializeToElement(parameters??new{}),Guid.NewGuid(),"observed-test",OperationTarget.ForRun(Run.Identity),DateTimeOffset.UtcNow.AddSeconds(10));
        public Task<OperationResult> Send(AutomationOperation op,object? parameters=null,CancellationToken token=default)=>Host.Scheduler.Gateway.ExecuteAsync(Caller,Request(op,parameters),token);
        public Task<OperationResult> Click(InputDeliveryMode mode=InputDeliveryMode.Win32PostMessage,CancellationToken token=default)=>Send(AutomationOperation.ClickObserved,ClickParameters(Source,mode),token);
        public async Task Observe()
        {Source=((CapturedFrameMetadata)(await Send(AutomationOperation.Capture)).RequireSuccess()!).Observation;}
        public static async Task<ClickFixture> Create(ObservationLimits? limits=null,int timeout=750,ExecutionTraceRecorder? traces=null)
        {
            var env=new ClickEnvironment();var pool=new ObservationFramePool(limits??new(65536,1024*1024));
            var host=await Fixture.CreateAsync(input:true,vision:true,checkpoint:false,script:"function on_tick() rug.sleep(60000) end",
                observationFrames:pool,observedClicks:new([new(VerificationTimeoutMs:timeout)],env),traces:traces);
            host.CaptureNext=()=>Task.FromResult<CapturedFrame?>(env.Frame());
            var run=await host.StartAsync();await Until(()=>run.TaskStatus==AutomationTaskStatus.Running);
            var caller=await host.Scheduler.Gateway.CreateContextAsync("probe","observed",AutomationSource.PluginUI);
            var f=new ClickFixture{Host=host,Run=run,Caller=caller,Pool=pool,Source=null!};
            // Both fixture and producer must use the same trusted clock/window state.
            f.Clock=env;await f.Observe();return f;
        }
        public ClickEnvironment Clock=null!;
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Check(Pool.Usage==new ObservationUsage(0,0,0,0,0)&&Pool.ActionUsage.ActiveInputs==0,"observed cleanup leaked frames/work");
        }
    }
    private static void ClickRejected(OperationResult result,OperationError error,bool uncertain=false)
    {Check(result.Status==OperationStatus.Rejected&&result.Error==error&&result.OutcomeUncertain==uncertain,
        $"observed rejection expected {error}/{uncertain}, got {result}");}
    public static async Task RunObservedClicksAsync()
    {
        await ObservedSuccessAsync();await ObservedInvalidAsync();await ObservedDerivedAsync();
        foreach(string change in new[]{"age","cache","geometry","identity"})await ObservedQueueAsync(change);
        foreach(var mode in new[]{InputDeliveryMode.Win32PostMessage,InputDeliveryMode.Win32SendInput})
        foreach(string change in new[]{"age","cache","geometry","scene"})await ObservedMoveAsync(mode,change);
        await ObservedForegroundAsync();await ObservedFailuresAsync();await ObservedStopAsync();
        await ObservedOriginalCancelAsync();await ObservedReleaseAsync();await ObservedLateAsync();
        await ObservedFinalGeometryAsync();
        await ObservedConcurrentAsync();await ObservedPauseAsync();await ObservedCleanupFailureAsync();await ObservedLuaAsync();
        Console.WriteLine("Observed clicks PASS: actual Lua/Gateway, input arbitration, trusted QPC/ROI, all execution boundaries, one-shot sources, cancellation/Stop/release and bounded resources");
    }
    private static async Task ObservedSuccessAsync()
    {
        await using var f=await ClickFixture.Create();var request=f.Request(AutomationOperation.ClickObserved,ClickParameters(f.Source));
        var result=await f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,request);
        Check(result.Status==OperationStatus.Success&&result.Value is ObservedClickResult{Status:"completed",ValidationFrames.Count:2}&&
            f.Input.MouseDownCalls==1&&f.Input.MouseUpCalls==1&&f.Input.X==32&&f.Input.Y==32,"valid observed click");
        Check(await f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,request)==result&&f.Input.MouseDownCalls==1,"dedup repeated click");
        ClickRejected(await f.Click(),OperationError.ObservationInvalid);
        await f.Observe();await f.Send(AutomationOperation.PressKey,new{key=66,duration_ms=0});
        ClickRejected(await f.Click(),OperationError.ObservationInvalid);
        await f.Observe();await f.Send(AutomationOperation.Click,new{x=2,y=2,button=0,mode=1});
        ClickRejected(await f.Click(),OperationError.ObservationInvalid);
    }
    private static async Task ObservedInvalidAsync()
    {
        await using var f=await ClickFixture.Create();
        foreach(var source in new[]{f.Source with{ObservationId=Guid.NewGuid()},f.Source with{FrameId=Guid.NewGuid()},
            f.Source with{FrameReference="foreign"},f.Source with{Run=f.Source.Run with{RunGeneration=99}}})
            ClickRejected(await f.Send(AutomationOperation.ClickObserved,ClickParameters(source)),OperationError.ObservationInvalid);
        var forged=JsonSerializer.SerializeToNode(ClickParameters(f.Source))!;forged["verified"]=true;
        ClickRejected(await f.Send(AutomationOperation.ClickObserved,forged),OperationError.InvalidParameters);
        ClickRejected(await f.Send(AutomationOperation.ClickObserved,ClickParameters(f.Source,x:0)),OperationError.ObservationCoordinateInvalid);
        var policy=JsonSerializer.SerializeToNode(ClickParameters(f.Source))!;policy["maxAgeMs"]=2000;
        ClickRejected(await f.Send(AutomationOperation.ClickObserved,policy),OperationError.ObservationPolicyUnavailable);
        var run=await f.Host.StartAsync();
        var cross=f.Request(AutomationOperation.ClickObserved,ClickParameters(f.Source)) with{Target=OperationTarget.ForRun(run.Identity)};
        ClickRejected(await f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,cross),OperationError.ObservationInvalid);
        var binding=f.Request(AutomationOperation.ClickObserved,ClickParameters(f.Source)) with{Target=OperationTarget.ForRun(f.Run.Identity) with{TargetBindingId=Guid.NewGuid()}};
        Check((await f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,binding)).Error==OperationError.BindingMismatch,"cross binding admitted");
        var limited=await f.Host.Scheduler.Gateway.CreateContextAsync("probe","input-only",AutomationSource.Agent,capabilities:[Permission.ControlInput]);
        var denied=await f.Host.Scheduler.Gateway.ExecuteAsync(limited,f.Request(AutomationOperation.ClickObserved,ClickParameters(f.Source)));
        Check(denied.Error==OperationError.PermissionDenied&&denied.MissingPermissions!.Contains(Permission.Capture),"caller capability bypassed capture permission");
        Check(f.Input.MoveCalls==0&&f.Input.MouseDownCalls==0,"invalid request produced input");
    }
    private static async Task ObservedDerivedAsync()
    {
        await using var f=await ClickFixture.Create();
        var ocr=(VisionOcrResult)(await f.Send(AutomationOperation.Ocr,new{region=new{X=16,Y=16,Width=32,Height=32}})).RequireSuccess()!;
        var click=await f.Send(AutomationOperation.ClickObserved,ClickParameters(ocr.Observation,x:16,y:16,space:"roi-local-pixels"));
        Check(click.Status==OperationStatus.Success&&f.Input.X==32&&f.Input.Y==32,"ROI offset applied incorrectly");
        ClickRejected(await f.Click(),OperationError.ObservationInvalid);
        await f.Observe();
        for(int i=1;i<ObservationFramePool.MaxObservationsPerFrame;i++)Check((await f.Send(AutomationOperation.Ocr)).Status==OperationStatus.Success,"bounded identity registration");
        Check((await f.Send(AutomationOperation.Ocr)).Error==OperationError.CapacityExceeded&&f.Pool.ActionUsage.RegisteredObservations==64,"unbounded observation registry");
        await f.Observe();Check(f.Pool.ActionUsage.RegisteredObservations==1,"replacement retained observation registry");
    }
    private static async Task ObservedQueueAsync(string change)
    {
        await using var f=await ClickFixture.Create();
        using var lease=await f.Host.Arbiter.AcquireAsync(InputOwner.ForDevTools("barrier"),f.Host.Arbiter.BindTarget(123),InputDeliveryMode.Win32PostMessage,()=>{});
        var pending=f.Click();await Until(()=>f.Host.Arbiter.Snapshot.Waiting==1);
        if(change=="age")f.Clock.Now+=20_000_000;
        if(change=="cache")await f.Observe();
        if(change=="geometry")f.Clock.Invalid=ObservedClickReason.GeometryChanged;
        if(change=="identity")f.Clock.Invalid=ObservedClickReason.WindowInvalid;
        lease.Dispose();
        var result=await pending;
        ClickRejected(result,change=="age"?OperationError.ObservationStale:change=="cache"?OperationError.ObservationInvalid:OperationError.ObservationGeometryChanged);
        Check(f.Input.MoveCalls==0&&f.Input.MouseDownCalls==0,"queued stale request moved");
    }
    private static async Task ObservedMoveAsync(InputDeliveryMode mode,string change)
    {
        await using var f=await ClickFixture.Create();
        f.Input.OnMove=async()=>
        {
            if(change=="age")f.Clock.Now+=20_000_000;
            if(change=="cache")await f.Observe();
            if(change=="geometry")f.Clock.Invalid=ObservedClickReason.GeometryChanged;
            if(change=="scene")f.Host.Captures[0].Next=()=>Task.FromResult<CapturedFrame?>(f.Clock.Frame(200));
        };
        ClickRejected(await f.Click(mode),change switch{"age"=>OperationError.ObservationStale,"cache"=>OperationError.ObservationInvalid,
            "geometry"=>OperationError.ObservationGeometryChanged,_=>OperationError.ObservationSceneChanged},true);
        Check(f.Input.MoveCalls==1&&f.Input.MouseDownCalls==0,"post-move rejection pressed");
    }
    private static async Task ObservedForegroundAsync()
    {
        await using var f=await ClickFixture.Create();f.Host.Foreground=_=>{f.Clock.Invalid=ObservedClickReason.GeometryChanged;return true;};
        ClickRejected(await f.Click(InputDeliveryMode.Win32SendInput),OperationError.ObservationGeometryChanged,true);
        Check(f.Input.MoveCalls==0&&f.Input.MouseDownCalls==0,"foreground change pressed");
    }
    private static async Task ObservedFailuresAsync()
    {
        foreach(string failure in new[]{"scene","none","future","unknown","clock","io","timeout","capacity"})
        {
            await using var f=await ClickFixture.Create(failure=="capacity"?new(65536,131072):null,timeout:failure=="timeout"?10:750);
            CapturedFrame? Frame()
            {
                if(failure=="io")throw new IOException("actual capture error");
                if(failure=="timeout")f.Clock.Now+=200_000; // Authoritative QPC advances without waiting for a timer callback.
                if(failure=="none")return null;
                var frame=f.Clock.Frame(failure=="scene"?(byte)200:(byte)80);
                if(failure=="unknown")return frame with{CaptureFacts=frame.CaptureFacts! with{NativeTime=null}};
                if(failure=="future")return frame with{CaptureFacts=frame.CaptureFacts! with{NativeTime=new(f.Clock.Now+100,10_000_000,f.Clock.Now)}};
                if(failure=="clock")return frame with{CaptureFacts=frame.CaptureFacts! with{NativeTime=new(f.Clock.Now,10_000_000,f.Clock.Now,"foreign")}};
                return frame;
            }
            f.Host.Captures[0].Next=()=>Task.FromResult(Frame());
            var result=await f.Click();
            if(failure=="io")Check(result.Status==OperationStatus.Failed&&result.Error==OperationError.ExecutionFailed&&
                result.Value is ObservedClickResult details&&details.FrameId==f.Source.FrameId,"IO swallowed as validation refusal/lost trusted source");
            else ClickRejected(result,failure switch{"scene"=>OperationError.ObservationSceneChanged,"future" or "unknown" or "clock"=>OperationError.ObservationTimeUnavailable,_=>OperationError.ObservationValidationUnavailable});
            Check(f.Input.MoveCalls==0&&f.Input.MouseDownCalls==0,"failed scene validation sent input");
        }
    }
    private static async Task ObservedStopAsync()
    {
        foreach(string stop in new[]{"stop","revoke","takeover"})
        {
            await using var f=await ClickFixture.Create();var entered=Signal();var release=Signal();
            f.Host.Captures[0].Next=async()=>{entered.TrySetResult();await release.Task;return f.Clock.Frame();};
            var pending=f.Click();await Bound(entered.Task);Task? ending=null;
            try
            {
                if(stop=="stop")ending=f.Host.Scheduler.StopTaskAsync(f.Run.InstanceId);
                if(stop=="takeover")ending=f.Host.Arbiter.TakeOverAsync();
                if(stop=="revoke")f.Host.Authorization.Revoke(f.Run.RecoveryAuthorization!.Installation,[Permission.ControlInput]);
                await Until(()=>f.Run.IsBusinessClosed);
                Check(!f.Host.Captures[0].Disposed&&f.Pool.Usage.Operations>0,"Stop released actual verification work");
                release.TrySetResult();var result=await pending;
                if(ending is not null)await Bound(ending);else await Bound(f.Run.Completion);
                Check(result.Status!=OperationStatus.Success&&f.Input.MouseDownCalls==0,"Stop/revoke/takeover allowed press");
            }
            finally{release.TrySetResult();}
        }
    }
    private static async Task ObservedOriginalCancelAsync()
    {
      foreach(bool afterMove in new[]{false,true})
      {
        await using var f=await ClickFixture.Create();var entered=Signal();var release=Signal();
        if(afterMove)f.Input.OnMove=async()=>{entered.TrySetResult();await release.Task;};
        else f.Host.Captures[0].Next=async()=>{entered.TrySetResult();await release.Task;return f.Clock.Frame();};
        using var source=new CancellationTokenSource();var pending=f.Click(token:source.Token);await Bound(entered.Task);
        var blocked=Signal();var unblock=Signal();using var registration=source.Token.Register(()=>{blocked.TrySetResult();unblock.Task.GetAwaiter().GetResult();});
        var cancel=Task.Run(source.Cancel);
        try{await Bound(blocked.Task);release.TrySetResult();var result=await pending;Check(result.Status==OperationStatus.Cancelled&&f.Input.MoveCalls==(afterMove?1:0)&&
            result.OutcomeUncertain==afterMove&&f.Input.MouseDownCalls==0&&result.Value is ObservedClickResult details&&
            details.FrameId==f.Source.FrameId&&details.ClientPoint==new PointInt(32,32),"original cancellation bypassed final validation/lost source");}
        finally{unblock.TrySetResult();release.TrySetResult();await Bound(cancel);}
      }
    }
    private static async Task ObservedReleaseAsync()
    {
        await using var f=await ClickFixture.Create();using var source=new CancellationTokenSource();
        f.Input.OnDown=()=>{source.Cancel();return Task.CompletedTask;};
        var result=await f.Click(token:source.Token);await Until(()=>f.Pool.Usage.Operations==0);
        Check(result.Status==OperationStatus.Cancelled&&result.OutcomeUncertain&&f.Input.MouseDownCalls==1&&f.Input.MouseUpCalls==1,"cancellation skipped necessary release");
    }
    private static async Task ObservedFinalGeometryAsync()
    {
        foreach(var mode in new[]{InputDeliveryMode.Win32PostMessage,InputDeliveryMode.Win32SendInput})
        {
            // Calibrate the last geometry query preceding each actual side effect in an
            // unchanged control run. Do not pin the regression to incidental query counts.
            var boundaries=new Dictionary<string,int>();int queries=0;
            await using(var control=await ClickFixture.Create())
            {
                control.Clock.OnGeometry=()=>queries++;
                control.Host.Foreground=_=>{boundaries["foreground"]=queries;return true;};
                control.Input.OnMove=()=>{boundaries["move"]=queries;return Task.CompletedTask;};
                control.Input.OnDown=()=>{boundaries["down"]=queries;return Task.CompletedTask;};
                Check((await control.Click(mode)).Status==OperationStatus.Success,"geometry boundary control failed");
            }
            foreach(var boundary in boundaries)
            foreach(string change in new[]{"cancel","age"})
            {
                await using var f=await ClickFixture.Create();using var caller=new CancellationTokenSource();
                var blocked=Signal();var unblock=Signal();Task? cancelling=null;CancellationTokenRegistration registration=default;
                int checks=0,foreground=0;bool injected=false;
                f.Host.Foreground=_=>{foreground++;return true;};
                f.Clock.OnGeometry=()=>
                {
                    if(++checks!=boundary.Value)return;
                    injected=true;
                    if(change=="age"){f.Clock.Now+=20_000_000;return;}
                    // Registered after Gateway's links, so this callback blocks propagation
                    // while the original token is already cancelled. No sleeps or real input.
                    registration=caller.Token.Register(()=>{blocked.TrySetResult();unblock.Task.GetAwaiter().GetResult();});
                    cancelling=Task.Run(caller.Cancel);
                    blocked.Task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
                };
                try
                {
                    var result=await f.Click(mode,caller.Token).WaitAsync(TimeSpan.FromSeconds(8));
                    await Until(()=>f.Pool.Usage.Operations==0);
                    bool moved=boundary.Key=="down";
                    bool uncertain=moved||(mode==InputDeliveryMode.Win32SendInput&&boundary.Key=="move");
                    Check(injected,$"geometry barrier not reached: {mode}/{boundary.Key}/{change}");
                    if(change=="cancel")Check(result.Status==OperationStatus.Cancelled&&result.OutcomeUncertain==uncertain,
                        $"final geometry cancellation: {mode}/{boundary.Key}: {result}");
                    else ClickRejected(result,OperationError.ObservationStale,uncertain);
                    Check(f.Input.MoveCalls==(moved?1:0)&&f.Input.MouseDownCalls==0&&f.Input.MouseUpCalls==0&&
                        foreground==(mode==InputDeliveryMode.Win32SendInput&&boundary.Key!="foreground"?1:0),
                        $"geometry query admitted a later side effect: {mode}/{boundary.Key}/{change}");
                }
                finally
                {
                    f.Clock.OnGeometry=null;unblock.TrySetResult();
                    if(cancelling is not null)await Bound(cancelling);
                    registration.Dispose();
                }
            }
        }
    }
    private static async Task ObservedConcurrentAsync()
    {
        await using var f=await ClickFixture.Create();var entered=Signal();var release=Signal();int calls=0;
        f.Host.Captures[0].Next=async()=>{if(Interlocked.Increment(ref calls)==1){entered.TrySetResult();await release.Task;}return f.Clock.Frame();};
        var first=f.Click();await Bound(entered.Task);var second=f.Click();
        await Until(()=>f.Pool.Usage.Operations==3); // Two source holds plus first verification capture.
        release.TrySetResult();var results=await Task.WhenAll(first,second);
        Check(results.Count(r=>r.Status==OperationStatus.Success)==1&&results.Count(r=>r.Error==OperationError.ObservationInvalid)==1&&
            f.Input.MouseDownCalls==1&&f.Input.MouseUpCalls==1,"same-source concurrent actions both pressed");
        await f.Observe();var source=f.Source;
        using(f.Pool.BeginExternalInput())await f.Observe();
        ClickRejected(await f.Click(),OperationError.ObservationInvalid);
        ClickRejected(await f.Send(AutomationOperation.ClickObserved,ClickParameters(source)),OperationError.ObservationInvalid);
    }
    private static async Task ObservedCleanupFailureAsync()
    {
        await using var f=await ClickFixture.Create();f.Input.OnUp=()=>throw new IOException("real release failure");
        var result=await f.Click();
        Check(result.Status is OperationStatus.Failed or OperationStatus.Cancelled &&result.OutcomeUncertain&&f.Input.MouseUpCalls==1&&
            f.Host.Arbiter.Snapshot.State==InputControlState.CleanupFailed,$"cleanup failure hidden as validation refusal: {result} / {f.Host.Arbiter.Snapshot} / ups={f.Input.MouseUpCalls}");
    }
    private static async Task ObservedPauseAsync()
    {
        await using var f=await ClickFixture.Create();var entered=Signal();var release=Signal();int captures=0;
        f.Host.Captures[0].Next=async()=>{if(Interlocked.Increment(ref captures)==1){entered.TrySetResult();await release.Task;}return f.Clock.Frame();};
        var pending=f.Click();await Bound(entered.Task);
        Check(f.Host.Scheduler.PauseTask(f.Run.InstanceId),"observed pause rejected");
        Check(f.Run.PauseRequested&&f.Run.TaskStatus!=AutomationTaskStatus.Paused,"pause confirmed before actual observed work");
        var refused=await f.Click();Check(refused.Error==OperationError.InvalidLifecycleState&&!refused.OutcomeUncertain,"paused new action admitted");
        release.TrySetResult();Check((await pending).Status==OperationStatus.Success,"already admitted observed work did not complete safely");
        await Until(()=>f.Run.TaskStatus==AutomationTaskStatus.Paused);
        Check(f.Input.MouseDownCalls==1&&f.Input.MouseUpCalls==1,"pause lost input release");
        Check(f.Host.Scheduler.ResumeTask(f.Run.InstanceId),"observed resume rejected");
        await Until(()=>f.Run.TaskStatus==AutomationTaskStatus.Running);
        ClickRejected(await f.Click(),OperationError.ObservationInvalid);
    }
    private static async Task ObservedLateAsync()
    {
        string root=Path.Combine(AppContext.BaseDirectory,"observed-held-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var env=new ClickEnvironment();var pool=new ObservationFramePool(new(65536,1024*1024));
            var run=new RunIdentity("one",Guid.NewGuid(),1,Guid.NewGuid(),123);
            CapturedFrame? frameOverride=null;
            var capture=new ObservationCapture(_=>Task.FromResult<CapturedFrame?>(frameOverride??env.Frame()));var ocr=new ObservationOcr{Hold=true};
            using var visual=ObservationBridge(root,pool,run,capture,ocr);
            var first=(await visual.CaptureAsync())!.Observation;var pending=visual.OcrAsync();await Bound(ocr.Entered.Task);
            var second=(await visual.CaptureAsync())!.Observation;ocr.Release.TrySetResult();var late=(await pending).Observation;
            Check(late.FrameId==first.FrameId&&pool.ActionUsage.RegisteredObservations==1,"late OCR reactivated old frame");
            void Reject(VisualEngineBridge v,ObservationEnvelope o)
            {
                try{using var action=v.PrepareObservedClick(ObservedClickRequest.Parse(JsonSerializer.SerializeToElement(ClickParameters(o))),new(environment:env));throw new Exception("foreign/old source accepted");}
                catch(ObservedClickException e){Check(e.Reason==ObservedClickReason.InvalidReference,"wrong late source rejection");}
            }
            Reject(visual,late);
            using var foreign=ObservationBridge(root,pool,new RunIdentity("other",Guid.NewGuid(),1,Guid.NewGuid(),123),capture,new ObservationOcr());
            await foreign.CaptureAsync();Reject(foreign,second);
            await File.WriteAllBytesAsync(Path.Combine(root,"template.png"),[137,80,78,71]);
            var match=(await visual.FindImageAsync("template.png"))!.Observation;
            using var valid=visual.PrepareObservedClick(ObservedClickRequest.Parse(JsonSerializer.SerializeToElement(ClickParameters(match))),new(environment:env));
            valid.Validate(default);
            var wide=env.Frame();var geometry=wide.CaptureFacts!.Geometry with{ClientWidth=64000,ClientHeight=64000,
                ImageToClient=new("capture-image-pixels","client-physical-pixels",new(0,0,64,64),new(0,0,64000,64000),1000,1000,0,0)};
            frameOverride=wide with{CaptureFacts=wide.CaptureFacts with{Geometry=geometry}};
            var huge=(await visual.CaptureAsync())!.Observation;
            try{using var oversized=visual.PrepareObservedClick(ObservedClickRequest.Parse(JsonSerializer.SerializeToElement(ClickParameters(huge,x:40,y:40))),new(environment:env));throw new Exception("PostMessage coordinate wrapped");}
            catch(ObservedClickException e){Check(e.Reason==ObservedClickReason.CoordinateInvalid,"PostMessage LPARAM range rejection");}
            visual.Dispose();Reject(foreign,match);
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task ObservedLuaAsync()
    {
        var clock=new ClickEnvironment();var pool=new ObservationFramePool(new(65536,1024*1024));
        const string script="""
            function on_init()
              local c=rug.capture(); local o=c.observation
              assert(c.width==64 and c.stride==272)
              local request=string.format('{"frameReference":"%s","observationId":"%s","frameId":"%s","runGeneration":%d,"x":32,"y":32,"coordinateSpace":"capture-image-pixels","button":0,"mode":1,"policyId":"roi-consistency-v1"}',o.frameReference,o.observationId,o.frameId,o.run.runGeneration)
              local a=rug.click_observed(request);assert(a.status=='completed' and a.frameId==o.frameId)
              local b=rug.click_observed(request);assert(b.status=='rejected' and b.reason=='InvalidReference')
              rug.set_state('observed_done',1)
            end
            function on_tick() rug.sleep(60000) end
            """;
        await using var f=await Fixture.CreateAsync(input:true,checkpoint:false,script:script,observationFrames:pool,observedClicks:new(environment:clock));
        f.CaptureNext=()=>Task.FromResult<CapturedFrame?>(clock.Frame());var c=await f.StartAsync();
        await Until(()=>f.StoreState.GetState(c.InstanceScope,"observed_done",0)==1||c.TaskStatus==AutomationTaskStatus.Faulted);
        Check(c.TaskStatus!=AutomationTaskStatus.Faulted&&f.Inputs[0].MouseDownCalls==1&&f.Inputs[0].MouseUpCalls==1,"actual Lua observed click: "+c.LastError);
    }
}
