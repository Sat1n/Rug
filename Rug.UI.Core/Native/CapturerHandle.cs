using Microsoft.Win32.SafeHandles;

namespace Rug.UI.Core.Native;

/// <summary>
/// Owns a native WGC capturer handle (RugCapturerHandle). Deterministically calls
/// Rug_DestroyCapturer on disposal/finalization so the capture session and its
/// Direct3D11 resources are always released.
/// </summary>
public sealed class CapturerHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public CapturerHandle() : base(ownsHandle: true) { }

    public CapturerHandle(nint handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        RugCoreNative.Rug_DestroyCapturer(handle);
        return true;
    }
}
