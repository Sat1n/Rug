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
    internal Operation Capture(Session session)
    {
        lock (_gate)
        {
            CheckOpen(session);
            // Reserve producer output AND detached host copy before invoking the producer; no capacity queue.
            Reserve(checked(2 * Limits.MaxFrameBytes));
            if (_frames + _captures >= Limits.MaxFrames) { _reserved -= 2 * Limits.MaxFrameBytes; _operations--; throw new ObservationCapacityException("Observation frame capacity exhausted."); }
            _captures++;
            return new Operation(this, session, null, 2 * Limits.MaxFrameBytes, ++session.CaptureSequence, true);
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
    }
    internal sealed class Operation(ObservationFramePool pool, Session session, Entry? entry, long reserved, long sequence, bool capture) : IDisposable
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
            return new CapturedFrame(frame.Pixels.AsSpan(0, checked((int)bytes)).ToArray(), frame.Width, frame.Height, frame.Stride) { CaptureFacts = frame.CaptureFacts };
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
                session.Current = new(frame, observation);
                return observation;
            }
        }
        public void Dispose()
        {
            lock (pool._gate)
            {
                if (_disposed) return; _disposed = true;
                pool._reserved -= _reservation; _reservation = 0; pool._operations--;
                if (capture) pool._captures--; else pool.Drop(entry);
            }
        }
    }
}
