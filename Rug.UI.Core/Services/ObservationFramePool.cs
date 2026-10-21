#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Process-wide default, shared across run factories; replace only at the trusted composition boundary.
/// @shape frames: one cached entry/session + active leases; reservations bound capture copies and ROI allocations.
/// @source identity: Models/RunIdentity.cs#class:RunIdentity</summary>
public sealed class ObservationFramePool
{
    public static ObservationFramePool Shared { get; } = new();
    private readonly object _gate = new();
    private readonly HashSet<Session> _sessions = [];
    private readonly TimeProvider _clock;
    private readonly Guid _clockId = Guid.NewGuid();
    private long _bytes, _reserved;
    private int _frames, _operations, _captures;
    private long _inputRevision;
    private int _activeInputs;
    public const int MaxObservationsPerFrame = 64;
    public ObservationActionUsage ActionUsage { get { lock (_gate) return new(_sessions.Sum(s=>s.Current?.Observations.Count??0), _activeInputs, _inputRevision); } }
    public ObservationLimits Limits { get; }
    public ObservationFramePool(ObservationLimits? limits = null, TimeProvider? clock = null)
    {
        Limits = limits ?? new(); _clock = clock ?? TimeProvider.System;
        if (Limits.MaxFrameBytes <= 0 || Limits.MaxFrameBytes > 64L * 1024 * 1024 ||
            Limits.MaxResidentBytes < checked(2 * Limits.MaxFrameBytes) || Limits.MaxFrames <= 0 ||
            Limits.MaxOperations <= 0 || Limits.MaxSessions <= 0) throw new ArgumentOutOfRangeException(nameof(limits));
    }
    public ObservationUsage Usage { get { lock (_gate) return new(_bytes, _reserved, _frames, _operations, _sessions.Count); } }
    public TimeSpan HostReceiptAge(ObservationEnvelope observation)
    {
        if (observation.Time.ClockId != _clockId) throw new ArgumentException("Foreign monotonic clock.", nameof(observation));
        return _clock.GetElapsedTime(observation.Time.ReceivedTimestamp, _clock.GetTimestamp());
    }
    internal long Timestamp => _clock.GetTimestamp();
    internal DateTimeOffset UtcNow => _clock.GetUtcNow();
    internal Session Open(RunIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            if (_sessions.Count >= Limits.MaxSessions) throw new ObservationCapacityException("Observation session capacity exhausted.");
            var session = new Session(this, ObservationRun.From(identity)); _sessions.Add(session); return session;
        }
    }
    internal static long FrameBytes(CapturedFrame frame)
    {
        long bytes = (long)frame.Stride * frame.Height;
        if (frame.Pixels is null || frame.Width <= 0 || frame.Height <= 0 || frame.Stride < (long)frame.Width * 4 ||
            bytes <= 0 || bytes > frame.Pixels.LongLength) throw new InvalidDataException("Invalid BGRA8 frame dimensions/stride/buffer.");
        return bytes;
    }
    internal static Rect Region(CapturedFrame frame, Rect? region)
    {
        var roi = region ?? new Rect(0, 0, frame.Width, frame.Height);
        if (roi.X < 0 || roi.Y < 0 || roi.Width <= 0 || roi.Height <= 0 ||
            (long)roi.X + roi.Width > frame.Width || (long)roi.Y + roi.Height > frame.Height)
            throw new ArgumentOutOfRangeException(nameof(region), "Region must fit inside captured image pixels.");
        return roi;
    }
    internal Operation Capture(Session session, bool privateFrame = false)
    {
        lock (_gate)
        {
            CheckOpen(session);
            // Reserve producer output AND detached host copy before invoking the producer; no capacity queue.
            Reserve(checked(2 * Limits.MaxFrameBytes));
            if (_frames + _captures >= Limits.MaxFrames) { _reserved -= 2 * Limits.MaxFrameBytes; _operations--; throw new ObservationCapacityException("Observation frame capacity exhausted."); }
            _captures++;
            return new Operation(this, session, null, 2 * Limits.MaxFrameBytes, privateFrame ? -1 : ++session.CaptureSequence, true, _inputRevision, _activeInputs == 0);
        }
    }
    internal Operation Read(Session session, string? reference, Rect? region)
    {
        lock (_gate)
        {
            CheckOpen(session);
            var entry = session.Current ?? throw new ObservationReferenceException();
            if (reference is not null && reference != entry.Observation.FrameReference) throw new ObservationReferenceException();
            var roi = Region(entry.Frame!, region); // Current cache ownership holds one reference under this lock.
            long crop = region is null ? 0 : checked((long)roi.Width * roi.Height * 4);
            Reserve(crop); entry.Holders++;
            return new Operation(this, session, entry, crop, 0, false);
        }
    }
    private void Reserve(long bytes)
    {
        if (_operations >= Limits.MaxOperations || bytes > Limits.MaxResidentBytes - _bytes - _reserved)
            throw new ObservationCapacityException("Observation byte/operation capacity exhausted; retry only as an explicit new request.");
        _operations++; _reserved += bytes;
    }
    private void CheckOpen(Session session)
    { if (!_sessions.Contains(session)) throw new ObjectDisposedException("Observation run"); }
    internal IDisposable BeginInput()
    {
        lock (_gate) _activeInputs++;
        return new InputScope(this);
    }
    /// <summary>Trusted host input sequences outside Lua (DevTools). Conservatively retire action references at entry.</summary>
    public IDisposable BeginExternalInput()
    {
        var scope=BeginInput(); InvalidateActions(); return scope;
    }
    private sealed class InputScope(ObservationFramePool pool) : IDisposable
    {
        private bool closed;
        public void Dispose() { lock (pool._gate) { if (!closed) { closed=true; pool._activeInputs--; } } }
    }
    internal void InvalidateActions()
    {
        lock (_gate) InvalidateActionsLocked();
    }
    private void InvalidateActionsLocked()
    {
        _inputRevision++;
        foreach(var session in _sessions) if(session.Current is { } e) e.ActionEligible=false;
    }
    internal ObservationEnvelope RegisterRecognition(Session session, ObservationEnvelope observation)
    {
        lock (_gate)
        {
            CheckOpen(session);
            // Late results retain metadata, never resurrect old action qualification.
            if (session.Current is {ActionEligible:true} e && e.Observation.FrameId==observation.FrameId)
            {
                if(e.Observations.Count>=MaxObservationsPerFrame) throw new ObservationCapacityException("Observation identity capacity exhausted.");
                e.Observations.Add(observation.ObservationId,observation);
            }
            return observation;
        }
    }
    internal ActionLease ResolveAction(Session session, ObservedClickRequest request)
    {
        lock (_gate)
        {
            CheckOpen(session);
            if (session.Run.RunGeneration!=request.RunGeneration || session.Current is not {ActionEligible:true} e ||
                e.Observation.FrameReference!=request.FrameReference || e.Observation.FrameId!=request.FrameId ||
                !e.Observations.TryGetValue(request.ObservationId,out var observation)) throw new ObservedClickException(ObservedClickReason.InvalidReference);
            var hold=Read(session,request.FrameReference,null);
            return new(this,session,e,hold,observation,_inputRevision);
        }
    }
    internal sealed class ActionLease(ObservationFramePool pool, Session session, Entry source, Operation hold,
        ObservationEnvelope observation, long revision) : IDisposable
    {
        private bool consumed, disposed;
        internal CapturedFrame Frame => hold.Frame;
        internal ObservationEnvelope Observation => observation;
        internal void Validate()
        {
            lock(pool._gate)
            {
                pool.CheckOpen(session);
                if(disposed || session.Current!=source || pool._inputRevision!=revision || (!consumed && !source.ActionEligible))
                    throw new ObservedClickException(ObservedClickReason.InvalidReference);
            }
        }
        internal void Consume()
        {
            lock(pool._gate) { Validate(); pool.InvalidateActionsLocked(); revision=pool._inputRevision; consumed=true; }
        }
        public void Dispose() { lock(pool._gate) { if(disposed)return; disposed=true; hold.Dispose(); } }
    }
    private void Drop(Entry? entry)
    {
        if (entry is not null && --entry.Holders == 0)
        { _bytes -= entry.Observation.Image.ByteCount; _frames--; entry.Frame = null; }
    }
    internal sealed class Session(ObservationFramePool pool, ObservationRun run) : IDisposable
    {
        internal ObservationRun Run { get; } = run;
        internal Entry? Current;
        internal long CaptureSequence;
        public void Dispose()
        {
            lock (pool._gate)
            {
                if (!pool._sessions.Remove(this)) return;
                pool.Drop(Current); Current = null;
            }
        }
    }
    internal sealed class Entry(CapturedFrame frame, ObservationEnvelope observation)
    {
        internal CapturedFrame? Frame = frame;
        internal ObservationEnvelope Observation { get; } = observation;
        internal int Holders = 1; // cache ownership; each actual recognition retains a separate lease.
        internal bool ActionEligible;
        internal Dictionary<Guid, ObservationEnvelope> Observations = new() { [observation.ObservationId] = observation };
    }
    internal sealed class Operation(ObservationFramePool pool, Session session, Entry? entry, long reserved, long sequence, bool capture,
        long inputRevision = 0, bool inputIdle = true) : IDisposable
    {
        private long _reservation = reserved;
        private bool _disposed;
        internal CapturedFrame Frame => entry?.Frame ?? throw new ObjectDisposedException("Observation frame lease");
        internal ObservationEnvelope Observation => entry!.Observation;
        internal CapturedFrame Detach(CapturedFrame frame)
        {
            long bytes = FrameBytes(frame);
            if (bytes > pool.Limits.MaxFrameBytes || frame.Pixels.LongLength > pool.Limits.MaxFrameBytes)
                throw new ObservationCapacityException("Frame exceeds MaxFrameBytes.");
            // The producer is trusted, but retaining its mutable buffer would leak ownership. Copy only bounded frame bytes.
            return new CapturedFrame(frame.Pixels.AsSpan(0, checked((int)bytes)).ToArray(), frame.Width, frame.Height, frame.Stride) { CaptureFacts = frame.CaptureFacts, NativeCaptureFacts = frame.NativeCaptureFacts };
        }
        /// <summary>@shape frame: private, prevalidated bounded copy/null; null clears cache without minting IDs.
        /// @source caller: Services/VisualEngineBridge.cs#function:CaptureAsync</summary>
        internal ObservationEnvelope? Publish(CapturedFrame? frame, DateTimeOffset requestedUtc, long requested, DateTimeOffset receivedUtc,
            long received, CancellationToken token)
        {
            lock (pool._gate)
            {
                pool.CheckOpen(session); token.ThrowIfCancellationRequested();
                if (_disposed || session.CaptureSequence != sequence) throw new OperationCanceledException("Capture superseded before publication.");
                ObservationCallBoundary.Current.Value?.Check(); // Final publication decision, before IDs/cache mutation.
                if (frame is null) { pool.Drop(session.Current); session.Current = null; return null; }
                long bytes = FrameBytes(frame);
                var observation = new ObservationEnvelope(2, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"), session.Run,
                    new(frame.Width, frame.Height, frame.Stride, bytes),
                    new(pool._clockId, pool._clock.TimestampFrequency, "host TimeProvider monotonic; UTC informational only",
                        requestedUtc, receivedUtc, pool.UtcNow, requested, received, pool.Timestamp, NativeTime: frame.CaptureFacts?.NativeTime),
                    frame.CaptureFacts?.Geometry ?? new(), CaptureFacts: frame.CaptureFacts);
                pool.Drop(session.Current);
                pool._reserved -= _reservation; _reservation = 0;
                pool._bytes += bytes; pool._frames++;
                session.Current = new(frame, observation) { ActionEligible = inputIdle && pool._activeInputs==0 && inputRevision==pool._inputRevision };
                return observation;
            }
        }
        internal void PublishPrivate(CapturedFrame frame, DateTimeOffset requestedUtc, long requested,
            DateTimeOffset receivedUtc, long received, CancellationToken token)
        {
            lock(pool._gate)
            {
                pool.CheckOpen(session); token.ThrowIfCancellationRequested(); ObservationCallBoundary.Current.Value?.Check();
                if(_disposed || sequence!=-1 || entry is not null) throw new InvalidOperationException("Private capture invalid.");
                long bytes=FrameBytes(frame);
                var envelope=new ObservationEnvelope(2,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid().ToString("N"),session.Run,
                    new(frame.Width,frame.Height,frame.Stride,bytes),new(pool._clockId,pool._clock.TimestampFrequency,"host TimeProvider",requestedUtc,receivedUtc,pool.UtcNow,
                        requested,received,pool.Timestamp,NativeTime:frame.CaptureFacts?.NativeTime),frame.CaptureFacts?.Geometry??new(),CaptureFacts:frame.CaptureFacts);
                pool._reserved-=_reservation; _reservation=0; pool._bytes+=bytes; pool._frames++;
                entry=new(frame,envelope); // Only this operation owns it; never becomes session.Current.
            }
        }
        public void Dispose()
        {
            lock (pool._gate)
            {
                if (_disposed) return; _disposed = true;
                pool._reserved -= _reservation; _reservation = 0; pool._operations--;
                if (capture) pool._captures--;
                pool.Drop(entry);
            }
        }
    }
}
