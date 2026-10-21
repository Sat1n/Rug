using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;

internal static partial class CheckpointTests
{
    private sealed class ObservationCapture(Func<int,Task<CapturedFrame?>> next) : ICaptureService
    {
        public int Calls;
        public bool IsCapturing => true;
        public Task StartAsync(nint hwnd,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken=default)=>next(Interlocked.Increment(ref Calls));
        public Task StopAsync()=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class ObservationOcr : IOcrService
    {
        public bool Hold, Fail;
        public int Calls;
        public CapturedFrame? Frame;
        public readonly TaskCompletionSource Entered=Signal(),Release=Signal();
        public IReadOnlyList<string> ListPaddleModelIds()=>[];
        public Task<IReadOnlyList<OcrTextBlock>> RecognizeAsync(string path,OcrEngineType engine,string? modelId=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public async Task<IReadOnlyList<OcrTextBlock>> RecognizeFrameAsync(CapturedFrame frame,OcrEngineType engine,string? modelId=null,CancellationToken cancellationToken=default)
        {
            Calls++;Frame=frame;Entered.TrySetResult();if(Hold)await Release.Task;
            if(Fail)throw new IOException("actual OCR failure");
            return [new("ready",1,new(0,0,1,1))];
        }
    }
    private sealed class ObservationMatch : ITemplateMatchService
    {
        public bool Miss;
        public CapturedFrame? Frame;
        public Task<IReadOnlyList<TemplateMatchResult>> MatchAsync(string target,string template,float threshold=.8f,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<IReadOnlyList<TemplateMatchResult>> MatchFrameAsync(CapturedFrame frame,string template,float threshold=.8f,CancellationToken cancellationToken=default)
        {Frame=frame;return Task.FromResult<IReadOnlyList<TemplateMatchResult>>(Miss?[]:[new(0,0,1)]);}
    }
    private sealed class ObservationClock : TimeProvider
    {
        public long Ticks=100;
        public DateTimeOffset Utc=new(2026,10,20,0,0,0,TimeSpan.Zero);
        public override long TimestampFrequency=>1000;
        public override long GetTimestamp()=>Ticks;
        public override DateTimeOffset GetUtcNow()=>Utc;
    }
    private static CapturedFrame ObservationPixels(byte marker=7)
    {
        var bytes=new byte[72]; // 4x3 BGRA, stride 24 (8 padding bytes per row).
        for(int y=0;y<3;y++)for(int x=0;x<4;x++){int i=y*24+x*4;bytes[i]=(byte)(marker+y*4+x);bytes[i+3]=255;}
        return new(bytes,4,3,24);
    }
    private static VisualEngineBridge ObservationBridge(string root,ObservationFramePool pool,RunIdentity identity,
        ICaptureService capture,ObservationOcr ocr,ObservationMatch? match=null)
    {
        var manifest=new PluginManifest([Permission.Capture,Permission.VisionOcr,Permission.VisionMatch]){Id=identity.PluginId,PluginDirectory=root};
        return new(capture,ocr,match??new ObservationMatch(),TestAuthorizationHost.Gate(manifest,NullLogger.Instance),identity,root,pool);
    }
    private static async Task ObservationRejectAsync<T>(Func<Task> action) where T:Exception
    {try{await action();throw new Exception("Observation expected "+typeof(T).Name);}catch(T){}}
    public static async Task RunObservationsAsync()
    {
        CaptureGeometryTests.Run();
        string root=Path.Combine(AppContext.BaseDirectory,"observations-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root,"template.png"),[137,80,78,71]);
        try
        {
            await ObservationLeasesAsync(root);
            await ObservationLimitsAsync(root);
            await ObservationLateCaptureAsync(root);
            await ObservationLuaAsync();
            await ObservationStopAsync();
            await ObservationCallerCancelAsync();
            await ObservationDeadlineAsync(root);
            await ObservationGatewayCapacityAsync();
            Console.WriteLine("Observations PASS: host identity, actual-frame provenance, padded ROI, global capacity, opaque reference isolation, late/cancel/Stop publication, actual Lua and drained resources");
        }
        finally{Directory.Delete(root,true);}
    }
    private static CaptureFacts ObservationFacts(int width, int height, long time) =>
        new(new(time,10_000_000,time+20),new("opaque-bound","opaque-capture",true),width,height,width,height,4,0,0,96,new());
    private static async Task ObservationLeasesAsync(string root)
    {
        var clock=new ObservationClock();var pool=new ObservationFramePool(new(96,1024,8,4,8),clock);
        var run=new RunIdentity("same",Guid.NewGuid(),3,Guid.NewGuid(),123);
        var first=ObservationPixels() with {CaptureFacts=ObservationFacts(4,3,100)};
        var capture=new ObservationCapture(i=>Task.FromResult<CapturedFrame?>(i==1?first:ObservationPixels(20) with {CaptureFacts=ObservationFacts(4,3,200)}));
        var ocr=new ObservationOcr{Hold=true};var match=new ObservationMatch();
        using var visual=ObservationBridge(root,pool,run,capture,ocr,match);
        try
        {
            var a=(await visual.CaptureAsync())!;first.Pixels[28]=200;
            Check(a.Observation.Run==ObservationRun.From(run) && a.Observation.FrameId!=Guid.Empty && a.Observation.ObservationId!=a.Observation.FrameId,"host frame/run identity");
            Check(a.Observation.Time.NativeAcquiredUtc is null && a.Observation.Time.NativeAcquiredTimestamp is null &&
                !a.Observation.Geometry.ClientGeometryVerified && a.Observation.Geometry.ClientWidth is null,"fabricated acquisition/client geometry");
            clock.Ticks+=10;var held=visual.OcrAsync(new(1,1,2,2),frameReference:a.Observation.FrameReference);
            await Bound(ocr.Entered.Task);
            Check(ocr.Frame!.Stride==8 && ocr.Frame.Pixels[0]==12 && ocr.Frame.Pixels[8]==16,"padded stride/ROI copy incorrect");
            var b=(await visual.CaptureAsync())!;
            Check(b.Observation.FrameId!=a.Observation.FrameId && pool.Usage is {Frames:2,ResidentBytes:144,ReservedBytes:16,Operations:1},"old frame reclaimed while OCR holds it");
            await ObservationRejectAsync<ObservationReferenceException>(()=>visual.OcrAsync(frameReference:a.Observation.FrameReference));
            var found=(await visual.FindImageAsync("template.png",region:new(1,1,2,2)))!;
            Check(found.X==1 && found.Y==1 && found.Observation.FrameId==b.Observation.FrameId &&
                found.Observation.Processing is {OffsetX:1,OffsetY:1,InputStride:8,InputCoordinateSpace:"roi-local-pixels",ActualResultCoordinateSpace:"capture-image-pixels"},"match source/ROI applied twice");
            ocr.Release.TrySetResult();var recognized=await held;
            Check(recognized.Observation.CaptureFacts==first.CaptureFacts && recognized.Observation.Time.NativeTime?.SystemRelative100ns==100 &&
                b.Observation.Time.NativeTime?.SystemRelative100ns==200,"native facts replaced by newer cached frame");
            Check(recognized.Observation.FrameId==a.Observation.FrameId && recognized.Observation.ObservationId!=a.Observation.ObservationId &&
                recognized.Blocks[0].BoundingBox is {X:1,Y:1} && recognized.Observation.Processing!.Roi==new Rect(1,1,2,2),"OCR returned B instead of held A");
            Check(pool.Usage is {Frames:1,ResidentBytes:72,ReservedBytes:0,Operations:0},"old frame/crop lease leaked");
            clock.Ticks+=2000;clock.Utc=clock.Utc.AddDays(-5);
            Check(b.Observation.Time.NativeTime?.AgeAtNativeSample==TimeSpan.FromTicks(20),"native age mixed UTC/host clock");
            Check(pool.HostReceiptAge(b.Observation)==TimeSpan.FromSeconds(2),"age used wall clock or pretended native acquisition time");
            var again=await visual.OcrAsync();Check(again.Observation.FrameId==b.Observation.FrameId && again.Observation.ObservationId!=recognized.Observation.ObservationId && capture.Calls==2,"cache read fabricated capture");
            foreach(string plugin in new[]{"same","different"})
            {
                using var other=ObservationBridge(root,pool,new(plugin,Guid.NewGuid(),1,Guid.NewGuid(),123),capture,new());
                await other.CaptureAsync();
                await ObservationRejectAsync<ObservationReferenceException>(()=>other.OcrAsync(frameReference:b.Observation.FrameReference));
            }
            using(var generation=ObservationBridge(root,pool,new(run.PluginId,run.InstanceId,run.RunGeneration+1,Guid.NewGuid(),123),capture,new()))
            {await generation.CaptureAsync();await ObservationRejectAsync<ObservationReferenceException>(()=>generation.OcrAsync(frameReference:b.Observation.FrameReference));}
            match.Miss=true;Check(await visual.FindImageAsync("template.png") is null,"no-match fabricated successful observation");
            await ObservationRejectAsync<ArgumentOutOfRangeException>(()=>visual.OcrAsync(new(int.MaxValue,0,1,1)));
            await ObservationRejectAsync<ArgumentOutOfRangeException>(()=>visual.OcrAsync(new(0,0,0,1)));
            var json=JsonSerializer.Serialize(b);Check(!json.Contains("Pixels") && !json.Contains("Hwnd") && !json.Contains("DataLength"),"public observation leaked pixels/handle");
        }
        finally{ocr.Release.TrySetResult();visual.Dispose();}
        Check(pool.Usage==new ObservationUsage(0,0,0,0,0),"isolation/lease/session resources leaked");
    }
    private static async Task ObservationLimitsAsync(string root)
    {
        var pool=new ObservationFramePool(new(96,320,4,2,4));
        var identity=new RunIdentity("bounded",Guid.NewGuid(),1,null,0);
        var source=new ObservationCapture(_=>Task.FromResult<CapturedFrame?>(ObservationPixels()));
        using(var a=ObservationBridge(root,pool,identity,source,new()))
        using(var b=ObservationBridge(root,pool,identity with {},source,new()))
        {
            await a.CaptureAsync();await b.CaptureAsync();int calls=source.Calls;
            await ObservationRejectAsync<ObservationCapacityException>(()=>a.CaptureAsync());
            Check(source.Calls==calls && pool.Usage.Frames==2,"global byte capacity did not reject before producer call");
        }
        Check(pool.Usage==new ObservationUsage(0,0,0,0,0),"capacity rejection leaked counters");
        var heldOcr=new ObservationOcr{Hold=true};pool=new(new(96,512,4,1,2));
        using(var a=ObservationBridge(root,pool,identity,source,heldOcr))
        {
            await a.CaptureAsync();using var cancel=new CancellationTokenSource();var pending=a.OcrAsync(cancellationToken:cancel.Token);
            await Bound(heldOcr.Entered.Task);cancel.Cancel();a.Dispose();
            Check(pool.Usage is {Frames:1,Operations:1,ResidentBytes:72},"cancel/dispose reclaimed actual noncooperative recognition frame");
            using(var other=ObservationBridge(root,pool,new("other",Guid.NewGuid(),1,null,0),source,new()))
                await ObservationRejectAsync<ObservationCapacityException>(()=>other.CaptureAsync());
            heldOcr.Release.TrySetResult();await ObservationRejectAsync<OperationCanceledException>(()=>pending);
            await ObservationRejectAsync<ObjectDisposedException>(()=>a.OcrAsync());
        }
        Check(pool.Usage==new ObservationUsage(0,0,0,0,0),"noncooperative lease not released after actual completion");
        CapturedFrame? value=null;pool=new(new(96,512,2,2,2));
        using(var a=ObservationBridge(root,pool,identity,new ObservationCapture(_=>Task.FromResult(value)),new()))
        {
            Check(await a.CaptureAsync() is null,"no frame published success");
            await ObservationRejectAsync<ObservationReferenceException>(()=>a.OcrAsync());
            foreach(var invalid in new[]{new CapturedFrame([],0,1,0),new([],int.MaxValue,1,int.MaxValue),new(new byte[4],4,3,24)})
            {value=invalid;await ObservationRejectAsync<InvalidDataException>(()=>a.CaptureAsync());}
            value=new(new byte[100],5,5,20);await ObservationRejectAsync<ObservationCapacityException>(()=>a.CaptureAsync());
            value=ObservationPixels();var prior=(await a.CaptureAsync())!;
            value=null;Check(await a.CaptureAsync() is null,"no-frame produced metadata");
            await ObservationRejectAsync<ObservationReferenceException>(()=>a.OcrAsync(frameReference:prior.Observation.FrameReference));
        }
        var native=new RugFrame{Width=1,Height=1,Stride=4,Format=RugPixelFormat.Bgra8,DataLength=uint.MaxValue};
        try{CaptureService.ValidateLayout(native);throw new Exception("native size accepted");}catch(ObservationCapacityException){}
        native.Stride=1;try{CaptureService.ValidateLayout(native);throw new Exception("native stride accepted");}catch(InvalidDataException){}
        native.Stride=4;native.DataLength=16;
        try{CaptureService.ValidateLayout(native,8);throw new Exception("custom native ingress limit ignored");}catch(ObservationCapacityException){}
        pool=new(new(96,512,1,2,1));using(var a=ObservationBridge(root,pool,identity,source,new()))
        {
            await a.CaptureAsync();await ObservationRejectAsync<ObservationCapacityException>(()=>a.CaptureAsync());
            try{using var b=ObservationBridge(root,pool,identity,source,new());throw new Exception("session capacity ignored");}catch(ObservationCapacityException){}
            Check(pool.Usage is {Frames:1,Operations:0,ReservedBytes:0},"frame/session rejection leaked reservation");
        }
        pool=new(new(96,512));var failing=new ObservationOcr{Fail=true};
        using(var a=ObservationBridge(root,pool,identity,source,failing))
        {await a.CaptureAsync();await ObservationRejectAsync<IOException>(()=>a.OcrAsync());Check(pool.Usage.Operations==0,"OCR exception leaked operation");}
        Check(pool.Usage==new ObservationUsage(0,0,0,0,0),"invalid frame/service error leaked resources");
    }
    private static async Task ObservationLateCaptureAsync(string root)
    {
        var pool=new ObservationFramePool(new(96,1024));var entered=Signal();var release=Signal();
        var capture=new ObservationCapture(async i=>{if(i==1){entered.TrySetResult();await release.Task;}return ObservationPixels((byte)i);});
        using var a=ObservationBridge(root,pool,new("late",Guid.NewGuid(),1,null,0),capture,new());
        var late=a.CaptureAsync();await Bound(entered.Task);
        var fresh=(await a.CaptureAsync())!;release.TrySetResult();
        await ObservationRejectAsync<OperationCanceledException>(()=>late);
        Check((await a.OcrAsync()).Observation.FrameId==fresh.Observation.FrameId,"late capture overwrote newer cache");
        a.Dispose();Check(pool.Usage==new ObservationUsage(0,0,0,0,0),"late capture leaked reservation");
    }
    private static async Task ObservationLuaAsync()
    {
        var pool=new ObservationFramePool();
        const string script="""
            function on_init()
              local c=rug.capture()
              local o=rug.ocr()
              assert(c.width==8 and c.height==8 and c.stride==32)
              assert(o.text=='ready' and o.blocks[1].text=='ready' and o[1].text=='ready')
              assert(c.observation.frameId==o.observation.frameId)
              assert(c.observation.observationId~=o.observation.observationId)
              assert(c.observation.schemaVersion==2 and c.observation.run.pluginId=='probe')
              assert(c.observation.time.nativeAcquiredUtc==nil and c.observation.geometry.clientGeometryVerified==false)
              assert(c.observation.time.nativeTime.systemRelative100ns==10)
              assert(o.observation.time.nativeTime.systemRelative100ns==10 and c.observation.captureFacts.contentWidth==8)
              rug.set_state('source_frame',c.observation.frameId)
              rug.set_state('source_instance',c.observation.run.instanceId)
            end
            function on_tick() rug.sleep(60000) end
            """;
        await using var f=await Fixture.CreateAsync(script:script,vision:true,observationFrames:pool);
        f.CaptureMetadata=ObservationFacts(8,8,10);
        var a=await f.StartAsync();var b=await f.StartAsync();
        await Until(()=>f.StoreState.Contains(a.InstanceScope,"source_instance")&&f.StoreState.Contains(b.InstanceScope,"source_instance"));
        Check(f.StoreState.GetState(a.InstanceScope,"source_instance","")==a.InstanceId.ToString() &&
            f.StoreState.GetState(a.InstanceScope,"source_frame","")!=f.StoreState.GetState(b.InstanceScope,"source_frame",""),"Lua identity shared between instances");
        var caller=await f.Scheduler.Gateway.CreateContextAsync("probe","observations",AutomationSource.Mcp);
        var request=new OperationRequest(AutomationOperation.Ocr,JsonSerializer.SerializeToElement(new{}),Guid.NewGuid(),"observation",OperationTarget.ForRun(a.Identity),DateTimeOffset.UtcNow.AddSeconds(10));
        var result=await f.Scheduler.Gateway.ExecuteAsync(caller,request);
        Check(result.Value is VisionOcrResult o && o.Observation.FrameId.ToString()==f.StoreState.GetState(a.InstanceScope,"source_frame","") &&
            o.Observation.Run==ObservationRun.From(a.Identity),"Gateway metadata differs from Lua/host identity");
        var original=(VisionOcrResult)result.Value!;
        _=original.Observation with{Run=new("forged",Guid.NewGuid(),999,null)}; // Detached copies grant no access and do not mutate cache.
        var again=await f.Scheduler.Gateway.ExecuteAsync(caller,request with{OperationId=Guid.NewGuid()});
        Check(((VisionOcrResult)again.Value!).Observation.Run==ObservationRun.From(a.Identity),"caller changed stored observation");
        await f.Scheduler.StopTaskAsync(a.InstanceId);await f.Scheduler.StopTaskAsync(b.InstanceId);
        Check(pool.Usage==new ObservationUsage(0,0,0,0,0),"actual Lua stop did not drain frame sessions");
        Check((await f.Scheduler.Gateway.ExecuteAsync(caller,request with{OperationId=Guid.NewGuid()})).Status!=OperationStatus.Success,"stopped run reused old observation");
    }
    private static async Task ObservationStopAsync()
    {
        var pool=new ObservationFramePool();await using var f=await Fixture.CreateAsync(script:"function on_tick() rug.sleep(60000) end",observationFrames:pool);
        var c=await f.StartAsync();f.Captures[0].Hold=true;
        var caller=await f.Scheduler.Gateway.CreateContextAsync("probe","late-observation",AutomationSource.PluginUI);
        var request=new OperationRequest(AutomationOperation.Capture,JsonSerializer.SerializeToElement(new{}),Guid.NewGuid(),"late-capture",OperationTarget.ForRun(c.Identity),DateTimeOffset.UtcNow.AddSeconds(10));
        var capture=f.Scheduler.Gateway.ExecuteAsync(caller,request);await Bound(f.Captures[0].Blocked.Task);
        var cancelEntered=Signal();var releaseCancel=Signal();CancellationTokenRegistration blocked=default;Task? stop=null;
        try
        {
            blocked=c.LifetimeToken.Register(()=>{cancelEntered.TrySetResult();releaseCancel.Task.GetAwaiter().GetResult();});
            stop=Task.Run(async()=>await f.Scheduler.StopTaskAsync(c.InstanceId));await Bound(cancelEntered.Task);
            Check(c.TaskStatus==AutomationTaskStatus.Stopping && !f.Captures[0].Disposed && pool.Usage.Operations==1,"Stop freed in-flight capture early");
            f.Captures[0].Release.TrySetResult();var result=await capture.WaitAsync(TimeSpan.FromSeconds(8));
            Check(result.Status!=OperationStatus.Success && pool.Usage.Frames==0,"Stop with blocked cancellation republished observation");
            releaseCancel.TrySetResult();await Bound(stop);
            Check(pool.Usage==new ObservationUsage(0,0,0,0,0) && f.Captures[0].Disposed,"late capture cleanup leaked");
        }
        finally{releaseCancel.TrySetResult();f.Captures[0].Release.TrySetResult();if(stop is not null)await Bound(stop);blocked.Dispose();}
    }
    private static async Task ObservationCallerCancelAsync()
    {
        var pool=new ObservationFramePool();await using var f=await Fixture.CreateAsync(script:"function on_tick() rug.sleep(60000) end",vision:true,observationFrames:pool);
        var c=await f.StartAsync();var caller=await f.Scheduler.Gateway.CreateContextAsync("probe","caller-observation",AutomationSource.Mcp);
        OperationRequest Request(AutomationOperation op)=>new(op,JsonSerializer.SerializeToElement(new{}),Guid.NewGuid(),"cancel-observation",OperationTarget.ForRun(c.Identity),DateTimeOffset.UtcNow.AddSeconds(10));
        var prior=(CapturedFrameMetadata)(await f.Scheduler.Gateway.ExecuteAsync(caller,Request(AutomationOperation.Capture))).Value!;
        f.Captures[0].Hold=true;using var source=new CancellationTokenSource();
        var pending=f.Scheduler.Gateway.ExecuteAsync(caller,Request(AutomationOperation.Capture),source.Token);
        await Bound(f.Captures[0].Blocked.Task);
        var entered=Signal();var release=Signal();CancellationTokenRegistration registration=default;Task? cancel=null;
        try
        {
            registration=source.Token.Register(()=>{entered.TrySetResult();release.Task.GetAwaiter().GetResult();});
            cancel=Task.Run(source.Cancel);await Bound(entered.Task);
            f.Captures[0].Release.TrySetResult();var result=await pending.WaitAsync(TimeSpan.FromSeconds(8));
            Check(result.Status==OperationStatus.Cancelled,"original caller cancellation was hidden by blocked propagation");
            release.TrySetResult();await Bound(cancel);
            var observed=(VisionOcrResult)(await f.Scheduler.Gateway.ExecuteAsync(caller,Request(AutomationOperation.Ocr))).Value!;
            Check(observed.Observation.FrameId==prior.Observation.FrameId && pool.Usage is {Frames:1,Operations:0,ReservedBytes:0},"cancelled caller replaced old valid observation");
        }
        finally{release.TrySetResult();f.Captures[0].Release.TrySetResult();if(cancel is not null)await Bound(cancel);registration.Dispose();}
        await f.Scheduler.StopTaskAsync(c.InstanceId);Check(pool.Usage==new ObservationUsage(0,0,0,0,0),"caller cancellation leaked frame pool");
    }
    private static async Task ObservationDeadlineAsync(string root)
    {
        var clock=new ObservationClock();var pool=new ObservationFramePool(new(96,1024),clock);
        var entered=Signal();var release=Signal();
        var capture=new ObservationCapture(async i=>{if(i==2){entered.TrySetResult();await release.Task;}return ObservationPixels();});
        var manifest=new PluginManifest([Permission.Capture,Permission.VisionOcr]){Id="deadline",PluginDirectory=root};
        var run=new RunIdentity(manifest.Id,Guid.NewGuid(),1,null,0);
        var gateway=new AutomationGateway(NullLogger.Instance,getPlugin:(_,_)=>Task.FromResult(manifest),clock:clock);
        await using var runtime=new LuaRuntime(capture,new FakeOcr(),new FakeInput(),NullLogger<LuaRuntime>.Instance,observationFrames:pool);
        runtime.ConfigureGateway(gateway);runtime.ConfigureState(run,new Dictionary<string,object?>(),new Dictionary<string,object?>());
        string script=Path.Combine(root,"deadline.lua");await File.WriteAllTextAsync(script,"function on_tick() end");
        await TestAuthorizationHost.InitializeAsync(runtime,script,manifest);
        var caller=await gateway.CreateContextAsync(manifest.Id,"deadline-observation",AutomationSource.Agent);
        OperationRequest Request(AutomationOperation op)=>new(op,JsonSerializer.SerializeToElement(new{}),Guid.NewGuid(),"observation-deadline",OperationTarget.ForRun(run),clock.Utc.AddSeconds(10));
        var prior=(CapturedFrameMetadata)(await gateway.ExecuteAsync(caller,Request(AutomationOperation.Capture))).Value!;
        var pending=gateway.ExecuteAsync(caller,Request(AutomationOperation.Capture));await Bound(entered.Task);
        // Advance the authoritative deadline clock without running its real timer callback.
        clock.Utc=clock.Utc.AddSeconds(20);clock.Ticks+=20000;release.TrySetResult();var result=await pending.WaitAsync(TimeSpan.FromSeconds(8));
        Check(result is {Status:OperationStatus.TimedOut,Error:OperationError.DeadlineExceeded},"expired observation deadline became success/cancellation");
        var current=(VisionOcrResult)(await gateway.ExecuteAsync(caller,Request(AutomationOperation.Ocr))).Value!;
        Check(current.Observation.FrameId==prior.Observation.FrameId && pool.Usage.Operations==0,"expired observation replaced valid cache/leaked work");
        await runtime.DisposeAsync();Check(pool.Usage==new ObservationUsage(0,0,0,0,0),"deadline observation resources leaked");
    }
    private static async Task ObservationGatewayCapacityAsync()
    {
        var pool=new ObservationFramePool(new(256,512,4,4,2));
        await using var f=await Fixture.CreateAsync(script:"function on_tick() rug.sleep(60000) end",vision:true,observationFrames:pool);
        var c=await f.StartAsync();var caller=await f.Scheduler.Gateway.CreateContextAsync("probe","capacity-observation",AutomationSource.Agent);
        OperationRequest Request(AutomationOperation op)=>new(op,JsonSerializer.SerializeToElement(new{}),Guid.NewGuid(),"capacity-observation",OperationTarget.ForRun(c.Identity),DateTimeOffset.UtcNow.AddSeconds(10));
        var first=(CapturedFrameMetadata)(await f.Scheduler.Gateway.ExecuteAsync(caller,Request(AutomationOperation.Capture))).Value!;
        var request=Request(AutomationOperation.Capture);var full=await f.Scheduler.Gateway.ExecuteAsync(caller,request);
        Check(full is {Status:OperationStatus.Rejected,Error:OperationError.CapacityExceeded} && f.Captures[0].Calls==1 && !c.IsBusinessClosed,
            "shared capacity rejection reached producer or faulted run");
        Check(await f.Scheduler.Gateway.ExecuteAsync(caller,request)==full && f.Captures[0].Calls==1,"replayed capacity rejection recaptured");
        var ocr=(VisionOcrResult)(await f.Scheduler.Gateway.ExecuteAsync(caller,Request(AutomationOperation.Ocr))).Value!;
        Check(ocr.Observation.FrameId==first.Observation.FrameId && pool.Usage is {Frames:1,Operations:0,ReservedBytes:0},"capacity rejection corrupted previous cache");
        await f.Scheduler.StopTaskAsync(c.InstanceId);Check(pool.Usage==new ObservationUsage(0,0,0,0,0),"Gateway capacity cleanup leaked");
    }
}
