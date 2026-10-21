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
    internal void PublishDetails(string stage) => InputExecutionBoundary.Observation.Value?.Invoke(Result("pending",stage));
    internal void Validate(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ObservationCallBoundary.Current.Value?.Check(); _validateRun(); _demandCapture();
        _source.Validate(); ValidateFrame(_source.Frame); _source.Validate();
    }
    private NativeFrameTime CheckTime(CapturedFrame frame)
    {
        var t=frame.CaptureFacts?.NativeTime;var now=_environment.ReadQpc();
        if(t is null || now.Domain!="Windows boot QPC" || t.ClockDomain!=now.Domain || now.Frequency<=0 ||
            t.QpcFrequency!=now.Frequency || t.AgeAt(now.Ticks,now.Frequency,now.Domain) is not { } age)
            throw new ObservedClickException(ObservedClickReason.TimeUnavailable);
        if(age.TotalMilliseconds>(_request.MaxAgeMs??_policy.MaxAgeMs)) throw new ObservedClickException(ObservedClickReason.Stale);
        return t;
    }
    private void ValidateFrame(CapturedFrame frame)
    {
        CheckTime(frame);
        if(frame.CaptureFacts?.Geometry.ClientGeometryVerified!=true) throw new ObservedClickException(ObservedClickReason.GeometryChanged);
        _environment.ValidateGeometry(_run,frame);
    }
    internal void StartSideEffect()
    {
        // No IO/callback inside the pool decision. All other checks were just performed.
        _source.Consume(); InputExecutionBoundary.MarkStarted();
    }
    /// <summary>@shape stage: one of at most three bounded fresh comparisons; source/cache are never replaced.
    /// @source capture: Contracts/Services/ICaptureService.cs#class:ICaptureService</summary>
    internal async Task VerifyAsync(string stage, CancellationToken token)
    {
        Validate(token);
        var started=_environment.ReadQpc();
        using var timeout=new CancellationTokenSource(_policy.VerificationTimeoutMs);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,timeout.Token);
        try
        {
            using var capture=_pool.Capture(_session,privateFrame:true);
            long requested=_pool.Timestamp;var requestedUtc=_pool.UtcNow;
            var frame=await (_capture is IBoundedFrameCapture bounded ? bounded.GrabFrameBoundedAsync(_pool.Limits.MaxFrameBytes,linked.Token)
                : _capture.GrabFrameAsync(linked.Token)).ConfigureAwait(false);
            long received=_pool.Timestamp;var receivedUtc=_pool.UtcNow;
            // Await the real service even if it ignores cancellation. Stop retains this work/lease.
            Validate(token);
            CheckVerificationDeadline(started);
            if(timeout.IsCancellationRequested) throw new ObservedClickException(ObservedClickReason.ValidationUnavailable);
            if(frame is null) throw new ObservedClickException(ObservedClickReason.ValidationUnavailable);
            var owned=capture.Detach(frame); ValidateFrame(owned);
            var time=CheckTime(owned);
            if(started.Domain!=time.ClockDomain || started.Frequency!=time.QpcFrequency ||
                (Int128)time.SystemRelative100ns*started.Frequency < (Int128)started.Ticks*TimeSpan.TicksPerSecond)
                throw new ObservedClickException(ObservedClickReason.ValidationUnavailable);
            capture.PublishPrivate(owned,requestedUtc,requested,receivedUtc,received,token);
            _validations.Add(new(capture.Observation.FrameId,time,stage));
            if(_validations.Count>3) throw new InvalidOperationException("Unexpected observed verification stage.");
            PublishDetails(stage);
            Compare(_source.Frame,owned);
            Validate(token); CheckVerificationDeadline(started); linked.Token.ThrowIfCancellationRequested();
        }
        catch(ObservationCapacityException) { throw new ObservedClickException(ObservedClickReason.ValidationUnavailable); }
        catch(OperationCanceledException) when(timeout.IsCancellationRequested && !token.IsCancellationRequested)
        { Validate(token); throw new ObservedClickException(ObservedClickReason.ValidationUnavailable); }
    }
    private void CheckVerificationDeadline(QpcReading started)
    {
        var now=_environment.ReadQpc();
        if(now.Domain!=started.Domain || now.Frequency!=started.Frequency || now.Frequency<=0 ||
            started.Ticks<=0 || now.Ticks<started.Ticks)
            throw new ObservedClickException(ObservedClickReason.TimeUnavailable);
        // The final check must not depend on timer/cancellation callback scheduling.
        if((Int128)(now.Ticks-started.Ticks)*1000 >= (Int128)_policy.VerificationTimeoutMs*now.Frequency)
            throw new ObservedClickException(ObservedClickReason.ValidationUnavailable);
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
