using Microsoft.Win32.SafeHandles;

namespace Rug.UI.Core.Native;

/// <summary>
/// Owns a native input controller handle (RugInputControllerHandle). Deterministically
/// calls Rug_DestroyInputController on disposal/finalization so the native controller
/// (and any bound resources) is always released.
/// </summary>
public sealed class InputControllerHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public InputControllerHandle() : base(ownsHandle: true) { }

    public InputControllerHandle(nint handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        RugCoreNative.Rug_DestroyInputController(handle);
        return true;
    }
}
