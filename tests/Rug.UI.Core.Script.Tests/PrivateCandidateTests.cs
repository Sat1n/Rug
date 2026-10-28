using System.Text.Json;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

internal static partial class CheckpointTests
{
    private static CapturedFrame BadCandidate(ClickFixture f,long? rendered=null,bool managed=false)
    {
        var frame=f.Clock.Frame();long time=rendered??f.Clock.Now+100;
        return frame with {CaptureFacts=frame.CaptureFacts! with{NativeTime=managed?new(time,10_000_000,f.Clock.Now):null},
            NativeCaptureFacts=new(){SystemRelative100ns=time,QpcSample=f.Clock.Now,QpcFrequency=10_000_000,Flags=4}};
    }
    /// <summary>@shape cases: bounded Gateway runs; trusted pixels/time, counted fake input and drained resources.
    /// @source transaction: Rug.UI.Core/Services/ObservedClickServices.cs#function:VerifyAsync</summary>
    public static async Task RunPrivateCandidatesAsync()
    {
        foreach(var mode in new[]{InputDeliveryMode.Win32PostMessage,InputDeliveryMode.Win32SendInput})
        foreach(int bad in new[]{0,1,2})await CandidateSuccessAsync(mode,bad);
        foreach(string failure in new[]{"three","same","rewrapped","managed-invalid","scene","geometry","none","io","old","candidate-frequency","candidate-domain","host-frequency","host-domain"})
            await CandidateRefusalAsync(failure);
        foreach(int timeout in new[]{100,750,2000})await CandidateBudgetAsync(timeout);
        await CandidateSourceAgeAsync();
        foreach(bool afterMove in new[]{false,true})
        foreach(string action in new[]{"stop","revoke","takeover","cancel","cancel-blocked","timeout"})await CandidateLateAsync(action,afterMove);
        await CandidateIsolationAsync();await CandidateTraceFailureAsync();
        Console.WriteLine("Private candidates PASS: actual Gateway/transaction, <=3 per phase, shared <=750ms, strict newer/native validity, terminal ROI/geometry/clock/errors, original source/authority/cancellation, late actual work, bounded pool and diagnostic failure isolation");
    }
    private static async Task CandidateSuccessAsync(InputDeliveryMode mode,int bad)
    {
        await using var trace=new ExecutionTraceRecorder();
        await using var f=await ClickFixture.Create(traces:trace);
        int phases=mode==InputDeliveryMode.Win32SendInput?3:2,calls=0;
        f.Host.Captures[0].Next=()=>
        {
            int ordinal=++calls;f.Clock.Now+=1000;
            // Each phase discards exactly bad candidates before accepting a newer real-shaped frame.
            return Task.FromResult<CapturedFrame?>(ordinal%(bad+1)==0?f.Clock.Frame():BadCandidate(f));
        };
        var result=await f.Click(mode);
        Check(result.Status==OperationStatus.Success&&result.Value is ObservedClickResult r&&r.FrameId==f.Source.FrameId&&r.ObservationId==f.Source.ObservationId&&
            r.ValidationFrames!.Count==phases&&calls==phases*(bad+1),"candidate success lost identity/count or replayed phase");
        Check(f.Input.MoveCalls==1&&f.Input.MouseDownCalls==1&&f.Input.MouseUpCalls==1&&f.Input.X==32&&f.Input.Y==32,"candidate selection replayed/moved input");
        Check(f.Pool.ActionUsage.RegisteredObservations==1&&f.Pool.Usage.Frames==1&&f.Pool.Usage.Operations==0,"private candidate replaced public cache or retained pool work");
        var events=trace.Query(f.Run.Trace!.TraceId,limit:256).Events;
        var ends=events.Where(e=>e.Observation?.Candidate?.Event=="StageEnd").ToArray();
        Check(ends.Length==phases&&ends.All(e=>e.Observation!.Candidate is { } c&&c.Number==bad+1&&c.Reason==(bad==0?"AcceptedFirst":"AcceptedAfterDiscard")),"phase completion diagnostics incorrect");
        Check(events.Count(e=>e.Observation?.Candidate?.Event=="Discarded")==bad*phases,"candidate discard evidence missing");
    }
    private static async Task CandidateRefusalAsync(string failure)
    {
        await using var trace=new ExecutionTraceRecorder();await using var f=await ClickFixture.Create(traces:trace);
        int calls=0;long discarded=f.Clock.Now+100;
        f.Host.Captures[0].Next=()=>
        {
            calls++;
            if(failure=="three")return Task.FromResult<CapturedFrame?>(BadCandidate(f));
            if(failure=="same"){f.Clock.Now+=1000;return Task.FromResult<CapturedFrame?>(BadCandidate(f,discarded));}
            if(failure=="managed-invalid"){f.Clock.Now+=1000;return Task.FromResult<CapturedFrame?>(BadCandidate(f,f.Clock.Now,managed:true));}
            if(calls==1){var first=BadCandidate(f,discarded);if(failure=="old")f.Clock.Now+=1000;return Task.FromResult<CapturedFrame?>(first);}
            f.Clock.Now+=1000;var frame=f.Clock.Frame();
            if(failure=="rewrapped")
            {
                if(calls==2)return Task.FromResult<CapturedFrame?>(frame with{CaptureFacts=frame.CaptureFacts! with{NativeTime=new(discarded,10_000_000,f.Clock.Now)}});
                return Task.FromResult<CapturedFrame?>(frame); // Strictly newer third candidate, then normal next phase.
            }
            if(failure=="scene")frame=f.Clock.Frame(200);
            if(failure=="geometry")frame=frame with{CaptureFacts=frame.CaptureFacts! with{Geometry=new(ClientGeometryVerified:false)}};
            if(failure=="none")return Task.FromResult<CapturedFrame?>(null);
            if(failure=="io")throw new IOException("real-shaped capture failure");
            if(failure=="old")frame=frame with{CaptureFacts=frame.CaptureFacts! with{NativeTime=new(100_000_000,10_000_000,f.Clock.Now)}};
            if(failure=="candidate-frequency")frame=frame with{CaptureFacts=frame.CaptureFacts! with{NativeTime=new(f.Clock.Now,1,f.Clock.Now)}};
            if(failure=="candidate-domain")frame=frame with{CaptureFacts=frame.CaptureFacts! with{NativeTime=new(f.Clock.Now,10_000_000,f.Clock.Now,"foreign")}};
            if(failure=="host-frequency")f.Clock.Reading=()=>new(f.Clock.Now,1);
            if(failure=="host-domain")f.Clock.Reading=()=>new(f.Clock.Now,10_000_000,"foreign");
            return Task.FromResult<CapturedFrame?>(frame);
        };
        var result=await f.Click();
        if(failure=="rewrapped")
        {Check(result.Status==OperationStatus.Success&&calls==4&&f.Input.MouseDownCalls==1,"same timestamp was accepted or newer candidate failed");return;}
        if(failure=="io")Check(result.Status==OperationStatus.Failed&&result.Error==OperationError.ExecutionFailed,"service exception became generic retry");
        else ClickRejected(result,failure switch{"scene"=>OperationError.ObservationSceneChanged,"geometry"=>OperationError.ObservationGeometryChanged,
            "none" or "old"=>OperationError.ObservationValidationUnavailable,_=>OperationError.ObservationTimeUnavailable});
        Check(calls==(failure is "three" or "same" or "managed-invalid"?3:2)&&f.Input.MoveCalls==0&&f.Input.MouseDownCalls==0,"terminal rejection retried or emitted input");
        if(result.Value is ObservedClickResult details)Check(details.ValidationFrames?.Count==0,"rejected candidate minted successful private frame");
        Check(trace.Query(f.Run.Trace!.TraceId,limit:256).Events.Any(e=>e.Observation?.Candidate?.Event=="StageEnd"),"stage rejection omitted ending");
    }
    private static async Task CandidateBudgetAsync(int timeout)
    {
        await using var trace=new ExecutionTraceRecorder();await using var f=await ClickFixture.Create(timeout:timeout,traces:trace);
        int calls=0;f.Host.Captures[0].Next=()=>
        {calls++;f.Clock.Now+=timeout==100?600_000:timeout==2000?8_000_000:3_000_000;return Task.FromResult<CapturedFrame?>(BadCandidate(f));};
        ClickRejected(await f.Click(),OperationError.ObservationValidationUnavailable);
        Check(calls==(timeout==100?2:timeout==2000?1:3)&&f.Input.MouseDownCalls==0,"phase deadline reset or host ceiling enlarged");
        Check(trace.Query(f.Run.Trace!.TraceId,limit:256).Events.Any(e=>e.Observation?.Candidate?.Reason=="ValidationTimedOut"),"timeout ending missing");
    }
    private static async Task CandidateSourceAgeAsync()
    {
        await using var f=await ClickFixture.Create();int calls=0;
        f.Host.Captures[0].Next=()=>{calls++;f.Clock.Now+=1_500_000;return Task.FromResult<CapturedFrame?>(BadCandidate(f));};
        var request=JsonSerializer.SerializeToNode(ClickParameters(f.Source))!;request["maxAgeMs"]=100;
        ClickRejected(await f.Send(AutomationOperation.ClickObserved,request),OperationError.ObservationStale);
        Check(calls==1&&f.Input.MoveCalls==0,"caller tightened source age ignored during acquisition");
    }
    private static async Task CandidateLateAsync(string action,bool afterMove)
    {
        await using var f=await ClickFixture.Create(timeout:action=="timeout"?30:750);
        var entered=Signal();var release=Signal();bool moved=false;int calls=0;using var caller=new CancellationTokenSource();
        f.Input.OnMove=()=>{moved=true;return Task.CompletedTask;};
        f.Host.Captures[0].Next=async()=>
        {
            calls++;
            if(afterMove&&!moved)return f.Clock.Frame();
            if(calls==(afterMove?2:1))return BadCandidate(f);
            entered.TrySetResult();await release.Task;f.Clock.Now+=1000;return f.Clock.Frame();
        };
        var pending=f.Click(token:caller.Token);await Bound(entered.Task);Task? stop=null;
        var cancellationEntered=Signal();using var cancellationRelease=new ManualResetEventSlim();Task? cancelling=null;
        CancellationTokenRegistration blockedCancellation=default;
        try
        {
            if(action=="stop")stop=f.Host.Scheduler.StopTaskAsync(f.Run.InstanceId);
            if(action=="revoke")f.Host.Authorization.Revoke(f.Run.RecoveryAuthorization!.Installation,[Permission.ControlInput]);
            if(action=="takeover")stop=f.Host.Arbiter.TakeOverAsync();
            if(action=="cancel")caller.Cancel();
            if(action=="cancel-blocked")
            {blockedCancellation=caller.Token.Register(()=>{cancellationEntered.TrySetResult();cancellationRelease.Wait();});
                cancelling=Task.Run(caller.Cancel);await Bound(cancellationEntered.Task);Check(caller.IsCancellationRequested,"original cancellation was not requested");}
            if(action=="timeout")
            {var timed=Signal();using var subscription=f.Host.Captures[0].LastToken.Register(()=>timed.TrySetResult());await Bound(timed.Task);}
            if(action is "stop" or "revoke" or "takeover")await Until(()=>f.Run.IsBusinessClosed);
            Check(!f.Host.Captures[0].Disposed&&f.Pool.Usage.Operations>0,"cancel/timeout abandoned actual capture resources");
            if(action=="timeout")Check(!pending.IsCompleted,"non-cooperative capture was abandoned at timeout");
            release.TrySetResult();var result=await pending;await Until(()=>f.Pool.Usage.Operations==0);
            if(stop is not null)await Bound(stop);
            Check(result.Status!=OperationStatus.Success&&f.Input.MoveCalls==(afterMove?1:0)&&f.Input.MouseDownCalls==0&&result.OutcomeUncertain==afterMove,
                "late result emitted/replayed input or lost uncertainty");
            Check(calls==(afterMove?3:2),"late result triggered another candidate");
            if(action=="timeout")ClickRejected(result,OperationError.ObservationValidationUnavailable,afterMove);
            Console.WriteLine($"Candidate late {action}/afterMove={afterMove}: calls={calls}, move/down/up={f.Input.MoveCalls}/{f.Input.MouseDownCalls}/{f.Input.MouseUpCalls}, uncertain={result.OutcomeUncertain}");
        }
        finally{release.TrySetResult();cancellationRelease.Set();if(cancelling is not null)await Bound(cancelling);blockedCancellation.Dispose();}
    }
    private static async Task CandidateIsolationAsync()
    {
        await using var f=await ClickFixture.Create(new(65536,196608));
        var second=await f.Host.StartAsync();await Until(()=>second.TaskStatus==AutomationTaskStatus.Running);
        OperationRequest Request(AutomationOperation op,object value)=>f.Request(op,value) with{Target=OperationTarget.ForRun(second.Identity)};
        async Task<ObservationEnvelope> ObserveSecond()=>((CapturedFrameMetadata)(await f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,Request(AutomationOperation.Capture,new{}))).RequireSuccess()!).Observation;
        var secondSource=await ObserveSecond();
        ClickRejected(await f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,Request(AutomationOperation.ClickObserved,ClickParameters(f.Source))),OperationError.ObservationInvalid);
        using(var pressure=f.Pool.Open(new("capacity-pressure",Guid.NewGuid(),1,null,0)))
        using(var held=f.Pool.Capture(pressure,privateFrame:true))
        {int before=f.Host.Captures[0].Calls;ClickRejected(await f.Click(),OperationError.ObservationValidationUnavailable);Check(f.Host.Captures[0].Calls==before,"capacity rejection called capture");}
        int calls=0;f.Host.Captures[0].Next=()=>
        {calls++;f.Clock.Now+=1000;Check(f.Pool.Usage.ReservedBytes==131072,"discard reservation accumulated");return Task.FromResult<CapturedFrame?>(calls==1?BadCandidate(f):f.Clock.Frame());};
        Check((await f.Click()).Status==OperationStatus.Success,"released capacity did not permit original source");
        secondSource=await ObserveSecond();
        Check((await f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,Request(AutomationOperation.ClickObserved,ClickParameters(secondSource)))).Status==OperationStatus.Success,
            "second instance did not use its own source");
        Check(f.Host.Inputs.All(i=>i.MouseDownCalls==1&&i.MouseUpCalls==1),"instance actions crossed identity");
    }
    private static async Task CandidateTraceFailureAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"rug-candidate-trace-"+Guid.NewGuid().ToString("N"));var sink=new TraceSink{Fail=true};
        try
        {
            await using var trace=new ExecutionTraceRecorder(root,sink:sink);await using var f=await ClickFixture.Create(traces:trace);
            await Bound(sink.Entered.Task);await Until(()=>trace.Diagnostics.WriteFailures>0);
            int calls=0;f.Host.Captures[0].Next=()=>{calls++;f.Clock.Now+=1000;return Task.FromResult<CapturedFrame?>(calls==1?BadCandidate(f):f.Clock.Frame());};
            Check((await f.Click()).Status==OperationStatus.Success&&calls==3&&f.Input.MouseDownCalls==1,"writer failure changed candidate decision");
            await f.Host.Scheduler.DisposeAsync();await Bound(trace.WriterCompletion);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
