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
public sealed class CaptureService : ICaptureService, IBoundedFrameCapture
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
            int rc = RugCoreNative.Rug_CreateCapturer(in config, out nint h);
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

        int rc = RugCoreNative.Rug_GrabFrame(capturer.DangerousGetHandle(), out RugFrame frame);
        if (!HasFrame(rc)) return null;
        if (frame.Data == 0) throw new InvalidDataException("Native capture reported success without frame data.");

        try
        {
            int bytes = ValidateLayout(frame, maxBytes);
            var pixels = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(frame.Data, pixels, 0, bytes);
            return new CapturedFrame(pixels, frame.Width, frame.Height, frame.Stride);
        }
        finally
        {
            RugCoreNative.Rug_FreeBuffer(frame.Data);  // native new[] buffer
        }
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
