#nullable enable
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;

namespace Rug.UI.Core.Services;

/// <summary>
/// Resolves the window under the cursor via user32 (WindowFromPoint -> GA_ROOT),
/// then reads its title, owning process, size and Per-Monitor DPI. Pure inspection;
/// no Rug.Core capability is involved.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowSpyService : IWindowSpyService
{
    public WindowInfo? ResolveAtCurrentCursor()
    {
        if (!WindowNative.GetCursorPos(out WindowNative.POINT pt)) return null;

        nint under = WindowNative.WindowFromPoint(pt);
        if (under == 0) return null;

        // Snap to the top-level owner so we bind the real window, not a child control.
        nint root = WindowNative.GetAncestor(under, WindowNative.GA_ROOT);
        nint hwnd = root != 0 ? root : under;

        string title = GetTitle(hwnd);
        string process = GetProcessName(hwnd);

        int width = 0, height = 0;
        if (WindowNative.GetWindowRect(hwnd, out WindowNative.RECT rc))
        {
            width = rc.Right - rc.Left;
            height = rc.Bottom - rc.Top;
        }

        uint dpi = WindowNative.GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;

        TryGetClientSize(hwnd, out int cw, out int ch);

        return new WindowInfo(hwnd, title, process, width, height, scale, cw, ch);
    }

    public bool BringToFront(nint hwnd) => hwnd != 0 && WindowNative.SetForegroundWindow(hwnd);

    public bool TryGetClientSize(nint hwnd, out int width, out int height)
    {
        width = 0; height = 0;
        if (hwnd == 0 || !WindowNative.GetClientRect(hwnd, out WindowNative.RECT rc)) return false;
        width = rc.Right - rc.Left;
        height = rc.Bottom - rc.Top;
        return width > 0 && height > 0;
    }

    public PointInt CurrentCursorScreen()
        => WindowNative.GetCursorPos(out WindowNative.POINT pt) ? new PointInt(pt.X, pt.Y) : default;

    public PointInt? ClientPointUnderCursor(nint hwnd)
    {
        if (hwnd == 0 || !WindowNative.GetCursorPos(out WindowNative.POINT pt)) return null;

        WindowNative.ScreenToClient(hwnd, ref pt);

        int x = pt.X, y = pt.Y;
        if (TryGetClientSize(hwnd, out int w, out int h))
        {
            x = Math.Clamp(x, 0, w - 1);
            y = Math.Clamp(y, 0, h - 1);
        }
        return new PointInt(x, y);
    }

    private static string GetTitle(nint hwnd)
    {
        var sb = new StringBuilder(512);
        int n = WindowNative.GetWindowTextW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : string.Empty;
    }

    private static string GetProcessName(nint hwnd)
    {
        WindowNative.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return string.Empty;
        try
        {
            using Process? p = Process.GetProcessById((int)pid);
            return p?.ProcessName ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;  // process already exited
        }
    }
}
