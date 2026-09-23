using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rug.UI.Core.Native;

/// <summary>
/// user32.dll windowing interop used by the Spy++-style crosshair picker. This is
/// OS window inspection (point -> HWND, title, process, size, DPI) — NOT Rug.Core
/// capability interop — but it still lives in the single managed native boundary
/// (Rug.UI.Core/Native) so Rug.UI never P/Invokes directly (AGENTS.md §5.3).
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class WindowNative
{
    private const string User32 = "user32.dll";

    internal const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport(User32)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport(User32)]
    internal static extern nint WindowFromPoint(POINT point);

    [DllImport(User32)]
    internal static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(nint hwnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport(User32)]
    internal static extern bool GetWindowRect(nint hwnd, out RECT lpRect);

    [DllImport(User32)]
    internal static extern bool GetClientRect(nint hwnd, out RECT lpRect);

    [DllImport(User32)]
    internal static extern bool ScreenToClient(nint hwnd, ref POINT lpPoint);

    [DllImport(User32)]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint lpdwProcessId);

    [DllImport(User32)]
    internal static extern uint GetDpiForWindow(nint hwnd);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hwnd);
}
