#nullable enable
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
namespace Rug.UI.Core.Services;

public sealed record QpcReading(long Ticks, long Frequency, string Domain = "Windows boot QPC");
/// <summary>Trusted host seam for deterministic clock/window tests. Production uses native checks.</summary>
public interface IObservedInputEnvironment
{
    QpcReading ReadQpc();
    void ValidateGeometry(RunIdentity run, CapturedFrame frame);
}
public sealed class ObservedClickServices
{
    internal IObservedInputEnvironment Environment { get; }
    private readonly IReadOnlyDictionary<string,ObservedClickPolicy> _policies;
    public ObservedClickServices(IEnumerable<ObservedClickPolicy>? policies = null, IObservedInputEnvironment? environment = null)
    {
        var items=(policies??[new ObservedClickPolicy()]).ToArray();
        if(items.Length is <1 or >16 || items.Select(p=>p.Id).Distinct().Count()!=items.Length || items.Any(p=>
            p.Id is not {Length: >0 and <=64} || p.MaxAgeMs is <1 or >2000 || p.Radius is <1 or >32 ||
            p.MaxChannelDifference is <0 or >32 || !double.IsFinite(p.MaxMeanDifference) || p.MaxMeanDifference is <0 or >8 ||
            p.VerificationTimeoutMs is <1 or >2000)) throw new ArgumentException("Invalid trusted observed click policy.");
        _policies=items.ToDictionary(p=>p.Id,StringComparer.Ordinal); Environment=environment??new ObservedInputEnvironment();
    }
    internal ObservedClickPolicy Policy(ObservedClickRequest request)
    {
        if(!_policies.TryGetValue(request.PolicyId,out var p) || request.MaxAgeMs>p.MaxAgeMs)
            throw new ObservedClickException(ObservedClickReason.PolicyUnavailable);
        return p;
    }
}

/// <summary>@shape source: retained owning-pool record; checks/capture execute outside all registry locks.
/// @source source: Services/ObservationFramePool.cs#class:ActionLease</summary>
internal sealed class ObservedClickTransaction : IDisposable
{
    private readonly ObservationFramePool _pool;
    private readonly ObservationFramePool.Session _session;
    private readonly ObservationFramePool.ActionLease _source;
    private readonly ICaptureService _capture;
    private readonly RunIdentity _run;
    private readonly IObservedInputEnvironment _environment;
    private readonly ObservedClickPolicy _policy;
    private readonly ObservedClickRequest _request;
    private readonly Action _validateRun;
    private readonly Action _demandCapture;
    private readonly List<ObservedValidationFrame> _validations=[];
    private string? _verificationStage;
    private int _candidateNumber;
    internal PointInt Point { get; }
    internal InputDeliveryMode Mode => _request.Mode;
    internal MouseButton Button => _request.Button;
    internal ObservedClickTransaction(ObservationFramePool pool, ObservationFramePool.Session session,
        ObservationFramePool.ActionLease source, ICaptureService capture, RunIdentity run,
        ObservedClickServices services, ObservedClickRequest request, Action validateRun, Action demandCapture)
    {
        _pool=pool;_session=session;_source=source;_capture=capture;_run=run;_request=request;
        _environment=services.Environment;_policy=services.Policy(request);_validateRun=validateRun;_demandCapture=demandCapture;
        var geometry=source.Observation.Geometry;
        if(!geometry.ClientGeometryVerified || geometry.ClientWidth is not >0 || geometry.ClientHeight is not >0 ||
            geometry.ImageToClient is not { } transform)
            throw new ObservedClickException(ObservedClickReason.GeometryChanged);
        double x=request.X,y=request.Y;
        if(request.CoordinateSpace=="roi-local-pixels")
        {
            if(source.Observation.Processing is not { } p || !new PixelRegion(0,0,p.InputWidth,p.InputHeight).Contains(x,y))
                throw new ObservedClickException(ObservedClickReason.CoordinateInvalid);
            x=x*p.ScaleX+p.OffsetX;y=y*p.ScaleY+p.OffsetY;
        }
        if(request.CoordinateSpace=="client-physical-pixels")
        {
            if(!transform.Inverse().TryMap(x,y,out x,out y)) throw new ObservedClickException(ObservedClickReason.CoordinateInvalid);
        }
        if(!transform.TryMapPixel(x,y,out int cx,out int cy) || (long)cx-_policy.Radius<0 || (long)cy-_policy.Radius<0 ||
            (long)cx+_policy.Radius>=geometry.ClientWidth || (long)cy+_policy.Radius>=geometry.ClientHeight)
            throw new ObservedClickException(ObservedClickReason.CoordinateInvalid);
        if(request.Mode==InputDeliveryMode.Win32PostMessage && (cx>short.MaxValue || cy>short.MaxValue))
            throw new ObservedClickException(ObservedClickReason.CoordinateInvalid); // LPARAM mouse coordinates are signed 16-bit.
        Point=new(cx,cy);
    }
    internal ObservedClickResult Result(string status, string reason) => new(status,reason,_source.Observation.ObservationId,
        _source.Observation.FrameId,Point,_policy.Id,Array.AsReadOnly(_validations.ToArray()));
    internal void PublishDetails(string stage)
    {
        var details=Result("pending",stage);
        InputExecutionBoundary.Observation.Value?.Invoke(details);
        ExecutionTraceScope.Current.Value?.Emit(TraceEventKind.Validation,reason:stage,observation:ExecutionTraceScope.Observation(details));
    }
    internal void Validate(CancellationToken token, Action? validateInput = null)
    {
        CheckAdmission(token);
        _source.Validate(); ValidateFrame(_source.Frame);
        // Geometry can block in Native code. Its return is not an admission decision:
        // recheck the lease/live input grant, original cancellation/deadline, run,
        // capture grant and current QPC after it, without another geometry query.
        validateInput?.Invoke();
        CheckAdmission(token); CheckTime(_source.Frame); _source.Validate();
    }
    private void CheckAdmission(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ObservationCallBoundary.Current.Value?.Check(); _validateRun(); _demandCapture();
    }
    private NativeFrameTime CheckTime(CapturedFrame frame)
    {
        var t=frame.CaptureFacts?.NativeTime;var now=_environment.ReadQpc();
        if(t is null || now.Domain!="Windows boot QPC" || t.ClockDomain!=now.Domain || now.Frequency<=0 ||
            t.QpcFrequency!=now.Frequency || t.AgeAt(now.Ticks,now.Frequency,now.Domain) is not { } age)
            throw TimeRejected(frame,t,now,ObservedClickReason.TimeUnavailable);
        if(age.TotalMilliseconds>(_request.MaxAgeMs??_policy.MaxAgeMs)) throw TimeRejected(frame,t,now,ObservedClickReason.Stale);
        return t;
    }
    private ObservedClickException TimeRejected(CapturedFrame frame, NativeFrameTime? time, QpcReading now, ObservedClickReason reason,
        TraceVerificationCandidate? candidate = null)
    {
        var raw=frame.NativeCaptureFacts;
        candidate??=_verificationStage is {} stage?new(stage,_candidateNumber,"Rejected",reason.ToString()):null;
        long? source=raw?.SystemRelative100ns??time?.SystemRelative100ns;
        long? frequency=raw?.QpcFrequency??time?.QpcFrequency;
        string? domain=raw is not null?"Windows boot QPC":time?.ClockDomain;
        long? SignedAge(long? rendered,long? hz,string? clock)
        {
            if(rendered is not >0 || clock!=now.Domain || hz!=now.Frequency || now.Frequency<=0 || now.Ticks<=0)return null;
            Int128 age=(Int128)now.Ticks*TimeSpan.TicksPerSecond/now.Frequency-rendered.Value;
            return age>=long.MinValue&&age<=long.MaxValue?(long)age:null;
        }
        var original=_source.Frame.CaptureFacts?.NativeTime;
        var detail=new TraceFrameTimeRejection(ReferenceEquals(frame,_source.Frame)?"source":"unpublished-verification",source,
            raw?.QpcSample??time?.QpcSample,frequency,raw?.Flags,domain,now.Ticks,now.Frequency,now.Domain,SignedAge(source,frequency,domain),time is not null,
            _request.MaxAgeMs??_policy.MaxAgeMs,original?.SystemRelative100ns,original?.QpcFrequency,
            SignedAge(original?.SystemRelative100ns,original?.QpcFrequency,original?.ClockDomain));
        ExecutionTraceScope.Current.Value?.Emit(TraceEventKind.Validation,status:"Rejected",error:reason.ToString(),reason:"frame-time-rejected",
            observation:new(_source.Observation.ObservationId,_source.Observation.FrameId,_policy.Id,Point,
                Array.AsReadOnly(_validations.Select(v=>v.FrameId).ToArray()),TimeRejection:detail,Candidate:candidate));
        return new(reason);
    }
    private void ValidateFrame(CapturedFrame frame)
    {
        CheckTime(frame);
        if(frame.CaptureFacts?.Geometry.ClientGeometryVerified!=true) throw new ObservedClickException(ObservedClickReason.GeometryChanged);
        _environment.ValidateGeometry(_run,frame);
    }
    internal void StartSideEffect(CancellationToken token, Action validateInput)
    {
        // Shared final boundary for foreground activation, movement and button-down.
        // No IO/callback inside the pool decision; necessary releases do not use it.
        Validate(token,validateInput);
        _source.Consume(); InputExecutionBoundary.MarkStarted();
    }
    private enum CandidateTime { Valid, Missing, NativeInvalid, Future, NotNewerThanDiscarded, NotFresh }
    private int VerificationMilliseconds => Math.Min(750,_policy.VerificationTimeoutMs);
    /// <summary>Only candidate-local invalidity is discardable; clock/frequency/domain errors are terminal.
    /// @shape frame: unpublished owned candidate; discarded: greatest positive discarded render100ns.
    /// @source time: Models/CaptureFacts.cs#class:NativeFrameTime</summary>
    private CandidateTime InspectCandidateTime(CapturedFrame frame,QpcReading now,long discarded,QpcReading requested)
    {
        var t=frame.CaptureFacts?.NativeTime;var raw=frame.NativeCaptureFacts;
        if(raw is {} f && (f.QpcFrequency<=0||f.QpcFrequency!=now.Frequency) ||
            t is not null && (t.ClockDomain!=now.Domain||t.QpcFrequency!=now.Frequency))
            throw TimeRejected(frame,t,now,ObservedClickReason.TimeUnavailable);
        if(raw is {} native && (native.Flags&1)==0)return CandidateTime.NativeInvalid;
        if(t is null || t.SystemRelative100ns<=0)return CandidateTime.Missing;
        if(t.AgeAtNativeSample is null)return CandidateTime.NativeInvalid;
        if(raw is {} facts && (facts.SystemRelative100ns!=t.SystemRelative100ns||facts.QpcSample!=t.QpcSample))
            throw TimeRejected(frame,t,now,ObservedClickReason.TimeUnavailable);
        if(t.AgeAt(now.Ticks,now.Frequency,now.Domain) is not {} age)return CandidateTime.Future;
        if(age.TotalMilliseconds>(_request.MaxAgeMs??_policy.MaxAgeMs))throw TimeRejected(frame,t,now,ObservedClickReason.Stale);
        if((Int128)t.SystemRelative100ns*requested.Frequency<(Int128)requested.Ticks*TimeSpan.TicksPerSecond)return CandidateTime.NotFresh;
        if(t.SystemRelative100ns<=discarded)return CandidateTime.NotNewerThanDiscarded;
        return CandidateTime.Valid;
    }
    private void CandidateEvent(string stage,int number,string kind,string reason)
    {
        try { ExecutionTraceScope.Current.Value?.Emit(TraceEventKind.Validation,reason:"private-verification:"+kind,
            observation:new(_source.Observation.ObservationId,_source.Observation.FrameId,_policy.Id,Point,
                Array.AsReadOnly(_validations.Select(v=>v.FrameId).ToArray()),Candidate:new(stage,number,kind,reason))); }
        catch { /* Diagnostic failure cannot change the decision. */ }
    }
    /// <summary>@shape stage: <=3 sequential candidates, one shared <=750ms deadline and <=1 private publication.
    /// @source capture: Contracts/Services/ICaptureService.cs#class:ICaptureService</summary>
    internal async Task VerifyAsync(string stage, CancellationToken token)
    {
        int number=0;string ending="Failed";
        _verificationStage=stage;_candidateNumber=0;
        try
        {
            Validate(token);
            var started=_environment.ReadQpc();var latest=started;
            using var timeout=new CancellationTokenSource(VerificationMilliseconds);
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,timeout.Token);
            QpcReading Guard()
            {
                Validate(token); // Original cancellation, run/grants and original source always win.
                latest=CheckVerificationDeadline(started,latest);
                if(timeout.IsCancellationRequested)throw new ObservedClickException(ObservedClickReason.ValidationUnavailable);
                return latest;
            }
            try
            {
                long discarded=0;
                for(number=1;number<=3;number++)
                {
                    _candidateNumber=number;
                    Guard();CandidateEvent(stage,number,"Acquiring","Candidate");
                    using var capture=_pool.Capture(_session,privateFrame:true);
                    var requestQpc=Guard();long requested=_pool.Timestamp;var requestedUtc=_pool.UtcNow;
                    // Await the real service: never abandon Native work or its capacity reservation.
                    var frame=await (_capture is IBoundedFrameCapture bounded?bounded.GrabFrameBoundedAsync(_pool.Limits.MaxFrameBytes,linked.Token)
                        :_capture.GrabFrameAsync(linked.Token)).ConfigureAwait(false);
                    long received=_pool.Timestamp;var receivedUtc=_pool.UtcNow;
                    var now=Guard();
                    if(frame is null){ending="NoFrame";throw new ObservedClickException(ObservedClickReason.ValidationUnavailable);}
                    var owned=capture.Detach(frame);
                    var verdict=InspectCandidateTime(owned,now,discarded,requestQpc);
                    if(verdict==CandidateTime.NotFresh){ending="NotFresh";throw new ObservedClickException(ObservedClickReason.ValidationUnavailable);}
                    if(verdict!=CandidateTime.Valid)
                    {
                        _=TimeRejected(owned,owned.CaptureFacts?.NativeTime,now,ObservedClickReason.TimeUnavailable,
                            new(stage,number,"Discarded",verdict.ToString()));
                        long rendered=owned.NativeCaptureFacts?.SystemRelative100ns??owned.CaptureFacts?.NativeTime?.SystemRelative100ns??0;
                        if(rendered>0)discarded=Math.Max(discarded,rendered);
                        if(number==3){ending="AttemptsExhausted";throw new ObservedClickException(ObservedClickReason.TimeUnavailable);}
                        owned=null!;frame=null; // Drop discarded buffers before awaiting another producer call.
                        continue;
                    }
                    var time=owned.CaptureFacts!.NativeTime!;
                    if((Int128)time.SystemRelative100ns*requestQpc.Frequency<(Int128)requestQpc.Ticks*TimeSpan.TicksPerSecond)
                    {ending="NotFresh";throw new ObservedClickException(ObservedClickReason.ValidationUnavailable);}
                    ValidateFrame(owned); // No geometry or ROI failure is eligible for another candidate.
                    Compare(_source.Frame,owned);
                    now=Guard();
                    if(InspectCandidateTime(owned,now,discarded,requestQpc)!=CandidateTime.Valid)
                        throw TimeRejected(owned,time,now,ObservedClickReason.TimeUnavailable);
                    capture.PublishPrivate(owned,requestedUtc,requested,receivedUtc,received,token);
                    _validations.Add(new(capture.Observation.FrameId,time,stage));
                    if(_validations.Count>3)throw new InvalidOperationException("Unexpected observed verification stage.");
                    ending=number==1?"AcceptedFirst":"AcceptedAfterDiscard";
                    PublishDetails(stage);PublishDetails(stage+":roi-passed");return;
                }
            }
            catch(ObservationCapacityException){Guard();ending="CapacityUnavailable";throw new ObservedClickException(ObservedClickReason.ValidationUnavailable);}
            catch(OperationCanceledException) when(timeout.IsCancellationRequested&&!token.IsCancellationRequested)
            {Guard();throw new ObservedClickException(ObservedClickReason.ValidationUnavailable);}
        }
        catch(ObservedClickException error){if(ending=="Failed")ending=error.Reason==ObservedClickReason.ValidationUnavailable?"ValidationTimedOut":error.Reason.ToString();throw;}
        catch(OperationCanceledException){ending="Cancelled";throw;}
        catch(ObservationDeadlineException){ending="RequestDeadline";throw;}
        catch(AutomationGateway.GatewayRejectedException){ending="AdmissionRejected";throw;}
        catch(Rug.UI.Core.Exceptions.PermissionDeniedException){ending="PermissionDenied";throw;}
        finally{CandidateEvent(stage,Math.Min(number,3),"StageEnd",ending);_verificationStage=null;_candidateNumber=0;}
    }
    private QpcReading CheckVerificationDeadline(QpcReading started,QpcReading latest)
    {
        var now=_environment.ReadQpc();
        if(now.Domain!=started.Domain || now.Frequency!=started.Frequency || now.Frequency<=0 ||
            now.Domain!="Windows boot QPC" || started.Ticks<=0 || now.Ticks<latest.Ticks)
            throw new ObservedClickException(ObservedClickReason.TimeUnavailable);
        // The final check must not depend on timer/cancellation callback scheduling.
        if((Int128)(now.Ticks-started.Ticks)*1000 >= (Int128)VerificationMilliseconds*now.Frequency)
            throw new ObservedClickException(ObservedClickReason.ValidationUnavailable);
        return now;
    }
    private void Compare(CapturedFrame source, CapturedFrame fresh)
    {
        var first=source.CaptureFacts!.Geometry.ImageToClient!.Inverse();
        var second=fresh.CaptureFacts!.Geometry.ImageToClient!.Inverse();
        long difference=0,count=0;
        for(int y=Point.Y-_policy.Radius;y<=Point.Y+_policy.Radius;y++)
        for(int x=Point.X-_policy.Radius;x<=Point.X+_policy.Radius;x++)
        {
            if(!first.TryMapPixel(x,y,out int sx,out int sy) || !second.TryMapPixel(x,y,out int fx,out int fy) ||
                sx<0 || sy<0 || sx>=source.Width || sy>=source.Height || fx<0 || fy<0 || fx>=fresh.Width || fy>=fresh.Height)
                throw new ObservedClickException(ObservedClickReason.CoordinateInvalid);
            int si=checked(sy*source.Stride+sx*4),fi=checked(fy*fresh.Stride+fx*4);
            for(int channel=0;channel<3;channel++)
            {
                int d=Math.Abs(source.Pixels[si+channel]-fresh.Pixels[fi+channel]);
                if(d>_policy.MaxChannelDifference) throw new ObservedClickException(ObservedClickReason.SceneChanged);
                difference+=d;count++;
            }
        }
        if((double)difference/count>_policy.MaxMeanDifference) throw new ObservedClickException(ObservedClickReason.SceneChanged);
    }
    public void Dispose() => _source.Dispose();
}
