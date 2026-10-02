#nullable enable
using System.Runtime.InteropServices;

namespace Rug.UI.Core.Native;

/// <summary>Opt-in diagnostic target/probes only; not part of the Raw Input event source.</summary>
internal sealed class RawInputDiagnosticWindow : IDisposable
{
    internal sealed record HookCounts(long CallbackTotal, long ActionableTotal, long Injected, long Unflagged,
        long TaggedTotal, long TaggedInjected, long TaggedUnflagged, long ParseErrors,
        long OwnTicks, long OwnMaxTicks, long ChainTicks, long ChainMaxTicks)
    {
        public HookCounts Delta(HookCounts before) => new(CallbackTotal - before.CallbackTotal, ActionableTotal - before.ActionableTotal,
            Injected - before.Injected, Unflagged - before.Unflagged, TaggedTotal - before.TaggedTotal,
            TaggedInjected - before.TaggedInjected, TaggedUnflagged - before.TaggedUnflagged, ParseErrors - before.ParseErrors,
            OwnTicks - before.OwnTicks, OwnMaxTicks, ChainTicks - before.ChainTicks, ChainMaxTicks);
    }
    internal sealed class HookCounter
    {
        private long _callbacks, _actions, _injected, _unflagged, _tagged, _tagInjected, _tagUnflagged, _errors;
        private long _ownTicks, _ownMax, _chainTicks, _chainMax;
        public HookCounts Snapshot => new(Interlocked.Read(ref _callbacks), Interlocked.Read(ref _actions),
            Interlocked.Read(ref _injected), Interlocked.Read(ref _unflagged), Interlocked.Read(ref _tagged),
            Interlocked.Read(ref _tagInjected), Interlocked.Read(ref _tagUnflagged), Interlocked.Read(ref _errors),
            Interlocked.Read(ref _ownTicks), Interlocked.Read(ref _ownMax), Interlocked.Read(ref _chainTicks), Interlocked.Read(ref _chainMax));
        // @shape Only detached aggregate counts survive. Negative codes never dereference data.
        internal void Observe(int code, nint data, bool keyboard)
        {
            Interlocked.Increment(ref _callbacks);
            if (code != 0) return; // Only HC_ACTION has a documented structure.
            Interlocked.Increment(ref _actions);
            try
            {
                if (data == 0) throw new ArgumentException("Missing hook data");
                bool injected = (ReadHookFlags(data, keyboard) & (keyboard ? 0x10u : 1u)) != 0;
                bool tagged = ReadHookExtraInformation(data, keyboard) == Models.InputObservation.RugSoftwareInputTag;
                if (injected) Interlocked.Increment(ref _injected); else Interlocked.Increment(ref _unflagged);
                if (tagged)
                {
                    Interlocked.Increment(ref _tagged);
                    if (injected) Interlocked.Increment(ref _tagInjected); else Interlocked.Increment(ref _tagUnflagged);
                }
            }
            catch (Exception) { Interlocked.Increment(ref _errors); } // No exception crosses the callback boundary.
        }
        internal void Timing(long ticks, bool chain)
        {
            if (chain) { Interlocked.Add(ref _chainTicks, ticks); Max(ref _chainMax, ticks); }
            else { Interlocked.Add(ref _ownTicks, ticks); Max(ref _ownMax, ticks); }
        }
        private static void Max(ref long target, long value)
        {
            long previous = Interlocked.Read(ref target);
            while (previous < value) { long seen = Interlocked.CompareExchange(ref target, value, previous); if (seen == previous) return; previous = seen; }
        }
    }
    internal sealed record Counters(long Move, long MouseDown, long MouseUp, long KeyDown, long KeyUp, HookCounts Keyboard, HookCounts Mouse)
    {
        public long InjectedKeyboard => Keyboard.Injected;
        public long InjectedMouse => Mouse.Injected;
        public long Unflagged => Keyboard.Unflagged + Mouse.Unflagged;
        public long TaggedKeyboard => Keyboard.TaggedInjected;
        public long TaggedMouse => Mouse.TaggedInjected;
    }
    internal sealed record ProbeStatus(long Generation, uint InstallThreadId, string Architecture,
        bool KeyboardInstalled, int KeyboardInstallError, bool MouseInstalled, int MouseInstallError,
        bool ThreadExited, string? Failure, string? ExitReason);
    private static long _nextGeneration;
    private readonly long _generation = Interlocked.Increment(ref _nextGeneration);
    private uint _installThreadId;
    private int _keyboardInstalled, _mouseInstalled, _keyboardInstallError, _mouseInstallError, _exited;
    private string? _failure, _exitReason;
    public ProbeStatus Status => new(_generation, _installThreadId, RuntimeInformation.ProcessArchitecture.ToString(),
        Volatile.Read(ref _keyboardInstalled) != 0, Volatile.Read(ref _keyboardInstallError), Volatile.Read(ref _mouseInstalled) != 0,
        Volatile.Read(ref _mouseInstallError), Volatile.Read(ref _exited) != 0, Volatile.Read(ref _failure), Volatile.Read(ref _exitReason));
    public readonly TaskCompletionSource<ProbeStatus> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HookCounter _keyCounts = new(), _mouseCounts = new();
    public readonly TaskCompletionSource<nint> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly WindowsRawInputReceiver.WindowProc _proc;
    private readonly HookProc _keyboard, _mouse;
    private readonly System.Collections.Concurrent.ConcurrentQueue<TaskCompletionSource> _barriers = new();
    private nint _hwnd, _keyHook, _mouseHook;
    private long _move, _down, _up, _keyDown, _keyUp;
    public Counters Counts => new(Interlocked.Read(ref _move), Interlocked.Read(ref _down), Interlocked.Read(ref _up),
        Interlocked.Read(ref _keyDown), Interlocked.Read(ref _keyUp), _keyCounts.Snapshot, _mouseCounts.Snapshot);
    public RawInputDiagnosticWindow()
    {
        _proc = Message;
        _keyboard = (code, w, l) => Hook(code, w, l, true);
        _mouse = (code, w, l) => Hook(code, w, l, false);
        _thread = new Thread(Run) { IsBackground = true, Name = "Rug raw diagnostics isolated window" }; _thread.Start();
    }
    private nint Hook(int code, nuint w, nint l, bool keyboard)
    {
        var counter = keyboard ? _keyCounts : _mouseCounts;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        counter.Observe(code, l, keyboard);
        long forwarding = System.Diagnostics.Stopwatch.GetTimestamp();
        counter.Timing(forwarding - started, false);
        try { return CallNextHookEx(0, code, w, l); } // Negative codes also forward unchanged.
        finally { counter.Timing(System.Diagnostics.Stopwatch.GetTimestamp() - forwarding, true); }
    }
    internal static uint ReadHookFlags(nint data, bool keyboard) => unchecked((uint)Marshal.ReadInt32(data, keyboard ? 8 : 12));
    internal static nuint ReadHookExtraInformation(nint data, bool keyboard) => unchecked((nuint)Marshal.ReadIntPtr(data, keyboard ? 16 : IntPtr.Size == 8 ? 24 : 20));
    public bool Activate() { ShowWindow(_hwnd, 5); return GetForegroundWindow() == _hwnd || SetForegroundWindow(_hwnd) && GetForegroundWindow() == _hwnd; }
    public async Task SynchronizeAsync()
    {
        ThrowIfThreadUnavailable();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _barriers.Enqueue(completion);
        if (!PostMessageW(_hwnd, 0x8001, 0, 0)) completion.TrySetException(new Exception("Diagnostic window barrier failed"));
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
        ThrowIfThreadUnavailable(); // Pump health is not proof of hook health.
    }
    private void ThrowIfThreadUnavailable()
    {
        if (Status is { Failure: not null } failed) throw new InvalidOperationException(failed.Failure);
        if (Status.ThreadExited) throw new InvalidOperationException("Diagnostic message thread exited");
    }
    internal void InjectThreadFailureForTest() => PostMessageW(_hwnd, 0x8002, 0, 0);
    internal static nint CheckHookInstallation(nint handle, int error, string name)
        => handle != 0 ? handle : throw new System.ComponentModel.Win32Exception(error, name + " installation failed; win32Error=" + error);
    private void Run()
    {
        _installThreadId = GetCurrentThreadId();
        string name = "RugRawDiagnostic" + Guid.NewGuid().ToString("N");
        nint instance = GetModuleHandleW(null);
        bool registered = false;
        try
        {
            var wc = new WindowClass { Proc = Marshal.GetFunctionPointerForDelegate(_proc), Name = name, Instance = instance };
            if (RegisterClassW(ref wc) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Diagnostic window registration failed");
            registered = true;
            _hwnd = CreateWindowExW(0, name, "Rug Raw Input diagnostics - isolated input target", 0x00cf0000, 300, 200, 440, 300, 0, 0, instance, 0);
            if (_hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Diagnostic window creation failed");
            _keyHook = SetWindowsHookExW(13, _keyboard, instance, 0);
            _keyboardInstallError = _keyHook == 0 ? Marshal.GetLastPInvokeError() : 0;
            CheckHookInstallation(_keyHook, _keyboardInstallError, "Keyboard hook");
            Volatile.Write(ref _keyboardInstalled, 1);
            _mouseHook = SetWindowsHookExW(14, _mouse, instance, 0);
            _mouseInstallError = _mouseHook == 0 ? Marshal.GetLastPInvokeError() : 0;
            CheckHookInstallation(_mouseHook, _mouseInstallError, "Mouse hook");
            Volatile.Write(ref _mouseInstalled, 1);
            ShowWindow(_hwnd, 5); Ready.SetResult(_hwnd);
            int status;
            while ((status = GetMessageW(out var m, 0, 0, 0)) > 0) { if (m.Id == 0x8002) throw new InvalidOperationException("Controlled diagnostic thread failure"); TranslateMessage(ref m); DispatchMessageW(ref m); }
            if (status < 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Diagnostic message pump failed");
            _exitReason = "WM_QUIT";
        }
        catch (Exception error) { _failure = error.GetType().Name + ": " + error.Message; _exitReason = "Fault"; Ready.TrySetException(error); }
        finally
        {
            RemoveHook(_keyHook, "Keyboard hook"); RemoveHook(_mouseHook, "Mouse hook");
            if (_hwnd != 0) DestroyWindow(_hwnd); if (registered) UnregisterClassW(name, instance);
            while (_barriers.TryDequeue(out var b)) b.TrySetException(new InvalidOperationException(_failure ?? "Diagnostic thread exited"));
            Volatile.Write(ref _exited, 1); Completion.TrySetResult(Status);
        }
    }
    private void RemoveHook(nint handle, string name)
    {
        if (handle == 0 || UnhookWindowsHookEx(handle)) return;
        int error = Marshal.GetLastPInvokeError();
        _failure = (_failure is null ? "" : _failure + "; ") + name + " removal failed; win32Error=" + error;
        _exitReason = "CleanupFault";
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
    private int _disposeRequested;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        if (_hwnd != 0) PostMessageW(_hwnd, 0x10, 0, 0);
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new Exception("Diagnostic window cleanup timed out");
        GC.KeepAlive(_proc); GC.KeepAlive(_keyboard); GC.KeepAlive(_mouse);
    }
    private delegate nint HookProc(int code, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass { public uint Style; public nint Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu, Name; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WindowClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint ex, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessageW(out NativeMessage m, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage m);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref NativeMessage m);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetWindowsHookExW(int id, HookProc proc, nint instance, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint w, nint l);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(nint hook);
}
