#nullable enable
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Rug.UI.Core.Native;

/// <summary>Opt-in controlled capture geometry marker target. No hooks, registrations or synthetic human input.</summary>
internal sealed class CaptureGeometryDiagnosticWindow : IDisposable
{
    private readonly Thread _thread;
    private readonly WindowProc _callback;
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _hwnd, _red, _green;
    private int _disposed, _tick;
    private nint _marker;
    private Exception? _failure;
    public Task<nint> Ready => _ready.Task;
    public CaptureGeometryDiagnosticWindow()
    {
        _callback = Message;
        _thread = new Thread(Run) { IsBackground = true, Name = "Rug controlled geometry target" };
        _thread.Start();
    }
    // Test host only: not a simulated human acceptance or a plugin command.
    public void Resize(int width, int height) => SendMessageW(_hwnd, 0x8002, (nuint)width, height);
    public bool MoveToOtherMonitor()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) throw new PlatformNotSupportedException("WGC geometry requires Windows 10 1809 or later.");
        var previous = SetThreadDpiAwarenessContext(-4);
        try
        {
            nint current = WindowNative.MonitorFromWindow(_hwnd, 2);
            WindowNative.RECT? destination = null;
            MonitorProc callback = (nint monitor, nint dc, ref NativeRect rect, nint data) =>
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return false;
                var info = new WindowNative.MONITORINFOEX { cbSize = Marshal.SizeOf<WindowNative.MONITORINFOEX>(), szDevice = "" };
                if (monitor != current && WindowNative.GetMonitorInfoW(monitor, ref info)) destination ??= info.rcWork;
                return true;
            };
            if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (destination is not { } work) return false;
            SendMessageW(_hwnd, 0x8003, unchecked((nuint)(nint)(work.Left + 30)), work.Top + 30);
            return WindowNative.MonitorFromWindow(_hwnd, 2) != current;
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }
    private void Run()
    {
        var dpiContext = SetThreadDpiAwarenessContext(-4);
        string name = "RugCaptureGeometry" + Guid.NewGuid().ToString("N");
        nint instance = GetModuleHandleW(null);
        bool registered = false;
        try
        {
            _marker = CreateSolidBrush(0x000000FF);
            _red = CreateSolidBrush(0x002020C0); _green = CreateSolidBrush(0x0020B020);
            if (_red == 0 || _green == 0 || _marker == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var wc = new WindowClass { Proc = Marshal.GetFunctionPointerForDelegate(_callback), Instance = instance, Name = name };
            if (RegisterClassW(ref wc) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            registered = true;
            _hwnd = CreateWindowExW(0, name, "Rug capture geometry: red marker at client (80,70)",
                0x00cf0000, 100, 100, 640, 420, 0, 0, instance, 0);
            if (_hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            ShowWindow(_hwnd, 5); UpdateWindow(_hwnd);
            if (SetTimer(_hwnd, 1, 33, 0) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            _ready.TrySetResult(_hwnd);
            int result;
            while ((result = GetMessageW(out var message, 0, 0, 0)) > 0)
            { TranslateMessage(ref message); DispatchMessageW(ref message); }
            if (result < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch (Exception error) { _failure = error; _ready.TrySetException(error); }
        finally
        {
            if (_hwnd != 0) { KillTimer(_hwnd, 1); DestroyWindow(_hwnd); }
            if (registered) UnregisterClassW(name, instance);
            if (_red != 0) DeleteObject(_red);
            if (_green != 0) DeleteObject(_green);
            if (_marker != 0) DeleteObject(_marker);
            if (dpiContext != 0) SetThreadDpiAwarenessContext(dpiContext);
            GC.KeepAlive(_callback);
        }
    }
    private nint Message(nint hwnd, uint id, nuint w, nint l)
    {
        if (id == 0x8002) { SetWindowPos(hwnd, 0, 0, 0, (int)w, (int)l, 0x16); return 0; }
        if (id == 0x8003) { SetWindowPos(hwnd, 0, unchecked((int)w), (int)l, 0, 0, 0x15); return 0; }
        if (id == 0x2e0)
        {
            var suggested = Marshal.PtrToStructure<NativeRect>(l);
            SetWindowPos(hwnd, 0, suggested.Left, suggested.Top, suggested.Right-suggested.Left, suggested.Bottom-suggested.Top, 0x14);
            return 0;
        }
        if (id == 0x113) { _tick++; InvalidateRect(hwnd, 0, true); UpdateWindow(hwnd); return 0; }
        if (id == 0x14)
        { GetClientRect(hwnd, out var rect); FillRect((nint)w, ref rect, _green);
            var marker = new NativeRect { Left = 80, Top = 70, Right = 100, Bottom = 90 };
            FillRect((nint)w, ref marker, _marker);
            var pulse = new NativeRect { Right = 12, Bottom = 12 }; FillRect((nint)w, ref pulse, (_tick & 1) == 0 ? _red : _green); return 1; }
        if (id == 2) { _hwnd = 0; PostQuitMessage(0); }
        return DefWindowProcW(hwnd, id, w, l);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_hwnd != 0) PostMessageW(_hwnd, 0x10, 0, 0);
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Capture geometry target cleanup timed out.");
        if (_failure is not null) throw new InvalidOperationException("Capture geometry target failed.", _failure);
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    private delegate nint WindowProc(nint hwnd, uint id, nuint w, nint l);
    private delegate bool MonitorProc(nint monitor, nint dc, ref NativeRect rect, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc callback, nint data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass { public uint Style; public nint Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu, Name; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WindowClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint ex, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessageW(out NativeMessage message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint id, nuint w, nint l);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint id, nuint w, nint l);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint id, nuint w, nint l);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref NativeRect rect, nint brush);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}
