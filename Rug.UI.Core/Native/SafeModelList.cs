using Microsoft.Win32.SafeHandles;

namespace Rug.UI.Core.Native;

/// <summary>Owns a native model list (RugModelListHandle); frees via Rug_FreeModelList.</summary>
public sealed class SafeModelList : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeModelList() : base(ownsHandle: true) { }

    public SafeModelList(nint handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        RugCoreNative.Rug_FreeModelList(handle);
        return true;
    }
}
