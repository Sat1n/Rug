#nullable enable
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Rug.UI.Core.Native;

/// <summary>Flow-local opt-in for observed physical-client input only. Each actual Native call
/// sets/restores its own thread context; no process/global or legacy input defaults change.</summary>
internal static class ObservedInputCoordinates
{
    private static readonly AsyncLocal<bool> Requested=new();
    internal static IDisposable Request()=>new RequestScope();
    internal static IDisposable ForNativeCall()=>new NativeScope(Requested.Value);
    private sealed class RequestScope : IDisposable
    {
        private readonly bool _previous=Requested.Value;
        internal RequestScope()=>Requested.Value=true;
        public void Dispose()=>Requested.Value=_previous;
    }
    private sealed class NativeScope : IDisposable
    {
        private readonly nint _previous;
        internal NativeScope(bool enabled)
        {
            if(!enabled)return;
            _previous=SetThreadDpiAwarenessContext(-4);
            if(_previous==0)throw new Win32Exception(Marshal.GetLastPInvokeError(),"Physical input DPI context unavailable.");
        }
        public void Dispose()
        {
            if(_previous!=0&&SetThreadDpiAwarenessContext(_previous)==0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(),"Physical input DPI context restoration failed.");
        }
    }
    [DllImport("user32.dll",SetLastError=true)] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
