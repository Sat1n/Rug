using Microsoft.Win32.SafeHandles;

namespace Rug.UI.Core.Native;

/// <summary>
/// Owns a native OCR result (RugOcrResultHandle). The service copies the lines
/// into managed objects, then disposing this releases the native memory via
/// Rug_FreeOcrResult — never Rug_FreeBuffer (which is for raw new[] buffers).
/// </summary>
public sealed class SafeOcrResult : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeOcrResult() : base(ownsHandle: true) { }

    public SafeOcrResult(nint handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        RugCoreNative.Rug_FreeOcrResult(handle);
        return true;
    }
}
