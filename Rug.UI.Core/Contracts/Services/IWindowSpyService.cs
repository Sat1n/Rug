#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Contracts.Services;

/// <summary>
/// Spy++-style window inspection: resolve the window under the current cursor to
/// its HWND, title, process, size and DPI scale. Used by the crosshair picker.
/// </summary>
public interface IWindowSpyService
{
    /// <summary>
    /// Resolve the top-level window under the current physical cursor position.
    /// Returns null when no window is found.
    /// </summary>
    WindowInfo? ResolveAtCurrentCursor();

    /// <summary>
    /// Best-effort activate <paramref name="hwnd"/> so foreground SendInput lands on
    /// it. Returns false when the OS refuses the focus change.
    /// </summary>
    bool BringToFront(nint hwnd);

    /// <summary>Client-area size of <paramref name="hwnd"/> in physical pixels.</summary>
    bool TryGetClientSize(nint hwnd, out int width, out int height);

    /// <summary>Current physical cursor position in screen coordinates.</summary>
    PointInt CurrentCursorScreen();

    /// <summary>
    /// Client-space point of the current cursor position relative to <paramref name="hwnd"/>,
    /// clamped to its client rect. Returns null when <paramref name="hwnd"/> is 0.
    /// Used by the coordinate picker to hand back scripting-friendly relative coords.
    /// </summary>
    PointInt? ClientPointUnderCursor(nint hwnd);
}
