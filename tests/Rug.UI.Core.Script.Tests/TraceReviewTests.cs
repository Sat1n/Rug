using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

internal static partial class CheckpointTests
{
    private sealed class ReviewReader : ITraceReviewReader
    {
        internal readonly TaskCompletionSource Entered=Signal(),Release=Signal();
        internal readonly ConcurrentQueue<TraceReviewRequest> Calls=new();
        internal bool Hold=true;internal int Active,Maximum;
        public TraceReviewResult Read(TraceReviewRequest request)
        {
            int active=Interlocked.Increment(ref Active);Maximum=Math.Max(Maximum,active);Calls.Enqueue(request);
            try{Entered.TrySetResult();if(Hold)Release.Task.GetAwaiter().GetResult();return new(request,null);}
            finally{Interlocked.Decrement(ref Active);}
        }
    }
    public static async Task RunTraceReviewAsync()
    {
        await ReviewBrokerAsync();await IncidentArchiveAsync();await IncidentFormatsAsync();
        await IncidentTraceFaultsAsync();await ReviewCatalogAsync();
        Console.WriteLine("Trace review PASS: bounded latest-only background reads/closed views, live+history identities, real PNG/JSON boundary links, recovery preservation, legacy/unknown/corrupt and recorder fault isolation");
    }
    private static async Task ReviewBrokerAsync()
    {
        var reader=new ReviewReader();using var broker=new TraceReviewService(reader);var ui=new ConcurrentQueue<Action>();
        var view=broker.Open(action=>ui.Enqueue(action));var results=new List<TraceReviewResult>();view.Completed+=results.Add;
        var first=new TraceReviewRequest(TraceReviewKind.Memory,TraceId:Guid.NewGuid());view.Submit(first);await Bound(reader.Entered.Task);
        for(int i=0;i<500;i++)view.Submit(new(TraceReviewKind.Memory,TraceId:Guid.NewGuid()));
        var last=new TraceReviewRequest(TraceReviewKind.Memory,TraceId:Guid.NewGuid());view.Submit(last);
        Check(broker.Usage is (1,true,1)&&reader.Calls.Count==1,"review queued unbounded/parallel physical reads");
        reader.Release.TrySetResult();await Until(()=>!broker.Usage.Reading);
        while(ui.TryDequeue(out var deliver))deliver();
        Check(reader.Maximum==1&&reader.Calls.Count==2&&results.Count==1&&results[0].Request==last,"stale query replaced current selection");
        reader.Hold=false;view.Submit(first);await Until(()=>!broker.Usage.Reading&&ui.Count>0);
        view.Submit(last);await Until(()=>!broker.Usage.Reading);
        Check(ui.Count==1,"UI dispatch results accumulated without a bound");
        while(ui.TryDequeue(out var deliver))deliver();
        Check(results.Count==2&&results[^1].Request==last,"already-dispatched stale UI callback applied");
        view.Dispose();Check(broker.Usage.Sessions==0,"closed viewer subscription retained");
        var stuck=new ReviewReader();using var stuckBroker=new TraceReviewService(stuck);
        var active=stuckBroker.Open(_=>throw new Exception("closed page callback"));active.Submit(first);await Bound(stuck.Entered.Task);
        active.Dispose();
        for(int i=0;i<100;i++){using var reopened=stuckBroker.Open(a=>ui.Enqueue(a));reopened.Submit(last);}
        Check(stuckBroker.Usage is (0,true,0)&&stuck.Calls.Count==1,"reopening spawned workers behind hung IO");
        stuckBroker.Dispose();Check(stuckBroker.Usage.Sessions==0,"close waited for physical IO");
        stuck.Release.TrySetResult();await Until(()=>!stuckBroker.Usage.Reading);
    }
    private static async Task IncidentArchiveAsync()
    {
        string root=TraceRoot();Directory.CreateDirectory(root);
        try
        {
            await using var traces=new ExecutionTraceRecorder(Path.Combine(root,"traces"),new(FlushMilliseconds:2000));
            await using var f=await ClickFixture.Create(traces:traces);
            var logger=new AnomalyLogger(NullLogger<AnomalyLogger>.Instance,Path.Combine(root,"incidents"));
            var store=new IncidentStore(logger.DirectoryPath);
            var entered=Signal();var release=Signal();bool archiving=false;string? path=null;TraceOperation? current=null;
            f.Host.Captures[0].Next=async()=>{if(archiving){entered.TrySetResult();await release.Task;}return f.Clock.Frame();};
            f.Input.OnMove=async()=>
            {
                current=ExecutionTraceScope.Current.Value;archiving=true;
                try{path=await logger.LogAnomalyAsync(f.Run,"controlled incident","bounded context");}
                finally{archiving=false;}
            };
            var request=f.Request(AutomationOperation.ClickObserved,ClickParameters(f.Source));var pending=f.Host.Scheduler.Gateway.ExecuteAsync(f.Caller,request);
            await Bound(entered.Task);
            // Mutating later operation metadata while screenshot IO is held must not relabel the boundary.
            current!.Emit(TraceEventKind.Observation,observation:new(Guid.NewGuid(),Guid.NewGuid()));
            release.TrySetResult();Check((await pending).Status==OperationStatus.Success,"incident archive changed click decision");
            var incident=store.Read(Path.GetFileName(path!));var record=incident.Record!;
            Check(incident.Status==IncidentReadStatus.Valid&&record.SchemaVersion==1&&record.IncidentId!=Guid.Empty&&
                record.TraceLink is {Status:"AssociatedInMemory",SequenceUpperBound:>0} link&&link.HostSessionId==traces.HostSessionId&&
                link.TraceId==f.Run.Trace!.TraceId&&link.OperationId==request.OperationId&&link.ExecutionId==current.ExecutionId&&
                link.ObservationId==f.Source.ObservationId&&link.FrameId==f.Source.FrameId,"incident boundary/source identity changed after IO");
            Check(record.ScreenshotCaptureKind=="independent-anomaly-capture"&&incident.ScreenshotReference==Path.GetFileNameWithoutExtension(path)+".png"&&
                (await File.ReadAllBytesAsync(Path.ChangeExtension(path!,".png"))).Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"real PNG reference missing");
            var noOperation=store.Read(Path.GetFileName(await logger.LogAnomalyAsync(f.Run,"without operation","no guessed last operation")));
            Check(noOperation.Record!.TraceLink!.OperationId is null&&noOperation.Record.TraceLink.FrameId is null,"last operation guessed outside execution scope");
            var recovery=f.Run.BeginRecovery("controlled archive");
            string recovering=await logger.LogAnomalyAsync(f.Run,"recovery record","context retained");
            var before=JsonNode.Parse(await File.ReadAllTextAsync(recovering))!;
            before["agent_resolution"]=new JsonObject{["note"]="retained"};await File.WriteAllTextAsync(recovering,before.ToJsonString());
            await logger.UpdateRecoveryAsync(recovering,new(recovery.RecoveryId,1,"rejected","ValidationRejected","red",DateTime.UtcNow));
            var after=JsonNode.Parse(await File.ReadAllTextAsync(recovering))!;
            foreach(string key in new[]{"schemaVersion","incidentId","traceLink","screenshotPath","screenshotCaptureKind","reason","scriptContext","agent_resolution"})
                Check(JsonNode.DeepEquals(before[key],after[key]),"recovery update changed incident field: "+key);
            Check(after["verification"] is not null,"recovery result not saved");
            f.Host.Captures[0].Next=()=>throw new IOException("real capture failure");
            try{await logger.LogAnomalyAsync(f.Run,"failure","context");throw new Exception("capture failure swallowed");}catch(IOException){}
            f.Host.Captures[0].Next=()=>Task.FromResult<CapturedFrame?>(new(new byte[1],10,10,40));
            try{await logger.LogAnomalyAsync(f.Run,"failure","context");throw new Exception("encode failure swallowed");}catch(ArgumentException){}
            f.Host.Captures[0].Next=()=>Task.FromResult<CapturedFrame?>(f.Clock.Frame());
            string occupied=Path.Combine(root,"occupied");await File.WriteAllTextAsync(occupied,"file");
            try{await new AnomalyLogger(NullLogger<AnomalyLogger>.Instance,occupied).LogAnomalyAsync(f.Run,"failure","context");throw new Exception("archive IO failure swallowed");}catch(IOException){}
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task IncidentFormatsAsync()
    {
        string root=TraceRoot();Directory.CreateDirectory(root);
        try
        {
            var logger=new AnomalyLogger(NullLogger<AnomalyLogger>.Instance,root);var store=new IncidentStore(root);var recoveryId=Guid.NewGuid();
            var legacy=new{timestamp=DateTime.UtcNow,pluginId="legacy",instanceId=Guid.NewGuid(),reason="old reason",scriptContext="old context",screenshotPath="old.png",recoveryId,agent_resolution=new{accepted=false}};
            string old=Path.Combine(root,"legacy.json");await File.WriteAllTextAsync(old,JsonSerializer.Serialize(legacy));
            Check(store.Read("legacy.json") is {Status:IncidentReadStatus.Legacy,Record.SchemaVersion:null,Record.IncidentId:null},"legacy invented version/id");
            await logger.UpdateRecoveryAsync(old,new(recoveryId,1,"needs_human","None","test",DateTime.UtcNow));
            var updated=JsonNode.Parse(await File.ReadAllTextAsync(old))!;
            Check(updated["schemaVersion"] is null&&updated["incidentId"] is null&&updated["traceLink"] is null&&updated["agent_resolution"] is not null,"legacy implicitly migrated");
            var unknown=updated.DeepClone();unknown["schemaVersion"]=99;string future=Path.Combine(root,"future.json");await File.WriteAllTextAsync(future,unknown.ToJsonString());
            byte[] original=await File.ReadAllBytesAsync(future);
            Check(store.Read("future.json").Status==IncidentReadStatus.UnsupportedFormat,"unknown version converted");
            try{await logger.UpdateRecoveryAsync(future,new(recoveryId,1,"resolved","None","test",DateTime.UtcNow));throw new Exception("unknown update allowed");}catch(NotSupportedException){}
            var unchanged=await File.ReadAllBytesAsync(future);Check(original.SequenceEqual(unchanged),"unknown file rewritten");
            await File.WriteAllTextAsync(Path.Combine(root,"broken.json"),"{half");
            await File.WriteAllTextAsync(Path.Combine(root,"large.json"),new string('x',IncidentStore.MaxRecordBytes+1));
            var malicious=updated.DeepClone();malicious["screenshotPath"]="../outside.png";await File.WriteAllTextAsync(Path.Combine(root,"path.json"),malicious.ToJsonString());
            Check(store.Read("path.json").ScreenshotReference is null&&store.Read("../legacy.json").Status==IncidentReadStatus.Corrupt,"archive path traversal allowed");
            Check(store.Read("large.json").Status==IncidentReadStatus.CapacityExceeded&&store.Read("missing.json").Status==IncidentReadStatus.NotFound,"incident size/absence states");
            using(var locked=new FileStream(old,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                Check(store.Read("legacy.json").Status==IncidentReadStatus.ReadFailed,"real archive IO failure displayed as corrupt/missing");
            var page=store.List(limit:2);Check(page.Entries.Count==2&&page.NextOffset==2,"incident page grew unbounded");
            var all=store.List();Check(all.Entries.Any(e=>e.Status==IncidentReadStatus.Corrupt)&&all.Entries.Any(e=>e.Status==IncidentReadStatus.Legacy),"bad file poisoned incident list");
            foreach(string invalid in new[]{"2.5","2147483648","null","\"1\""})
            {
                await File.WriteAllTextAsync(Path.Combine(root,"invalid.json"),"{\"schemaVersion\":"+invalid+"}");
                Check(store.Read("invalid.json").Status==IncidentReadStatus.Corrupt,"invalid incident version: "+invalid);
            }
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task IncidentTraceFaultsAsync()
    {
        foreach(string state in new[]{"disabled","closed","failed","hung"})
        {
            string root=TraceRoot();Directory.CreateDirectory(root);var sink=new TraceSink{Hold=state=="hung",Fail=state=="failed"};
            var traces=new ExecutionTraceRecorder(Path.Combine(root,"traces"),new(FlushMilliseconds:50),sink:sink,enabled:state!="disabled");
            try
            {
                await using(var f=await ClickFixture.Create(traces:traces))
                {
                    if(state=="closed")await traces.DisposeAsync();
                    if(state=="failed")await Until(()=>traces.Diagnostics.WriteFailures>0);
                    if(state=="hung")await Bound(sink.Entered.Task);
                    var logger=new AnomalyLogger(NullLogger<AnomalyLogger>.Instance,Path.Combine(root,"incidents"));
                    string path=await logger.LogAnomalyAsync(f.Run,"trace fault independent archive","context").WaitAsync(TimeSpan.FromSeconds(8));
                    var record=new IncidentStore(logger.DirectoryPath).Read(Path.GetFileName(path)).Record!;
                    Check(record.TraceLink!.Status==(state switch{"disabled"=>"Disabled","closed"=>"Closed","failed"=>"WriterFailed",_=>"AssociatedInMemory"})&&
                        record.TraceLink.Persistence=="NotConfirmed","trace failure blocked/claimed persistent incident link: "+state);
                    await Bound(f.Host.Scheduler.StopTaskAsync(f.Run.InstanceId));
                }
            }
            finally
            {
                sink.Release.TrySetResult();await traces.DisposeAsync();await traces.WriterCompletion.WaitAsync(TimeSpan.FromSeconds(8));Directory.Delete(root,true);
            }
        }
    }
    private static async Task ReviewCatalogAsync()
    {
        string root=TraceRoot();Directory.CreateDirectory(root);
        try
        {
            var traces=new ExecutionTraceRecorder(Path.Combine(root,"traces"),new(FlushMilliseconds:2000));
            var first=traces.OpenRun(new("same",Guid.NewGuid(),1,Guid.NewGuid(),123));var second=traces.OpenRun(new("same",Guid.NewGuid(),2,Guid.NewGuid(),123));
            first.Emit(TraceEventKind.RunCreated);second.Emit(TraceEventKind.RunCreated);
            var reader=new TraceReviewReader(traces,new(Path.Combine(root,"traces")),new(Path.Combine(root,"incidents")));
            var current=reader.Read(new(TraceReviewKind.Catalog)).Choices!;
            Check(current.Any(c=>c.Memory&&c.IsControl==true)&&current.Where(c=>c.Memory&&c.Run is not null).Select(c=>c.Run!.InstanceId).Distinct().Count()==2,"control/dual run selection mixed");
            Check(reader.Read(new(TraceReviewKind.Memory,TraceId:first.TraceId)).Page is {FromDisk:false,Status:TraceReadStatus.Incomplete},"memory advertised complete");
            traces.CloseRun(first);first.Emit(TraceEventKind.CleanupCompleted);traces.CloseRun(second);second.Emit(TraceEventKind.CleanupCompleted);await traces.DisposeAsync();
            using var restart=new TraceReviewService(new TraceReviewReader(ExecutionTraceRecorder.Disabled(),new(Path.Combine(root,"traces")),new(Path.Combine(root,"incidents"))));
            var history=reader.Read(new(TraceReviewKind.Disk,traces.HostSessionId,first.TraceId)).Page!;
            Check(history.Status==TraceReadStatus.Valid&&history.Summary!.Run==first.Run,"stopped run history unavailable");
            string eventPath=Directory.GetFiles(Path.Combine(root,"traces"),"*"+first.TraceId.ToString("N")+".events").Single();File.Delete(eventPath);
            Check(reader.Read(new(TraceReviewKind.Disk,traces.HostSessionId,first.TraceId)).Page!.Status==TraceReadStatus.NotFound,"expired trace not unavailable");
            Check(new TraceChoice(Guid.NewGuid(),Guid.NewGuid(),null,false,"Corrupt").Label.Contains("运行身份未知"),"corrupt history claimed control identity");
            await using var crowded=new ExecutionTraceRecorder();
            for(int i=0;i<105;i++)crowded.OpenRun(new("page",Guid.NewGuid(),i+1,Guid.NewGuid(),123));
            var paged=new TraceReviewReader(crowded,new(Path.Combine(root,"empty")),new(Path.Combine(root,"incidents")));
            var head=paged.Read(new(TraceReviewKind.Catalog));var tail=paged.Read(new(TraceReviewKind.Catalog,Offset:head.NextOffset!.Value));
            Check(head.Choices!.Count==100&&tail.Choices!.Count==6&&tail.NextOffset is null&&
                !head.Choices.Select(c=>c.TraceId).Intersect(tail.Choices.Select(c=>c.TraceId)).Any(),"current run catalog lost traces beyond first page");
        }
        finally{Directory.Delete(root,true);}
    }
}
