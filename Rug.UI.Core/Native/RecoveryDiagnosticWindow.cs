#nullable enable
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Rug.UI.Core.Native;

/// <summary>Opt-in controlled recovery target. No hooks, registrations or synthetic human input.</summary>
internal sealed class RecoveryDiagnosticWindow : IDisposable
{
    private readonly Thread _thread;
    private readonly WindowProc _callback;
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _hwnd, _red, _green;
    private long _down, _up;
    private long _afterDown, _afterUp;
    private int _isReady, _disposed;
    private Exception? _failure;
    public Task<nint> Ready => _ready.Task;
    public (long Down, long Up) KeyCounts => (Interlocked.Read(ref _down), Interlocked.Read(ref _up));
    public (long Down, long Up) AfterKeyCounts => (Interlocked.Read(ref _afterDown), Interlocked.Read(ref _afterUp));
    public RecoveryDiagnosticWindow()
    {
        _callback = Message;
        _thread = new Thread(Run) { IsBackground = true, Name = "Rug controlled recovery target" };
        _thread.Start();
    }
    // Test host only: not a simulated human acceptance or a plugin command.
    public void SetReady(bool ready) => SendMessageW(_hwnd, 0x8001, ready ? (nuint)1 : 0, 0);
    private void Run()
    {
        string name = "RugRecovery" + Guid.NewGuid().ToString("N");
        nint instance = GetModuleHandleW(null);
        bool registered = false;
        try
        {
            _red = CreateSolidBrush(0x002020C0); _green = CreateSolidBrush(0x0020B020);
            if (_red == 0 || _green == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var wc = new WindowClass { Proc = Marshal.GetFunctionPointerForDelegate(_callback), Instance = instance, Name = name };
            if (RegisterClassW(ref wc) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            registered = true;
            _hwnd = CreateWindowExW(0, name, "Rug recovery: BLOCKED (red). Click or Space to make READY (green)",
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
            GC.KeepAlive(_callback);
        }
    }
    private nint Message(nint hwnd, uint id, nuint w, nint l)
    {
        if (id == 0x113) { InvalidateRect(hwnd, 0, true); UpdateWindow(hwnd); return 0; }
        if (id == 0x14)
        { GetClientRect(hwnd, out var rect); FillRect((nint)w, ref rect, _isReady != 0 ? _green : _red); return 1; }
        if (id == 0x8001 || id == 0x201 || id == 0x100 && w == 32)
        {
            _isReady = id == 0x8001 ? (w == 0 ? 0 : 1) : 1;
            SetWindowTextW(hwnd, _isReady != 0 ? "Rug recovery: READY (green)" : "Rug recovery: BLOCKED (red)");
            InvalidateRect(hwnd, 0, true); UpdateWindow(hwnd); return 0;
        }
        if (id is 0x100 or 0x104 && w == 66) Interlocked.Increment(ref _down);
        if (id is 0x101 or 0x105 && w == 66) Interlocked.Increment(ref _up);
        if (id is 0x100 or 0x104 && w == 67) Interlocked.Increment(ref _afterDown);
        if (id is 0x101 or 0x105 && w == 67) Interlocked.Increment(ref _afterUp);
        if (id == 2) PostQuitMessage(0);
        return DefWindowProcW(hwnd, id, w, l);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_hwnd != 0) PostMessageW(_hwnd, 0x10, 0, 0);
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Recovery target cleanup timed out.");
        if (_failure is not null) throw new InvalidOperationException("Recovery target failed.", _failure);
    }
    private delegate nint WindowProc(nint hwnd, uint id, nuint w, nint l);
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(nint hwnd, string text);
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
