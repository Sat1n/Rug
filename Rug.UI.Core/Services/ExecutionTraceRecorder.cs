#nullable enable
using System.Text.Json;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Host-only recording capability. Contains detached IDs, never a runtime/resource.</summary>
public sealed class ExecutionTraceHandle
{
    internal ExecutionTraceHandle(ExecutionTraceRecorder owner, Guid id, TraceRun? run, TraceOrigin? origin)
    { Owner=owner;TraceId=id;Run=run;Origin=origin; }
    internal ExecutionTraceRecorder Owner { get; }
    public Guid TraceId { get; }
    public TraceRun? Run { get; }
    public TraceOrigin? Origin { get; }
    internal void Emit(TraceEventKind kind, TraceOperation? operation=null, string? status=null, string? error=null,
        string? reason=null, bool? uncertain=null, TraceAuthorization? authorization=null, TraceObservation? observation=null)
        => Owner.Emit(this,kind,operation,status,error,reason,uncertain,authorization,observation);
}
internal sealed record TraceOperation(ExecutionTraceHandle Handle, Guid OperationId, string CorrelationId,
    Guid? ExecutionId, string Caller, AutomationSource Source, string Operation, TraceTarget Target)
{
    private TraceObservation? _knownObservation;
    internal TraceObservation? KnownObservation=>Volatile.Read(ref _knownObservation);
    internal void Emit(TraceEventKind kind,string? status=null,string? error=null,string? reason=null,bool? uncertain=null,
        TraceAuthorization? authorization=null,TraceObservation? observation=null)
    {
        if(observation is not null)Volatile.Write(ref _knownObservation,observation);
        Handle.Emit(kind,this,status,error,reason,uncertain,authorization,observation);
    }
}
internal static class ExecutionTraceScope
{
    internal static readonly AsyncLocal<TraceOperation?> Current = new();
    internal static TraceObservation? Observation(object? value) => value switch
    {
        CapturedFrameMetadata c => From(c.Observation), VisionOcrResult o => From(o.Observation),
        ImageFindResult m => From(m.Observation), ObservedClickResult c when c.ObservationId is {} id && c.FrameId is {} frame =>
            new(id,frame,c.PolicyId,c.ClientPoint,Array.AsReadOnly(c.ValidationFrames?.Take(3).Select(f=>f.FrameId).ToArray()??[])),
        _ => null
    };
    private static TraceObservation From(ObservationEnvelope o)=>new(o.ObservationId,o.FrameId);
}

/// <summary>Nonblocking bounded producer. Only the worker serializes or calls the file sink.
/// @shape events: whitelisted detached metadata; sequence: session-wide record order, not causality.
/// @source execution: Services/AutomationGateway.cs#class:AutomationGateway</summary>
public sealed class ExecutionTraceRecorder : IAsyncDisposable
{
    internal sealed class Progress(ExecutionTraceHandle handle)
    {
        internal ExecutionTraceHandle Handle=handle;
        internal long First,Last,Count,Dropped,Recorded,LastRecorded;
        internal bool Cleanup,Closed,CleanupFailure;
        internal int Work;
        internal TraceSummary Snapshot(Guid session)=>new(1,session,Handle.TraceId,Handle.Run,First,Last,Count,Dropped,
            Cleanup,Closed,Dropped!=0||!Closed||Work!=0||Count!=Recorded||(Handle.Run is not null&&!Cleanup),Work,Recorded,LastRecorded,
            Cleanup&&Dropped==0?!CleanupFailure:null);
    }
    private readonly object _gate=new();
    private readonly Dictionary<Guid,Progress> _traces=[];
    private readonly LinkedList<(ExecutionTraceEvent Event,int Bytes)> _queue=[];
    private readonly Queue<(ExecutionTraceEvent Event,int Bytes)> _memory=[];
    private readonly SemaphoreSlim _signal=new(0,1);
    private readonly TimeProvider _clock;
    private readonly TraceFileStore? _store;
    private readonly Task _writer;
    private readonly bool _enabled;
    internal Task WriterCompletion => _writer;
    private long _sequence,_dropped,_failures;
    private int _queueBytes,_memoryBytes;
    private bool _closing,_flushIncomplete,_busy;
    private string? _writerError;
    public Guid HostSessionId { get; }=Guid.NewGuid();
    public TraceLimits Limits { get; }
    public bool Enabled=>_enabled;
    public ExecutionTraceHandle Control { get; }
    public static ExecutionTraceRecorder Disabled()=>new(enabled:false);
    internal IncidentTraceLink IncidentBoundary(ExecutionTraceHandle handle,TraceOperation? operation)
    {
        try
        {
            lock(_gate)
            {
                string status=!_enabled?"Disabled":handle.TraceId==Guid.Empty?"CapacityRejected":
                    !_traces.TryGetValue(handle.TraceId,out var p)?"Unavailable":_closing||p.Closed?"Closed":
                    _failures>0?"WriterFailed":"AssociatedInMemory";
                bool exists=_traces.ContainsKey(handle.TraceId)&&handle.TraceId!=Guid.Empty;
                // An inherited Start/control scope or another instance is not this run's operation.
                var op=operation?.Handle.TraceId==handle.TraceId&&operation.Handle.Run==handle.Run?operation:null;
                var observation=op?.KnownObservation;
                return new(status,HostSessionId,exists?handle.TraceId:null,handle.Run,
                    exists?_sequence:null,op?.OperationId,op?.ExecutionId,observation?.ObservationId,observation?.FrameId,
                    _clock.GetUtcNow());
            }
        }
        catch{return new("Unknown");} // Never make an otherwise valid archive depend on tracing.
    }
    public ExecutionTraceRecorder(string? root=null,TraceLimits? limits=null,TimeProvider? clock=null,
        ITraceFileSink? sink=null,bool enabled=true)
    {
        Limits=limits??new();_clock=clock??TimeProvider.System;_enabled=enabled;
        if(Limits.MaxEventBytes is <4096 or >65536||Limits.MemoryEvents is <1 or >16384||Limits.MemoryBytes<Limits.MaxEventBytes||Limits.MemoryBytes>64*1024*1024||
            Limits.QueueEvents<1||Limits.QueueBytes<Limits.MaxEventBytes||Limits.MaxTraces<2||Limits.MaxTraces>1024||
            Limits.MaxFileBytes<Limits.MaxEventBytes||Limits.MaxDiskBytes<Limits.MaxFileBytes||Limits.MaxFiles<2||
            Limits.QueueEvents>4096||Limits.QueueBytes>16*1024*1024||Limits.MaxFileBytes>64*1024*1024||
            Limits.MaxDiskBytes>1024L*1024*1024||Limits.MaxFiles>2048||Limits.RetentionDays is <1 or >30||
            Limits.FlushMilliseconds is <1 or >5000)throw new ArgumentOutOfRangeException(nameof(limits));
        Control=new(this,Guid.NewGuid(),null,null);_traces.Add(Control.TraceId,new(Control));
        if(enabled&&root is not null)
            try{_store=new(root,Limits,sink);}
            catch(Exception error){_failures++;_writerError=Text(error.GetType().Name);_flushIncomplete=true;}
        _writer=_store is null?Task.CompletedTask:Task.Run(WriteLoopAsync);
    }
    public ExecutionTraceHandle OpenRun(RunIdentity identity,CheckpointOrigin? origin=null,RuntimeUpdateOrigin? update=null)
    {
        try
        {
            lock(_gate)
            {
                if(!_enabled||_closing)return new(this,Guid.Empty,null,null);
                if(_traces.Count>=Limits.MaxTraces)
                {
                    // Retire only closed, drained histories; never borrow another run's identity.
                    var retired=_traces.Values.FirstOrDefault(p=>p.Closed&&p.Work==0&&!_queue.Any(q=>q.Event.TraceId==p.Handle.TraceId));
                    if(retired is not null)_traces.Remove(retired.Handle.TraceId);
                    else{_dropped++;return new(this,Guid.Empty,null,null);}
                }
                var h=new ExecutionTraceHandle(this,Guid.NewGuid(),new(Text(identity.PluginId,128)!,identity.InstanceId,identity.RunGeneration,identity.TargetBindingId),
                    origin is null?null:new(origin.ParentInstanceId,origin.CheckpointId,origin.Sequence,Text(origin.NodeId,128)!,update?.UpdateId,Text(update?.RuleId,64)));
                _traces.Add(h.TraceId,new(h));return h;
            }
        }
        catch{Interlocked.Increment(ref _dropped);return new(this,Guid.Empty,null,null);}
    }
    internal TraceOperation Operation(ExecutionTraceHandle? handle,AutomationCallContext caller,OperationRequest request,bool actual=true)=>
        new(handle??Control,request.OperationId,Text(request.CorrelationId,128)??"",actual?Guid.NewGuid():null,Text(caller.Principal,128)??"",
            caller.Source,request.Operation.ToString(),new(Text(request.Target?.PluginId,128)??"",request.Target?.InstanceId,
                request.Target?.RunGeneration,request.Target?.TargetBindingId));
    internal static string? Text(string? value,int length=192)=>value is null?null:value[..Math.Min(value.Length,length)];
    internal void Emit(ExecutionTraceHandle handle,TraceEventKind kind,TraceOperation? op,string? status,string? error,
        string? reason,bool? uncertain,TraceAuthorization? authorization,TraceObservation? observation)
    {
        if(!_enabled)return;
        try
        {
            // Conservative UTF-8 JSON bound (including worst-case escaping), without serialization.
            string? st=Text(status,64),er=Text(error,96),why=Text(reason);
            var auth=authorization is null?null:new TraceAuthorization(Text(authorization.Decision,32)!,
                Array.AsReadOnly(authorization.Required.Take(16).Select(p=>Text(p,64)!).ToArray()),authorization.Revision,
                authorization.Missing is null?null:Array.AsReadOnly(authorization.Missing.Take(16).Select(p=>Text(p,64)!).ToArray()));
            var obs=observation is null?null:observation with{PolicyId=Text(observation.PolicyId,64),
                ValidationFrames=observation.ValidationFrames is null?null:Array.AsReadOnly(observation.ValidationFrames.Take(3).ToArray())};
            int chars=(st?.Length??0)+(er?.Length??0)+(why?.Length??0)+(handle.Run?.PluginId.Length??0)+
                (handle.Origin?.NodeId.Length??0)+(handle.Origin?.CompatibilityRuleId?.Length??0)+(op?.CorrelationId.Length??0)+(op?.Caller.Length??0)+
                (op?.Operation.Length??0)+(op?.Target.PluginId.Length??0)+(obs?.PolicyId?.Length??0)+
                (auth?.Required.Sum(s=>s.Length)??0)+(auth?.Missing?.Sum(s=>s.Length)??0)+(auth?.Decision.Length??0);
            int bytes=3072+chars*6;
            lock(_gate)
            {
                if(_closing||!_traces.TryGetValue(handle.TraceId,out var progress)){_dropped++;return;}
                long sequence=++_sequence;
                progress.Recorded++;progress.LastRecorded=sequence;
                if(bytes>Limits.MaxEventBytes){progress.Dropped++;_dropped++;return;}
                var e=new ExecutionTraceEvent(1,HostSessionId,handle.TraceId,sequence,handle.Run,handle.Origin,
                    op?.OperationId,op?.CorrelationId,op?.ExecutionId,op?.Caller,op?.Source,op?.Operation,op?.Target,kind,
                    _clock.GetUtcNow(),_clock.GetTimestamp(),_clock.TimestampFrequency,"host-session TimeProvider monotonic",
                    st,er,why,uncertain,auth,obs,
                    obs is not null?TraceDataState.Present:op?.Operation is "Capture" or "Ocr" or "FindImage" or "ClickObserved"?
                        kind==TraceEventKind.CallerReturned&&st=="Success"?TraceDataState.Absent:TraceDataState.NotCollected:TraceDataState.NotApplicable,
                    obs?.ValidationFrames is {} validation?validation.Count==0?TraceDataState.Absent:TraceDataState.Present:
                        op?.Operation=="ClickObserved"?TraceDataState.NotCollected:TraceDataState.NotApplicable);
                while(_memory.Count>=Limits.MemoryEvents||_memoryBytes+bytes>Limits.MemoryBytes)
                {var old=_memory.Dequeue();_memoryBytes-=old.Bytes;}
                _memory.Enqueue((e,bytes));_memoryBytes+=bytes;
                if(_store is null)return;
                bool important=kind is TraceEventKind.CallerReturned or TraceEventKind.WorkCompleted or TraceEventKind.ReleaseFailed or
                    TraceEventKind.CleanupFailed or TraceEventKind.CleanupCompleted or TraceEventKind.HostClosing;
                while(_queue.Count>=Limits.QueueEvents||_queueBytes+bytes>Limits.QueueBytes)
                {
                    var victim=important?_queue.First:null;
                    while(victim is not null&&Critical(victim.Value.Event.Kind))victim=victim.Next;
                    if(victim is null){progress.Dropped++;_dropped++;return;}
                    var dropped=victim.Value;_queueBytes-=dropped.Bytes;_queue.Remove(victim);
                    if(_traces.TryGetValue(dropped.Event.TraceId,out var p))p.Dropped++;
                    _dropped++;
                }
                _queue.AddLast((e,bytes));_queueBytes+=bytes;
                if(_signal.CurrentCount==0)_signal.Release();
            }
        }
        catch
        {
            lock(_gate){_dropped++;if(_traces.TryGetValue(handle.TraceId,out var p))p.Dropped++;}
        } // Recording must never change a control decision.
    }
    private static bool Critical(TraceEventKind k)=>k is TraceEventKind.CallerReturned or TraceEventKind.WorkCompleted or
        TraceEventKind.ReleaseFailed or TraceEventKind.CleanupFailed or TraceEventKind.CleanupCompleted or TraceEventKind.HostClosing;
    public void CloseRun(ExecutionTraceHandle handle)
    {try{lock(_gate)if(_traces.TryGetValue(handle.TraceId,out var p))p.Closed=true;}catch{}}
    public TraceDiagnostics Diagnostics { get { lock(_gate)return new(_dropped,_failures,_writerError,_queue.Count,_queueBytes,
        _memory.Count,_memoryBytes,_traces.Count,_closing,_flushIncomplete,_busy); } }
    public TracePage Query(Guid traceId,Guid? operationId=null,long afterSequence=0,int limit=100,long? throughSequence=null)
    {
        if(limit is <1 or >256||afterSequence<0||throughSequence<afterSequence)return new(TraceReadStatus.CapacityExceeded,[],null,null,false);
        lock(_gate)
        {
            var rows=_memory.Select(x=>x.Event).Where(e=>e.TraceId==traceId&&e.Sequence>afterSequence&&(throughSequence is null||e.Sequence<=throughSequence)&&
                (operationId is null||e.OperationId==operationId)).Take(limit+1).ToArray();
            var summary=_traces.GetValueOrDefault(traceId)?.Snapshot(HostSessionId);
            // Memory is only a bounded window, never claim durable/full history here.
            return new(summary is null?TraceReadStatus.NotFound:TraceReadStatus.Incomplete,Array.AsReadOnly(rows.Take(limit).ToArray()),
                rows.Length>limit?rows[limit-1].Sequence:null,summary,false,"Bounded memory window; consult disk for persisted range.");
        }
    }
    public IReadOnlyList<TraceSummary> Summaries { get { lock(_gate)return Array.AsReadOnly(_traces.Values.Select(p=>p.Snapshot(HostSessionId)).ToArray()); } }
    private async Task WriteLoopAsync()
    {
        while(true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            while(true)
            {
                ExecutionTraceEvent? nextEvent=null;Progress? progress=null;TraceSummary[]? final=null;
                lock(_gate)
                {
                    if(_queue.First is {} next)
                    {
                        nextEvent=next.Value.Event;_queueBytes-=next.Value.Bytes;_queue.RemoveFirst();_busy=true;
                        progress=_traces.GetValueOrDefault(nextEvent.TraceId);
                    }
                    else {_busy=false;if(_closing)final=_traces.Values.Where(p=>p.Count>0).Select(p=>p.Snapshot(HostSessionId)).ToArray();}
                }
                if(final is not null)
                {
                    foreach(var summary in final)
                        try{await _store!.WriteSummaryAsync(summary).ConfigureAwait(false);}
                        catch(Exception error){lock(_gate){_failures++;_writerError=WriterError(error);}}
                    bool seal;long dropped;lock(_gate){seal=_failures==0;dropped=_dropped;}
                    if(seal)try{await _store!.CompleteSessionAsync(HostSessionId,dropped).ConfigureAwait(false);}
                        catch(Exception error){lock(_gate){_failures++;_writerError=WriterError(error);}}
                    return;
                }
                if(nextEvent is not {} e)break;
                if(progress is null)continue;
                try
                {
                    await _store!.AppendAsync(e).ConfigureAwait(false);
                    TraceSummary summary;
                    lock(_gate)
                    {
                        progress.First=progress.First==0?e.Sequence:progress.First;progress.Last=e.Sequence;progress.Count++;
                        if(e.Kind==TraceEventKind.WorkStarted)progress.Work++;
                        if(e.Kind==TraceEventKind.WorkCompleted)progress.Work--;
                        if(e.Kind==TraceEventKind.CleanupCompleted)progress.Cleanup=true;
                        if(e.Kind is TraceEventKind.CleanupFailed or TraceEventKind.ReleaseFailed)progress.CleanupFailure=true;
                        summary=progress.Snapshot(HostSessionId);
                    }
                    await _store.WriteSummaryAsync(summary).ConfigureAwait(false);
                }
                catch(Exception error)
                {lock(_gate){progress.Dropped++;_dropped++;_failures++;_writerError=WriterError(error);}}
            }
        }
    }
    private static string WriterError(Exception error)=>error.GetType().Name+"/0x"+error.HResult.ToString("X8");
    public async ValueTask DisposeAsync()
    {
        lock(_gate)
        {
            if(_closing)return;
            Control.Emit(TraceEventKind.HostClosing);_closing=true;
            foreach(var p in _traces.Values)p.Closed=true;
            if(_signal.CurrentCount==0)_signal.Release();
        }
        try{await _writer.WaitAsync(TimeSpan.FromMilliseconds(Limits.FlushMilliseconds)).ConfigureAwait(false);}
        catch{lock(_gate)_flushIncomplete=true;}
        // The writer alone owns its detached data/sink. Never dispose locks under stuck IO.
    }
}
