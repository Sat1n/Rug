using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

internal static partial class CheckpointTests
{
    private sealed class TraceSink : ITraceFileSink
    {
        internal bool Fail,Hold,AccessDenied,SummaryFail;
        internal readonly TaskCompletionSource Entered=Signal(),Release=Signal();
        private readonly TraceFileSink _real=new();
        public async Task AppendAsync(string path,ReadOnlyMemory<byte> bytes)
        {
            Entered.TrySetResult();if(Hold)await Release.Task;
            if(AccessDenied)throw new UnauthorizedAccessException("simulated access denied");
            if(Fail)throw new IOException("simulated disk full");
            await _real.AppendAsync(path,bytes);
        }
        public Task ReplaceSummaryAsync(string path,ReadOnlyMemory<byte> bytes)=>SummaryFail?
            Task.FromException(new IOException("simulated replace failure")):_real.ReplaceSummaryAsync(path,bytes);
    }
    private sealed class TraceClock : TimeProvider
    {
        internal DateTimeOffset Utc=DateTimeOffset.UtcNow;internal long Ticks=100;
        public override DateTimeOffset GetUtcNow()=>Utc;
        public override long GetTimestamp()=>Interlocked.Increment(ref Ticks);
        public override long TimestampFrequency=>1000;
    }
    private static string TraceRoot()=>Path.Combine(AppContext.BaseDirectory,"trace-test-"+Guid.NewGuid().ToString("N"));
    private static TraceRun RunTrace(RunIdentity run)=>new(run.PluginId,run.InstanceId,run.RunGeneration,run.TargetBindingId);
    private static async Task TraceDrained(ExecutionTraceRecorder trace)=>await Until(()=>!trace.Diagnostics.WriterBusy&&trace.Diagnostics.QueuedEvents==0);
    private static void TraceCheck(bool value,string text)=>Check(value,"trace: "+text);
    public static async Task RunExecutionTracesAsync()
    {
        await TraceGatewayAsync();await TraceLateAsync();await TraceRecorderLimitsAsync();
        await TraceStorageAsync();await TraceFailuresAsync();
        Console.WriteLine("Execution traces PASS: actual Gateway/Lua, dedup, actual-work vs caller, source/validation/release, bounded writer/reader, corruption, quotas and stuck IO isolation");
    }
    private static async Task TraceGatewayAsync()
    {
        string root=TraceRoot();Directory.CreateDirectory(root);
        try
        {
            await using var traces=new ExecutionTraceRecorder(root,new(FlushMilliseconds:2000));
            Guid traceId;Guid session=traces.HostSessionId;RunIdentity identity;
            await using(var f=await ClickFixture.Create(traces:traces))
            {
                identity=f.Run.Identity;traceId=f.Run.Trace!.TraceId;
                var ocrRequest=f.Request(AutomationOperation.Ocr);
                var ocr=(VisionOcrResult)(await f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,ocrRequest)).RequireSuccess()!;
                TraceCheck(traces.Query(traceId,ocrRequest.OperationId).Events.Any(e=>e.Kind==TraceEventKind.Observation&&
                    e.Observation?.FrameId==f.Source.FrameId&&e.Observation.ObservationId==ocr.Observation.ObservationId),"OCR actual frame link missing");
                var request=f.Request(AutomationOperation.ClickObserved,ClickParameters(f.Source));
                var entered=Signal();var release=Signal();
                f.Input.OnMove=async()=>{entered.TrySetResult();await release.Task;};
                var pending=f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,request);await Bound(entered.Task);
                var duplicate=f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,request);
                TraceCheck(!duplicate.IsCompleted,"in-flight dedup did not wait");release.TrySetResult();
                var first=await pending;TraceCheck(await duplicate==first,"in-flight duplicate result changed");
                TraceCheck(first.Status==OperationStatus.Success,"valid click");
                TraceCheck(await f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,request)==first,"dedup result changed");
                var second=await f.Host.StartAsync();TraceCheck(second.Trace!.TraceId!=traceId,"same-plugin run traces mixed");
                var invalid=request with{OperationId=Guid.NewGuid(),Target=request.Target with{RunGeneration=99}};
                TraceCheck((await f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,invalid)).Error==OperationError.StaleRun,"stale generation admitted");
                var limited=await f.Host.Scheduler.Gateway.CreateContextAsync("probe","no-input",AutomationSource.Agent,capabilities:[Permission.Capture]);
                await f.Host.Scheduler.Gateway.ExecuteAsync(limited,request with{OperationId=Guid.NewGuid()});
                var memory=traces.Query(traceId,request.OperationId,limit:256).Events;
                TraceCheck(memory.Count(e=>e.Kind==TraceEventKind.Dispatch)==1&&memory.Count(e=>e.Kind==TraceEventKind.RepeatedRequest)==2&&
                    memory.Select(e=>e.ExecutionId).Distinct().Count()==1,"dedup replay executed");
                TraceCheck(memory.Where(e=>e.Kind==TraceEventKind.Authorized).All(e=>e.Authorization is {Decision:"allowed",Revision:>0}),"historical grant revision unavailable");
                TraceCheck(memory.Any(e=>e.Observation?.FrameId==f.Source.FrameId&&e.Kind==TraceEventKind.CallerReturned)&&
                    memory.Any(e=>e.Kind==TraceEventKind.Validation&&e.Reason=="after-move:roi-passed"&&e.Observation!.ValidationFrames!.Count==2),"missing actual source/validation");
                TraceCheck(memory.Any(e=>e.Kind==TraceEventKind.InputSideEffect)&&f.Input.MouseDownCalls==1&&f.Input.MouseUpCalls==1,"missing input boundary");
                var rejected=traces.Query(traces.Control.TraceId,limit:256).Events;
                TraceCheck(rejected.Any(e=>e.Error=="StaleRun"&&e.Run is null&&e.RequestedTarget?.RunGeneration==99)&&
                    rejected.Any(e=>e.Authorization?.Decision=="denied"),"unresolved target/history misrepresented");
                var all=traces.Query(traceId,limit:256).Events;
                TraceCheck(all.All(e=>e.Run==RunTrace(identity))&&all.Select(e=>e.Sequence).Distinct().Count()==all.Count,"identity/sequence");
                TraceCheck(all is not ExecutionTraceEvent[],"mutable event list");
                await f.Host.Scheduler.StopTaskAsync(second.InstanceId);
                await f.Host.Scheduler.StopTaskAsync(f.Run.InstanceId);
            }
            await traces.DisposeAsync();
            var store=new TraceFileStore(root);
            var page=store.Read(session,traceId,limit:256);
            TraceCheck(page.Status==TraceReadStatus.Valid&&page.Summary is {RunCleanupCompleted:true,ActualWorkOutstanding:0},"offline complete: "+page.Status+" "+page.Diagnostic);
            var one=store.Read(session,traceId,limit:1);TraceCheck(one.Events.Count==1&&one.NextSequence==one.Events[0].Sequence,"bounded pagination");
            var next=store.Read(session,traceId,afterSequence:one.NextSequence!.Value,limit:1);TraceCheck(next.Events[0].Sequence>one.Events[0].Sequence,"page cursor");
            TraceCheck(!Directory.GetFiles(root).Any(p=>File.ReadAllText(p).Contains("frameReference",StringComparison.OrdinalIgnoreCase)),"opaque payload leaked");
            var process=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))process.ArgumentList.Add(typeof(CheckpointTests).Assembly.Location);
            foreach(string arg in new[]{"--execution-trace-read",root,session.ToString(),traceId.ToString()})process.ArgumentList.Add(arg);
            using var child=Process.Start(process)!;await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            TraceCheck(child.ExitCode==0,"child offline reader: "+await child.StandardError.ReadToEndAsync());
            Console.WriteLine((await child.StandardOutput.ReadToEndAsync()).Trim());
        }
        finally{Directory.Delete(root,true);}
    }
    public static void TraceReadChild(string[] args)
    {
        var page=new TraceFileStore(args[1]).Read(Guid.Parse(args[2]),Guid.Parse(args[3]),limit:256);
        TraceCheck(page.Status==TraceReadStatus.Valid&&page.Events.Any(e=>e.Kind==TraceEventKind.CleanupCompleted),"restarted offline read");
        Console.WriteLine("Trace subprocess offline read PASS; no task/authorization/window restored");
    }
    private static async Task TraceLateAsync()
    {
        foreach(string ending in new[]{"cancel","late-failure","timeout"})
        {
            bool fail=ending=="late-failure";
            await using var traces=new ExecutionTraceRecorder();await using var f=await ClickFixture.Create(traces:traces);
            var entered=Signal();var release=Signal();using var cancel=new CancellationTokenSource();
            f.Host.Captures[0].Next=async()=>{entered.TrySetResult();await release.Task;if(fail)throw new IOException("secret-should-not-be-recorded");return f.Clock.Frame();};
            var request=f.Request(AutomationOperation.Capture);
            if(ending=="timeout")request=request with{Deadline=DateTimeOffset.UtcNow.AddSeconds(1)};
            var pending=f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,request,cancel.Token);
            await Bound(entered.Task);if(ending!="timeout")cancel.Cancel();var returned=await pending;
            TraceCheck(returned.Status==(ending=="timeout"?OperationStatus.TimedOut:OperationStatus.Cancelled),"caller cancellation/deadline");
            var rows=traces.Query(f.Run.Trace!.TraceId,request.OperationId).Events;
            TraceCheck(rows.Any(e=>e.Kind==TraceEventKind.CallerReturned)&&rows.All(e=>e.Kind!=TraceEventKind.WorkCompleted),"caller vs actual work conflated");
            var stop=f.Host.Scheduler.StopTaskAsync(f.Run.InstanceId);
            TraceCheck(!stop.IsCompleted&&!f.Host.Captures[0].Disposed,"actual work released before completion");
            release.TrySetResult();await Bound(stop);await Until(()=>f.Host.Scheduler.Gateway.ActiveWorkCount==0);
            rows=traces.Query(f.Run.Trace.TraceId,request.OperationId).Events;
            TraceCheck(rows.Any(e=>e.Kind==TraceEventKind.WorkCompleted&&e.Sequence>rows.Single(r=>r.Kind==TraceEventKind.CallerReturned).Sequence&&
                (fail?e.Error=="IOException":true)),"late work/failure missing");
            TraceCheck(rows.All(e=>e.Reason!="secret-should-not-be-recorded"),"exception text leaked");
        }
        foreach(string cause in new[]{"takeover","revoke","release"})
        {
            await using var traces=new ExecutionTraceRecorder();await using var f=await ClickFixture.Create(traces:traces);
            if(cause=="release")
            {
                f.Input.OnUp=()=>throw new IOException("secret-release");await f.Click();
                TraceCheck(traces.Query(f.Run.Trace!.TraceId,limit:256).Events.Any(e=>e.Kind==TraceEventKind.ReleaseFailed&&e.Error=="IOException"),"mandatory release failure missing");
            }
            else
            {
                if(cause=="takeover")await f.Host.Arbiter.TakeOverAsync();
                else f.Host.Authorization.Revoke(f.Run.RecoveryAuthorization!.Installation,[Permission.ControlInput]);
                await Bound(f.Run.Completion);
                TraceCheck(traces.Query(f.Run.Trace!.TraceId,limit:256).Events.Any(e=>e.Kind==TraceEventKind.CleanupCompleted&&
                    e.Reason==(cause=="revoke"?"AuthorizationRevoked":"ManualTakeover")),"stop cause missing: "+cause);
            }
        }
    }
    private static async Task TraceRecorderLimitsAsync()
    {
        var clock=new TraceClock();await using var recorder=new ExecutionTraceRecorder(limits:new(MemoryEvents:8,MemoryBytes:65536,MaxTraces:3),clock:clock);
        var run=new RunIdentity("one",Guid.NewGuid(),1,Guid.NewGuid(),123);
        var origin=new CheckpointOrigin(Guid.NewGuid(),Guid.NewGuid(),3,"ready");
        var first=recorder.OpenRun(run,origin,new(Guid.NewGuid(),"trusted-rule","fingerprint","v1","v2"));var second=recorder.OpenRun(new("two",Guid.NewGuid(),2,Guid.NewGuid(),123));
        TraceCheck(first.Origin?.ParentInstanceId==origin.ParentInstanceId&&first.Origin.UpdateId is not null,"new-run origin link missing");
        TraceCheck(recorder.OpenRun(run).TraceId==Guid.Empty,"active trace capacity ignored");
        first.Emit(TraceEventKind.RunCreated);clock.Utc=clock.Utc.AddDays(-1);first.Emit(TraceEventKind.Lifecycle);
        var times=recorder.Query(first.TraceId).Events;TraceCheck(times[1].Utc<times[0].Utc&&times[1].MonotonicTicks>times[0].MonotonicTicks,"UTC changed monotonic order");
        await Task.WhenAll(Enumerable.Range(0,4).Select(_=>Task.Run(()=>{for(int i=0;i<100;i++)second.Emit(TraceEventKind.Lifecycle,reason:new string('x',10000));})));
        TraceCheck(recorder.Diagnostics.RetainedEvents==8&&recorder.Diagnostics.RetainedBytes<=65536,"memory unbounded");
        var all=recorder.Query(second.TraceId).Events;TraceCheck(all.Select(e=>e.Sequence).Distinct().Count()==all.Count&&all.All(e=>e.Reason!.Length==192),"text/sequence bounds");
        await using var restarted=new ExecutionTraceRecorder();TraceCheck(restarted.HostSessionId!=recorder.HostSessionId,"restart session reused");
        await using var disabled=ExecutionTraceRecorder.Disabled();disabled.Control.Emit(TraceEventKind.Dispatch);TraceCheck(disabled.Diagnostics.RetainedEvents==0,"disabled records");
    }
    private static async Task TraceFailuresAsync()
    {
        foreach(string failure in new[]{"io","access","summary","stuck"})
        {
            bool stuck=failure=="stuck";
            string root=TraceRoot();var sink=new TraceSink{Hold=stuck,Fail=failure=="io",AccessDenied=failure=="access",SummaryFail=failure=="summary"};
            var recorder=new ExecutionTraceRecorder(root,new(QueueEvents:4,QueueBytes:32768,MemoryEvents:16,FlushMilliseconds:50),sink:sink);
            try
            {
                await using(var f=await ClickFixture.Create(traces:recorder))
                {
                    await Bound(sink.Entered.Task);
                    for(int i=0;i<40;i++)f.Run.Trace!.Emit(TraceEventKind.Observation);
                    f.Run.Trace!.Emit(TraceEventKind.CleanupFailed,error:"SyntheticTraceOnly");
                    TraceCheck((await f.Click()).Status==OperationStatus.Success,"trace fault changed input decision");
                    await Bound(f.Host.Scheduler.StopTaskAsync(f.Run.InstanceId));
                    TraceCheck(f.Host.Captures[0].Disposed&&f.Pool.Usage.Operations==0,"writer retained run resources");
                }
                TraceCheck(recorder.Diagnostics.QueuedEvents<=4&&recorder.Diagnostics.RetainedEvents<=16,"queue unbounded");
                if(stuck)TraceCheck(recorder.Diagnostics.FlushIncomplete&&recorder.Diagnostics.DroppedEvents>0,"stuck flush/drop invisible");
                else TraceCheck(recorder.Diagnostics.WriteFailures>0,"IO failure invisible");
            }
            finally
            {
                sink.Release.TrySetResult();await recorder.DisposeAsync();
                await Until(()=>!recorder.Diagnostics.WriterBusy&&recorder.Diagnostics.QueuedEvents==0);
                // Allow final writer summaries/seal to finish before removing the test-owned directory.
                await recorder.WriterCompletion.WaitAsync(TimeSpan.FromSeconds(8));
                if(stuck)
                {
                    var pages=new TraceFileStore(root).Discover();
                    TraceCheck(pages.Entries.Any(e=>e.Status==TraceReadStatus.Incomplete),"dropped trace declared complete");
                }
                if(Directory.Exists(root))Directory.Delete(root,true);
            }
        }
    }
    private static async Task TraceStorageAsync()
    {
        string root=TraceRoot();Directory.CreateDirectory(root);
        try
        {
            Guid session,trace;
            await using(var recorder=new ExecutionTraceRecorder(root,new(FlushMilliseconds:2000)))
            {session=recorder.HostSessionId;trace=recorder.Control.TraceId;recorder.Control.Emit(TraceEventKind.Request);}
            string path=Directory.GetFiles(root,"*.events").Single();byte[] original=await File.ReadAllBytesAsync(path);
            var store=new TraceFileStore(root);TraceCheck(store.Read(session,trace).Status==TraceReadStatus.Valid,"valid control trace");
            await File.AppendAllTextAsync(path,"{half");TraceCheck(store.Read(session,trace).Status==TraceReadStatus.Incomplete,"half line accepted");
            await File.WriteAllBytesAsync(path,original);
            await File.WriteAllTextAsync(path+".irrelevant.tmp","not ours");
            string summary=Path.ChangeExtension(path,".summary");await File.WriteAllTextAsync(summary+".tmp","half");
            TraceCheck(store.Read(session,trace).Status==TraceReadStatus.Incomplete,"temporary summary ignored");File.Delete(summary+".tmp");
            var changed=(byte[])original.Clone();changed[30]^=1;await File.WriteAllBytesAsync(path,changed);
            TraceCheck(store.Read(session,trace).Status==TraceReadStatus.Corrupt,"middle corruption accepted");
            var line=Encoding.UTF8.GetString(original).Split('\n')[0];using var doc=JsonDocument.Parse(line);
            var node=System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetProperty("payload").GetRawText())!;node["schemaVersion"]=99;
            var payload=JsonSerializer.SerializeToElement(node);string checksum=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{checksum,payload})+"\n");
            TraceCheck(store.Read(session,trace).Status==TraceReadStatus.UnsupportedFormat,"unknown version accepted");
            await File.WriteAllBytesAsync(path,original);
            TraceCheck(store.Read(session,trace,limit:999).Status==TraceReadStatus.CapacityExceeded,"reader unbounded");
            await using(var limited=new ExecutionTraceRecorder(Path.Combine(root,"limit"),new(MaxFileBytes:8192,MaxDiskBytes:16384,MaxFiles:4,FlushMilliseconds:2000)))
            {for(int i=0;i<100;i++)limited.Control.Emit(TraceEventKind.Request);await TraceDrained(limited);TraceCheck(limited.Diagnostics.WriteFailures>0,"file quota ignored");}
            TraceCheck(Directory.GetFiles(Path.Combine(root,"limit"),"*.events").All(f=>new FileInfo(f).Length<=8192),"oversize event file");
            // Retention applies only to exact component names; unrelated files survive.
            File.SetLastWriteTimeUtc(summary,DateTime.UtcNow.AddDays(-8));
            await using(var fresh=new ExecutionTraceRecorder(root,new(FlushMilliseconds:2000)))fresh.Control.Emit(TraceEventKind.Request);
            TraceCheck(!File.Exists(path)&&File.Exists(path+".irrelevant.tmp"),"retention touched foreign data or retained expired trace");
        }
        finally{Directory.Delete(root,true);}
    }
}
