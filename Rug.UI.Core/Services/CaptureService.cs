#nullable enable
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;

namespace Rug.UI.Core.Services;

/// <summary>
/// Managed WGC capture facade over the Rug.Core C-ABI. Creates one native capturer
/// per session, copies each grabbed frame into a managed <see cref="CapturedFrame"/>,
/// and frees the native buffer deterministically. Native calls run on a background
/// (MTA) thread via Task.Run — the WGC/WinRT path requires MTA.
/// </summary>
public sealed class CaptureService(bool exactBoundWindow = false) : ICaptureService, IBoundedFrameCapture
{
    private readonly object _gate = new();
    private CapturerHandle? _capturer;

    public bool IsCapturing
    {
        get { lock (_gate) return _capturer is { IsInvalid: false, IsClosed: false }; }
    }

    public Task StartAsync(nint hwnd, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (hwnd == 0)
                throw new RugNativeException(nameof(RugCoreNative.Rug_CreateCapturer), RugStatus.ErrInvalidParam);

            var config = new RugCaptureConfig { TargetWindow = hwnd, OcrUpscale = 1, CaptureCursor = 0 };
            int rc = exactBoundWindow ? RugCoreNative.Rug_CreateCapturerExact(in config, out nint h)
                : RugCoreNative.Rug_CreateCapturer(in config, out h);
            if (rc != RugStatus.Ok || h == 0)
                throw new RugNativeException(nameof(RugCoreNative.Rug_CreateCapturer), rc);

            var created = new CapturerHandle(h);
            lock (_gate)
            {
                _capturer?.Dispose();   // replace any previous session
                _capturer = created;
            }
        }, cancellationToken);

    public Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken = default)
        => GrabFrameBoundedAsync(64L * 1024 * 1024, cancellationToken);
    Task<CapturedFrame?> IBoundedFrameCapture.GrabFrameBoundedAsync(long maxBytes, CancellationToken cancellationToken)
        => GrabFrameBoundedAsync(maxBytes, cancellationToken);
    private Task<CapturedFrame?> GrabFrameBoundedAsync(long maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0 || maxBytes > 64L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        return Task.Run(() => GrabFrame(maxBytes), cancellationToken);
    }

    private CapturedFrame? GrabFrame(long maxBytes)
    {
        CapturerHandle? capturer;
        bool retained = false;
        lock (_gate)
        {
            capturer = _capturer;
            if (capturer is null || capturer.IsInvalid || capturer.IsClosed) return null;
            capturer.DangerousAddRef(ref retained);
        }
        try { return GrabRetained(capturer, maxBytes); }
        finally { if (retained) capturer.DangerousRelease(); }
    }
    private static CapturedFrame? GrabRetained(CapturerHandle capturer, long maxBytes)
    {

        var facts = RugCaptureFacts.Create();
        int rc = RugCoreNative.Rug_GrabFrameWithFacts(capturer.DangerousGetHandle(), out RugFrame frame, ref facts);
        if (!HasFrame(rc)) return null;
        if (frame.Data == 0) throw new InvalidDataException("Native capture reported success without frame data.");

        try
        {
            int bytes = ValidateLayout(frame, maxBytes);
            var pixels = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(frame.Data, pixels, 0, bytes);
            return new CapturedFrame(pixels, frame.Width, frame.Height, frame.Stride) { CaptureFacts = ConvertFacts(facts, frame), NativeCaptureFacts = facts };
        }
        finally
        {
            RugCoreNative.Rug_FreeBuffer(frame.Data);  // native new[] buffer
        }
    }
    /// <summary>@shape f/frame: atomically returned native V1 facts/pixels; no public HWND.
    /// @source f: Native/Rug.Core.Native.cs#class:RugCaptureFacts</summary>
    internal static CaptureFacts ConvertFacts(RugCaptureFacts f, RugFrame frame)
    {
        if (f.Version != 1 || f.StructSize != System.Runtime.InteropServices.Marshal.SizeOf<RugCaptureFacts>() ||
            f.BufferWidth != frame.Width || f.BufferHeight != frame.Height || f.ContentWidth <= 0 || f.ContentHeight <= 0)
            throw new InvalidDataException("Native capture facts do not match their frame.");
        var native = (f.Flags & 1) != 0 ? new NativeFrameTime(f.SystemRelative100ns, f.QpcFrequency, f.QpcSample) : null;
        if (native?.AgeAtNativeSample is null) native = null;
        bool verified = (f.Flags & 6) == 6 && f.GeometryStatus == 1 && native is not null &&
            f.GeometryBeforeQpc > 0 && f.GeometryAfterQpc >= f.GeometryBeforeQpc && f.QpcSample >= f.GeometryAfterQpc &&
            (Int128)f.SystemRelative100ns * f.QpcFrequency >= (Int128)f.GeometryBeforeQpc * TimeSpan.TicksPerSecond &&
            f.ContentWidth == frame.Width && f.ContentHeight == frame.Height &&
            f.BoundLifetime != 0 && f.BoundLifetime == f.CapturedLifetime && f.BoundWindow == f.CapturedWindow &&
            f.Dpi > 0 && f.ClientWidth > 0 && f.ClientHeight > 0 && f.ImageClientX >= 0 && f.ImageClientY >= 0 &&
            (long)f.ImageClientX + f.ClientWidth <= frame.Width && (long)f.ImageClientY + f.ClientHeight <= frame.Height;
        PixelTransform? image = verified ? new("capture-image-pixels", "client-physical-pixels",
            new(f.ImageClientX, f.ImageClientY, f.ClientWidth, f.ClientHeight), new(0, 0, f.ClientWidth, f.ClientHeight),
            1, 1, -f.ImageClientX, -f.ImageClientY) : null;
        PixelTransform? screen = verified ? new("client-physical-pixels", "screen-physical-pixels",
            new(0, 0, f.ClientWidth, f.ClientHeight), new(f.ClientScreenX, f.ClientScreenY, f.ClientWidth, f.ClientHeight),
            1, 1, f.ClientScreenX, f.ClientScreenY) : null;
        return new(native, new(f.BoundLifetime != 0 ? f.BoundLifetime.ToString("X") : null,
                f.CapturedLifetime != 0 ? f.CapturedLifetime.ToString("X") : null, (f.Flags & 4) != 0),
            f.ContentWidth, f.ContentHeight, f.BufferWidth, f.BufferHeight, f.GeometryStatus, f.GeometryBeforeQpc, f.GeometryAfterQpc, f.Dpi,
            new(verified ? "sampled exact-window DWM client offset" : $"unavailable ({f.GeometryStatus})",
                f.ClientWidth > 0 ? f.ClientWidth : null, f.ClientHeight > 0 ? f.ClientHeight : null, verified, image, screen), f.QpcFrequency);
    }
    internal static int ValidateLayout(RugFrame frame, long maxBytes = 64L * 1024 * 1024)
    {
        if (frame.Format != RugPixelFormat.Bgra8 || frame.Width <= 0 || frame.Height <= 0 ||
            frame.Stride < (long)frame.Width * 4 || (long)frame.Stride * frame.Height > frame.DataLength || frame.DataLength == 0)
            throw new InvalidDataException("Native capture frame layout is invalid.");
        if (frame.DataLength > maxBytes) throw new ObservationCapacityException("Native frame exceeds the host's managed-copy byte limit.");
        return checked((int)frame.DataLength);
    }
    internal static bool HasFrame(int status)
    {
        if (status == RugStatus.ErrCaptureFailed) return false; // ABI also uses this code for no available frame.
        if (status != RugStatus.Ok) throw new RugNativeException(nameof(RugCoreNative.Rug_GrabFrame), status);
        return true;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _capturer?.Dispose();
            _capturer = null;
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _capturer?.Dispose();
            _capturer = null;
        }
        return ValueTask.CompletedTask;
    }
}
