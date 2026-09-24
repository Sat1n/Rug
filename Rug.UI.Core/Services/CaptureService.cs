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
public sealed class CaptureService : ICaptureService
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
        => Task.Run(() => GrabFrame(), cancellationToken);

    private CapturedFrame? GrabFrame()
    {
        CapturerHandle? capturer;
        lock (_gate) capturer = _capturer;
        if (capturer is null || capturer.IsInvalid || capturer.IsClosed) return null;

        int rc = RugCoreNative.Rug_GrabFrame(capturer.DangerousGetHandle(), out RugFrame frame);
        if (rc != RugStatus.Ok || frame.Data == 0) return null;

        try
        {
            if (frame.Format != RugPixelFormat.Bgra8 || frame.Width <= 0 || frame.Height <= 0)
                return null;

            int bytes = (int)frame.DataLength;
            var pixels = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(frame.Data, pixels, 0, bytes);
            return new CapturedFrame(pixels, frame.Width, frame.Height, frame.Stride);
        }
        finally
        {
            RugCoreNative.Rug_FreeBuffer(frame.Data);  // native new[] buffer
        }
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
