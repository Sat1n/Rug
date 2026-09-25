using System.ComponentModel;
using System.Runtime.InteropServices;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;

namespace Rug.UI.Core.Services;

/// <summary>
/// Maps a physical client pixel to physical screen space. The WinUI host declares
/// PerMonitorV2 DPI awareness, so Win32 performs the per-monitor mapping; applying
/// DpiHelper here would scale physical pixels twice.
/// </summary>
public sealed class CoordinateMapper : ICoordinateMapper
{
    public PointInt ClientToScreen(nint hwnd, int x, int y)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Win32 coordinate mapping requires Windows 10 or later.");
        if (hwnd == 0) throw new ArgumentException("A bound window is required.", nameof(hwnd));
        if (!WindowNative.GetClientRect(hwnd, out WindowNative.RECT rect))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the target client area.");
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height)
            throw new ArgumentOutOfRangeException(nameof(x), "Click point must be inside the target client area.");
        var point = new WindowNative.POINT { X = x, Y = y };
        if (!WindowNative.ClientToScreen(hwnd, ref point))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not map the client point to screen pixels.");
        return new PointInt(point.X, point.Y);
    }
}
