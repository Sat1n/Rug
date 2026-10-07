#nullable enable
using System.Runtime.InteropServices;

namespace Rug.UI.Core.Native;

/// <summary>Test-only native callback; caller retains the existing managed Hook thread and install parameters.</summary>
internal sealed class NativeKeyboardHookDiagnostic : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Snapshot
    {
        public ulong CallbackTotal, ActionableTotal, Injected, Unflagged, TaggedTotal, TaggedInjected, TaggedUnflagged, ParseErrors;
        public ulong OwnTicks, OwnMaxTicks, ChainTicks, ChainMaxTicks, F8Down, F8Up, BDown, BUp;
        public ulong ThreadId, Attached, ThreadMismatch, Frequency;
        internal RawInputDiagnosticWindow.HookCounts Counts => new((long)CallbackTotal, (long)ActionableTotal,
            (long)Injected, (long)Unflagged, (long)TaggedTotal, (long)TaggedInjected, (long)TaggedUnflagged,
            (long)ParseErrors, (long)OwnTicks, (long)OwnMaxTicks, (long)ChainTicks, (long)ChainMaxTicks);
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Pointer();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Change(nint counter);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Read(nint counter, out Snapshot snapshot);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Observe(nint counter, int code, nuint message, nint data);
    private readonly object _gate = new();
    private readonly Change _attach, _detach, _destroy;
    private readonly Read _read;
    private readonly Observe _observe;
    private nint _library, _counter;
    private Snapshot _last;
    internal string LoadedPath { get; }
    internal nint Procedure { get; }
    internal NativeKeyboardHookDiagnostic(string path)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || Marshal.SizeOf<Snapshot>() != 160)
            throw new PlatformNotSupportedException("Native keyboard callback requires the x64 diagnostic layout.");
        _library = NativeLibrary.Load(Path.GetFullPath(path));
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            LoadedPath = process.Modules.Cast<System.Diagnostics.ProcessModule>().Single(m => m.BaseAddress == _library).FileName;
            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
            _attach = Export<Change>("Hook_Attach"); _detach = Export<Change>("Hook_Detach"); _destroy = Export<Change>("Hook_Destroy");
            _read = Export<Read>("Hook_Read"); _observe = Export<Observe>("Hook_ObserveForTest");
            Procedure = Export<Pointer>("Hook_Procedure")();
            _counter = Export<Pointer>("Hook_Create")();
            if (_counter == 0 || Procedure == 0) throw new InvalidOperationException("Native callback allocation failed.");
        }
        catch { if (_counter != 0) _destroy?.Invoke(_counter); NativeLibrary.Free(_library); _library = 0; throw; }
    }
    internal Snapshot Status { get { lock (_gate) { if (_counter != 0) _read(_counter, out _last); return _last; } } }
    internal void Attach() { lock (_gate) { ObjectDisposedException.ThrowIf(_counter == 0, this); Check(_attach(_counter), "attach"); } }
    // Must run on the installation thread, strictly after successful UnhookWindowsHookEx.
    internal void Detach() { lock (_gate) Check(_detach(_counter), "detach"); }
    internal void ObserveForTest(int code, nuint message, nint data) { lock (_gate) _observe(_counter, code, message, data); }
    private static void Check(uint error, string action)
    {
        if (error != 0) throw new System.ComponentModel.Win32Exception((int)error, "Native keyboard callback " + action + " failed");
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_counter == 0) return;
            _read(_counter, out _last);
            // An unsuccessful unhook deliberately retains the callback DLL and counter.
            Check(_destroy(_counter), "destroy (unhook and detach first)");
            _counter = 0; NativeLibrary.Free(_library); _library = 0;
        }
    }
}
