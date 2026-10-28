#nullable enable
using System.Runtime.InteropServices;

namespace Rug.UI.Core.Native;

/// <summary>Internal test-only DLL adapter. Never used by production event sources or Rug.Core ABI.</summary>
internal sealed class NativeRawInputDiagnosticReceiver : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Snapshot
    {
        public ulong ThreadId, Hwnd, Ready, Exited, Timer, Input, Device, Cleanup, Barrier, Fault, Error, CleanupError;
        internal string? Failure => Fault == 0 && CleanupError == 0 ? null
            : $"nativeReceiver {(Fault == 2 ? "registration ownership changed" : Fault == 1 ? "registration failed/conflict" : "fault")} code={Fault} win32Error={Error} cleanupError={CleanupError}";
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Create();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Action(nint receiver);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Read(nint receiver, out Snapshot snapshot);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Barrier(nint receiver);
    private readonly object _gate = new();
    private nint _library, _receiver;
    private readonly Action _run, _stop, _destroy;
    private readonly Read _read;
    private readonly Barrier _barrier;
    private Snapshot _last;
    internal string LoadedPath { get; }
    internal NativeRawInputDiagnosticReceiver(string path)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("Native diagnostic receiver requires x64.");
        if (Marshal.SizeOf<Snapshot>() != 96) throw new InvalidOperationException("Native diagnostic snapshot layout mismatch.");
        _library = NativeLibrary.Load(Path.GetFullPath(path));
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            LoadedPath = process.Modules.Cast<System.Diagnostics.ProcessModule>().Single(m => m.BaseAddress == _library).FileName;
            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
            _run = Export<Action>("Probe_Run"); _stop = Export<Action>("Probe_Stop"); _destroy = Export<Action>("Probe_Destroy");
            _read = Export<Read>("Probe_Read"); _barrier = Export<Barrier>("Probe_Barrier");
            _receiver = Export<Create>("Probe_Create")();
            if (_receiver == 0) throw new InvalidOperationException("Native diagnostic receiver allocation failed.");
        }
        catch { NativeLibrary.Free(_library); _library = 0; throw; }
    }
    internal Snapshot Status
    {
        get { lock (_gate) { if (_receiver != 0) _read(_receiver, out _last); return _last; } }
    }
    // Caller owns the dedicated thread and must join it before Dispose; no native-to-managed callbacks.
    internal void Run() => _run(_receiver);
    internal void RequestStop() { lock (_gate) { if (_receiver != 0) _stop(_receiver); } }
    internal ulong SubmitBarrier() { lock (_gate) return _receiver == 0 ? 0 : _barrier(_receiver); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_receiver == 0) return;
            _read(_receiver, out _last);
            // Never unload native window procedures still in use, including cleanup after a fault.
            if (_last.ThreadId != 0 && _last.Exited == 0) throw new InvalidOperationException("Join native receiver before releasing its DLL.");
            _destroy(_receiver); _receiver = 0; NativeLibrary.Free(_library); _library = 0;
        }
    }
}
