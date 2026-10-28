#nullable enable
using Rug.UI.Core.Models;
namespace Rug.UI.Core.Services;

public interface ITraceReviewReader { TraceReviewResult Read(TraceReviewRequest request); }
/// <summary>Read-only adapter; called only by the host's bounded background query broker.</summary>
public sealed class TraceReviewReader(ExecutionTraceRecorder recorder,TraceFileStore store,IncidentStore incidents) : ITraceReviewReader
{
    public TraceReviewResult Read(TraceReviewRequest request)=>ReadCore(request) with {RecorderEnabled=recorder.Enabled};
    private TraceReviewResult ReadCore(TraceReviewRequest request)
    {
        if(request.Limit is <1 or >256)throw new ArgumentOutOfRangeException(nameof(request));
        switch(request.Kind)
        {
            case TraceReviewKind.Catalog:
                var disk=store.Discover(offset:request.Offset,limit:100);
                var memory=recorder.Summaries;
                var choices=memory.Skip(request.Offset).Take(100).Select(s=>new TraceChoice(s.HostSessionId,s.TraceId,s.Run,true,"当前保留窗口",s.Run is null))
                    .Concat(disk.Entries.Select(e=>new TraceChoice(e.HostSessionId,e.TraceId,e.Run,false,e.Status.ToString(),e.Summary is null?null:e.Run is null))).ToArray();
                return new(request,recorder.Diagnostics,Array.AsReadOnly(choices),memory.Count>request.Offset+100?request.Offset+100:disk.NextOffset);
            case TraceReviewKind.Memory:
                return new(request,recorder.Diagnostics,Page:recorder.Query(request.TraceId??Guid.Empty,request.OperationId,request.AfterSequence,request.Limit));
            case TraceReviewKind.Disk:
                return new(request,recorder.Diagnostics,Page:store.Read(request.SessionId??Guid.Empty,request.TraceId??Guid.Empty,request.OperationId,request.AfterSequence,request.Limit));
            case TraceReviewKind.Incidents:
                return new(request,recorder.Diagnostics,Incidents:incidents.List(request.Offset,Math.Min(100,request.Limit)));
            case TraceReviewKind.Incident:
                return new(request,recorder.Diagnostics,Incident:incidents.Read(request.IncidentKey??""));
            default:throw new ArgumentOutOfRangeException(nameof(request));
        }
    }
}

/// <summary>One real read plus one replaceable pending read for the entire host.
/// @shape session: logical UI owner, revision: latest-only delivery, IO: not cancelled by Dispose.
/// @source read: Services/TraceFileStore.cs#class:TraceFileStore</summary>
public sealed class TraceReviewService(ITraceReviewReader reader) : IDisposable
{
    private readonly object _gate=new();
    private readonly Dictionary<Guid,Session> _sessions=[];
    private sealed record Pending(Guid Owner,long Revision,TraceReviewRequest Request);
    private Pending? _pending;
    private bool _reading,_closed;
    public (int Sessions,bool Reading,int Pending) Usage { get { lock(_gate)return(_sessions.Count,_reading,_pending is null?0:1); } }
    public Session Open(Action<Action> dispatch)
    {
        lock(_gate)
        {
            if(_closed)throw new ObjectDisposedException(nameof(TraceReviewService));
            if(_sessions.Count>=4)throw new InvalidOperationException("Viewer session capacity reached.");
            var session=new Session(this,dispatch);_sessions.Add(session.Id,session);return session;
        }
    }
    public sealed class Session : IDisposable
    {
        private readonly TraceReviewService _owner;
        internal readonly Guid Id=Guid.NewGuid();
        internal readonly Action<Action> Dispatch;
        internal long Revision;
        internal TraceReviewResult? Ready;
        internal long ReadyRevision;
        internal bool DeliveryScheduled;
        public event Action<TraceReviewResult>? Completed;
        internal Session(TraceReviewService owner,Action<Action> dispatch){_owner=owner;Dispatch=dispatch;}
        public void Submit(TraceReviewRequest request)=>_owner.Submit(this,request);
        // Invalidate any scheduled UI delivery even when no new read is wanted.
        public void Invalidate(){lock(_owner._gate){Revision++;Ready=null;if(_owner._pending?.Owner==Id)_owner._pending=null;}}
        public void Dispose()
        {lock(_owner._gate){Revision++;Ready=null;Completed=null;_owner._sessions.Remove(Id);if(_owner._pending?.Owner==Id)_owner._pending=null;}}
        internal void Deliver(TraceReviewResult result)=>Completed?.Invoke(result);
    }
    private void Submit(Session session,TraceReviewRequest request)
    {
        lock(_gate)
        {
            if(_closed||!_sessions.ContainsKey(session.Id))return;
            session.Ready=null;
            _pending=new(session.Id,++session.Revision,request);
            if(_reading)return;_reading=true;
            _=Task.Run(Pump); // The worker captures no page/session/event handler, only this host broker.
        }
    }
    private void Pump()
    {
        while(true)
        {
            Pending next;
            lock(_gate)
            {
                if(_closed||_pending is null){_reading=false;return;}
                next=_pending;_pending=null;
            }
            TraceReviewResult result;
            try{result=reader.Read(next.Request);}
            catch(Exception error){result=new(next.Request,null,Error:"ReadFailed / "+error.GetType().Name);}
            Action<Action>? dispatch;
            lock(_gate)
            {
                dispatch=null;
                if(_sessions.TryGetValue(next.Owner,out var session)&&session.Revision==next.Revision)
                {
                    session.Ready=result;session.ReadyRevision=next.Revision;
                    if(!session.DeliveryScheduled){session.DeliveryScheduled=true;dispatch=session.Dispatch;}
                }
            }
            try
            {
                dispatch?.Invoke(()=>
                {
                    Session? current=null;TraceReviewResult? ready=null;
                    lock(_gate)
                    {
                        if(!_closed&&_sessions.TryGetValue(next.Owner,out var s))
                        {
                            s.DeliveryScheduled=false;
                            if(s.Revision==s.ReadyRevision){current=s;ready=s.Ready;}
                            s.Ready=null;
                        }
                    }
                    // External UI callback is outside the broker lock and rechecks its own loaded state.
                    if(ready is not null)current?.Deliver(ready);
                });
            }
            catch{ /* A closed dispatcher/observer cannot fault the worker or control flow. */ }
        }
    }
    public void Dispose()
    {
        lock(_gate){_closed=true;_pending=null;foreach(var s in _sessions.Values.ToArray())s.Dispose();_sessions.Clear();}
        // Never wait for physical file IO: the sole reader may remain hung without a UI owner.
    }
}
