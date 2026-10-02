#nullable enable
using System.Runtime.InteropServices;

namespace Rug.UI.Core.Native;

/// <summary>Opt-in diagnostic target/probes only; not part of the Raw Input event source.</summary>
internal sealed class RawInputDiagnosticWindow : IDisposable
{
    internal sealed record Counters(long Move, long MouseDown, long MouseUp, long KeyDown, long KeyUp, long InjectedKeyboard, long InjectedMouse, long Unflagged, long TaggedKeyboard, long TaggedMouse);
    public readonly TaskCompletionSource<nint> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly WindowsRawInputReceiver.WindowProc _proc;
    private readonly HookProc _keyboard, _mouse;
    private readonly System.Collections.Concurrent.ConcurrentQueue<TaskCompletionSource> _barriers = new();
    private nint _hwnd, _keyHook, _mouseHook;
    private long _move, _down, _up, _keyDown, _keyUp, _injectedKey, _injectedMouse, _unflagged, _taggedKey, _taggedMouse;
    public Counters Counts => new(Interlocked.Read(ref _move), Interlocked.Read(ref _down), Interlocked.Read(ref _up), Interlocked.Read(ref _keyDown), Interlocked.Read(ref _keyUp),
        Interlocked.Read(ref _injectedKey), Interlocked.Read(ref _injectedMouse), Interlocked.Read(ref _unflagged), Interlocked.Read(ref _taggedKey), Interlocked.Read(ref _taggedMouse));
    public RawInputDiagnosticWindow()
    {
        _proc = Message;
        _keyboard = (code, w, l) => Hook(code, w, l, true);
        _mouse = (code, w, l) => Hook(code, w, l, false);
        _thread = new Thread(Run) { IsBackground = true, Name = "Rug raw diagnostics isolated window" }; _thread.Start();
    }
    private nint Hook(int code, nuint w, nint l, bool keyboard)
    {
        if (code >= 0)
        {
            uint flags = ReadHookFlags(l, keyboard);
            if ((flags & (keyboard ? 0x10u : 1u)) != 0)
            {
                if (keyboard) Interlocked.Increment(ref _injectedKey); else Interlocked.Increment(ref _injectedMouse);
                if (ReadHookExtraInformation(l, keyboard) == Models.InputObservation.RugSoftwareInputTag)
                { if (keyboard) Interlocked.Increment(ref _taggedKey); else Interlocked.Increment(ref _taggedMouse); }
            }
            else Interlocked.Increment(ref _unflagged);
        }
        return CallNextHookEx(0, code, w, l); // Observes only; never suppresses input.
    }
    internal static uint ReadHookFlags(nint data, bool keyboard) => unchecked((uint)Marshal.ReadInt32(data, keyboard ? 8 : 12));
    internal static nuint ReadHookExtraInformation(nint data, bool keyboard) => unchecked((nuint)Marshal.ReadIntPtr(data, keyboard ? 16 : IntPtr.Size == 8 ? 24 : 20));
    public bool Activate() { ShowWindow(_hwnd, 5); return GetForegroundWindow() == _hwnd || SetForegroundWindow(_hwnd) && GetForegroundWindow() == _hwnd; }
    public Task SynchronizeAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _barriers.Enqueue(completion);
        if (!PostMessageW(_hwnd, 0x8001, 0, 0)) completion.TrySetException(new Exception("Diagnostic window barrier failed"));
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private void Run()
    {
        string name = "RugRawDiagnostic" + Guid.NewGuid().ToString("N");
        nint instance = GetModuleHandleW(null);
        bool registered = false;
        try
        {
            var wc = new WindowClass { Proc = Marshal.GetFunctionPointerForDelegate(_proc), Name = name, Instance = instance };
            if (RegisterClassW(ref wc) == 0) throw new Exception("Diagnostic window registration failed");
            registered = true;
            _hwnd = CreateWindowExW(0, name, "Rug Raw Input diagnostics - isolated input target", 0x00cf0000, 300, 200, 440, 300, 0, 0, instance, 0);
            if (_hwnd == 0) throw new Exception("Diagnostic window creation failed");
            _keyHook = SetWindowsHookExW(13, _keyboard, instance, 0); _mouseHook = SetWindowsHookExW(14, _mouse, instance, 0);
            if (_keyHook == 0 || _mouseHook == 0) throw new Exception("Diagnostic interference probes failed");
            ShowWindow(_hwnd, 5); Ready.SetResult(_hwnd);
            int status;
            while ((status = GetMessageW(out var m, 0, 0, 0)) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
            if (status < 0) throw new Exception("Diagnostic message pump failed");
        }
        catch (Exception error) { Ready.TrySetException(error); }
        finally
        {
            if (_keyHook != 0) UnhookWindowsHookEx(_keyHook); if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
            if (_hwnd != 0) DestroyWindow(_hwnd); if (registered) UnregisterClassW(name, instance);
            while (_barriers.TryDequeue(out var b)) b.TrySetCanceled();
        }
    }
    private nint Message(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x200) Interlocked.Increment(ref _move);
        if (message == 0x201) Interlocked.Increment(ref _down);
        if (message == 0x202) Interlocked.Increment(ref _up);
        if (message == 0x100) Interlocked.Increment(ref _keyDown);
        if (message == 0x101) Interlocked.Increment(ref _keyUp);
        if (message == 0x8001) { if (_barriers.TryDequeue(out var b)) b.TrySetResult(); return 0; }
        if (message == 2) PostQuitMessage(0);
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
    public void Dispose()
    {
        if (_hwnd != 0) PostMessageW(_hwnd, 0x10, 0, 0);
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new Exception("Diagnostic window cleanup timed out");
        GC.KeepAlive(_proc); GC.KeepAlive(_keyboard); GC.KeepAlive(_mouse);
    }
    private delegate nint HookProc(int code, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass { public uint Style; public nint Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu, Name; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WindowClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetMessageW(out NativeMessage m, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage m);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref NativeMessage m);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SetWindowsHookExW(int id, HookProc proc, nint instance, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint w, nint l);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
}
