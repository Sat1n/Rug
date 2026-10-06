#nullable enable
using System.ComponentModel;
using System.Runtime.InteropServices;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Native;

/// <summary>Explicitly started by host, one receiver per process keyboard/mouse class.</summary>
internal sealed class WindowsRawInputReceiver : IRawInputReceiver
{
    private static readonly object RegistrationGate = new();
    private const uint StopMessage = 0x8001, BarrierMessage = 0x8002;
    private readonly object _barrierGate = new();
    private readonly Queue<TaskCompletionSource> _barriers = new();
    private readonly RawInputKeyboardTracker _keys = new();
    private readonly RawInputDeviceIds _devices = new();
    private readonly WindowProc _proc;
    private nint _hwnd;
    private Exception? _failure;
    private Action<InputObservation>? _receive;
    private bool _stopRequested;
    private bool _registrationEnabled = true;
    private bool _readPackets = true;
    private bool _registerKeyboard = true;
    private bool _registerMouse = true;
    private uint _threadId;
    private long _timerMessages, _inputMessages, _deviceMessages, _inputCleanupCalls;

    internal sealed record PumpSnapshot(uint ThreadId, nint Hwnd, bool RegistrationEnabled,
        long TimerMessages, long InputMessages, long DeviceMessages, bool PacketReadingEnabled, long InputCleanupCalls,
        bool KeyboardRegistrationEnabled, bool MouseRegistrationEnabled);
    internal PumpSnapshot DiagnosticSnapshot
    {
        get { lock (_barrierGate) return new(_threadId, _hwnd, _registrationEnabled,
            Interlocked.Read(ref _timerMessages), Interlocked.Read(ref _inputMessages), Interlocked.Read(ref _deviceMessages),
            _readPackets, Interlocked.Read(ref _inputCleanupCalls), _registrationEnabled && _registerKeyboard, _registrationEnabled && _registerMouse); }
    }

    internal WindowsRawInputReceiver() => _proc = HandleMessage;
    public void RequestStop()
    {
        lock (_barrierGate) { _stopRequested = true; if (_hwnd != 0) PostMessageW(_hwnd, StopMessage, 0, 0); }
    }
    public Task SynchronizeAsync()
    {
        lock (_barrierGate)
        {
            if (_hwnd == 0 || _stopRequested) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _barriers.Enqueue(completion);
            if (!PostMessageW(_hwnd, BarrierMessage, 0, 0)) completion.TrySetException(Win32("PostMessage"));
            return completion.Task;
        }
    }
    /// <summary>@shape callbacks: ready once after owned registrations, receive copied scalars; @source messages: Win32 WM_INPUT/WM_INPUT_DEVICE_CHANGE</summary>
    public void Run(Action ready, Action<InputObservation> receive, CancellationToken cancellationToken)
        => RunCore(ready, receive, cancellationToken, registerDevices: true);

    // Internal diagnostic seam only; this callback certifies a pump, never IInputEventSource readiness.
    internal void RunUnregisteredDiagnostic(Action pumpReady, Action<InputObservation> receive, CancellationToken cancellationToken)
        => RunCore(pumpReady, receive, cancellationToken, registerDevices: false);

    // Registered message-count control only; not capture readiness and no raw classification evidence.
    internal void RunMessageCountDiagnostic(Action pumpReady, CancellationToken cancellationToken, bool registerKeyboard = true, bool registerMouse = true)
    {
        if (!registerKeyboard && !registerMouse) throw new ArgumentException("Registered message control requires a selected device class.");
        RunCore(pumpReady, null, cancellationToken, registerDevices: true, readPackets: false, registerKeyboard: registerKeyboard, registerMouse: registerMouse);
    }

    /// <summary>@shape production packet receiver or internal unregistered/count-only pump; @source Win32 RegisterRawInputDevices/WM_INPUT</summary>
    private void RunCore(Action ready, Action<InputObservation>? receive, CancellationToken cancellationToken, bool registerDevices, bool readPackets = true, bool registerKeyboard = true, bool registerMouse = true)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Raw Input requires Windows.");
        string name = "RugRawInput" + Guid.NewGuid().ToString("N");
        nint instance = GetModuleHandleW(null);
        bool registeredClass = false;
        lock (_barrierGate) { _threadId = GetCurrentThreadId(); _registrationEnabled = registerDevices; _readPackets = readPackets; _registerKeyboard = registerKeyboard; _registerMouse = registerMouse; }
        _receive = receive;
        try
        {
            var wc = new WindowClass { Proc = Marshal.GetFunctionPointerForDelegate(_proc), ClassName = name, Instance = instance };
            if (RegisterClassW(ref wc) == 0) throw Win32("RegisterClass");
            registeredClass = true;
            nint hwnd = CreateWindowExW(0, name, name, 0, 0, 0, 0, 0, -3, 0, instance, 0);
            if (hwnd == 0) throw Win32("CreateWindow");
            lock (_barrierGate) _hwnd = hwnd;
            using var stop = cancellationToken.Register(RequestStop);
            cancellationToken.ThrowIfCancellationRequested();
            if (registerDevices) lock (RegistrationGate)
            {
                if (Registrations().Any(IsRelevant)) throw new RawInputException(InputEventSourceFault.Registration, "Raw keyboard/mouse registration already belongs to another component.");
                Device mouse = new() { Page = 1, Usage = 2, Flags = 0x2100, Target = hwnd };
                Device keyboard = new() { Page = 1, Usage = 6, Flags = 0x2100, Target = hwnd };
                Device[] devices = registerMouse ? (registerKeyboard ? [mouse, keyboard] : [mouse]) : [keyboard];
                if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<Device>())) throw Win32("RegisterRawInputDevices", InputEventSourceFault.Registration);
                VerifyOwnership(hwnd);
            }
            if (SetTimer(hwnd, 1, 250, 0) == 0) throw Win32("SetTimer");
            cancellationToken.ThrowIfCancellationRequested();
            lock (_barrierGate) { if (_stopRequested) return; }
            ready();
            int result;
            while ((result = GetMessageW(out var message, 0, 0, 0)) > 0)
            { TranslateMessage(ref message); DispatchMessageW(ref message); }
            if (result < 0) throw Win32("GetMessage");
            if (_failure is not null) throw _failure;
        }
        finally
        {
            Exception? cleanup = null;
            nint hwnd;
            lock (_barrierGate) { hwnd = _hwnd; _stopRequested = true; }
            try
            {
                if (hwnd != 0)
                {
                    if (registerDevices) lock (RegistrationGate)
                    {
                        // Never REMOVE a registration now owned by a replacement receiver.
                        var owned = Registrations().Where(d => IsRelevant(d) && d.Target == hwnd
                            && ((registerKeyboard && registerMouse) || (registerKeyboard && d.Usage == 6) || (registerMouse && d.Usage == 2)))
                            .Select(d => new Device { Page = d.Page, Usage = d.Usage, Flags = 1 }).ToArray();
                        if (owned.Length != 0 && !RegisterRawInputDevices(owned, (uint)owned.Length, (uint)Marshal.SizeOf<Device>())) throw Win32("Remove raw registration");
                    }
                }
            }
            catch (Exception error) { cleanup = error; }
            if (hwnd != 0) { KillTimer(hwnd, 1); if (!DestroyWindow(hwnd)) cleanup ??= Win32("DestroyWindow"); }
            if (registeredClass && !UnregisterClassW(name, instance)) cleanup ??= Win32("UnregisterClass");
            lock (_barrierGate) { _hwnd = 0; while (_barriers.TryDequeue(out var barrier)) barrier.TrySetCanceled(); }
            _keys.Clear(); _devices.Clear(); _receive = null; GC.KeepAlive(_proc);
            if (cleanup is not null) throw new RawInputException(InputEventSourceFault.CleanupFailure, cleanup.Message);
        }
    }
    private static bool IsRelevant(Device d) => d.Page == 1 && d.Usage is 0 or 2 or 6;
    private void VerifyOwnership(nint hwnd)
    {
        var devices = Registrations();
        ushort[] usages = _registerMouse ? (_registerKeyboard ? [2, 6] : [2]) : [6];
        if (!usages.All(usage => devices.Any(d => d.Page == 1 && d.Usage == usage && d.Target == hwnd && (d.Flags & ~0x2000u) == 0x100))
            || devices.Any(d => d.Page == 1 && (d.Usage == 0 || (!_registerKeyboard && d.Usage == 6) || (!_registerMouse && d.Usage == 2))))
            throw new RawInputException(InputEventSourceFault.OwnershipLost, "Raw keyboard/mouse registration ownership changed: " + string.Join(", ", devices.Where(IsRelevant).Select(d => $"usage={d.Usage}, flags=0x{d.Flags:x}, ownTarget={d.Target == hwnd}")));
    }
    private nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == StopMessage) { PostQuitMessage(0); return 0; }
            if (message == BarrierMessage)
            { lock (_barrierGate) { if (_barriers.TryDequeue(out var barrier)) barrier.TrySetResult(); } return 0; }
            if (message is 0x113 or 0xff or 0xfe)
            {
                if (message == 0x113) Interlocked.Increment(ref _timerMessages);
                if (message == 0xff) Interlocked.Increment(ref _inputMessages);
                if (message == 0xfe) Interlocked.Increment(ref _deviceMessages);
                if (_registrationEnabled) lock (RegistrationGate) VerifyOwnership(hwnd);
                if (message == 0xff && _readPackets) Read(lParam, (wParam & 0xff) == 0);
                if (message == 0xfe && _readPackets)
                {
                    ulong device = unchecked((ulong)(nuint)lParam);
                    if (wParam is not (1 or 2)) throw new RawInputException(InputEventSourceFault.InvalidData, "Invalid device notification.");
                    _receive!(_keys.Apply(new(wParam == 1 ? InputEventKind.DeviceArrived : InputEventKind.DeviceRemoved, _devices.Get(device), false)));
                    if (wParam == 2) _devices.Remove(device);
                }
            }
        }
        catch (Exception error) { _failure ??= error; PostQuitMessage(0); }
        // RIM_INPUT requires DefWindowProc cleanup, even on malformed data or faults.
        if (message == 0xff) { DefWindowProcW(hwnd, message, wParam, lParam); Interlocked.Increment(ref _inputCleanupCalls); return 0; }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
    private void Read(nint handle, bool foreground)
    {
        uint size = 0, header = (uint)(8 + IntPtr.Size * 2);
        if (GetRawInputData(handle, 0x10000003, 0, ref size, header) != 0 || size < header || size > 4096)
            throw Win32("GetRawInputData size", InputEventSourceFault.InvalidData);
        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint expected = size;
            if (GetRawInputData(handle, 0x10000003, buffer, ref size, header) != expected || size != expected)
                throw Win32("GetRawInputData copy", InputEventSourceFault.InvalidData);
            var copy = new byte[size]; Marshal.Copy(buffer, copy, 0, copy.Length);
            var observation = RawInputPacketParser.Parse(copy, foreground, IntPtr.Size);
            if (observation is null) return;
            observation = _keys.Apply(observation with { DeviceId = _devices.Get(observation.DeviceId) });
            _receive!(observation);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static Device[] Registrations()
    {
        uint count = 0, size = (uint)Marshal.SizeOf<Device>();
        if (GetRegisteredRawInputDevices(null, ref count, size) == uint.MaxValue) throw Win32("GetRegisteredRawInputDevices size");
        if (count > 1024) throw new RawInputException(InputEventSourceFault.Registration, "Unexpected raw registration count.");
        if (count == 0) return [];
        var devices = new Device[count];
        uint result = GetRegisteredRawInputDevices(devices, ref count, size);
        if (result == uint.MaxValue || result > devices.Length) throw Win32("GetRegisteredRawInputDevices copy");
        return devices.Take((int)result).ToArray();
    }
    private static RawInputException Win32(string api, InputEventSourceFault fault = InputEventSourceFault.NativeFailure) =>
        new(fault, $"{api}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    internal delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] internal struct Device { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass { public uint Style; public nint Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName, ClassName; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterRawInputDevices([In] Device[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRegisteredRawInputDevices([Out] Device[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(nint handle, uint command, nint data, ref uint size, uint header);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? module);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessageW(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd, nuint id);
}
