#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Contracts.Services;

/// <summary>
/// WGC window capture over the Rug.Core C-ABI. Holds at most one native capturer;
/// <see cref="StartAsync"/> replaces any previous session. Frames are copied into
/// managed memory so callers never touch native buffers.
/// </summary>
public interface ICaptureService : IAsyncDisposable
{
    /// <summary>True while a capturer session is open.</summary>
    bool IsCapturing { get; }

    /// <summary>Start (or restart) capture of <paramref name="hwnd"/>.</summary>
    Task StartAsync(nint hwnd, CancellationToken cancellationToken = default);

    /// <summary>Grab the latest frame as managed BGRA8, or null when not capturing.</summary>
    Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken = default);

    /// <summary>Close the current capture session and release native resources.</summary>
    Task StopAsync();
}
