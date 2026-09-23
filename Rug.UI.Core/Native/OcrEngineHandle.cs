using Microsoft.Win32.SafeHandles;

namespace Rug.UI.Core.Native;

/// <summary>
/// Owns a native OCR engine handle (RugOcrEngineHandle). Deterministically calls
/// Rug_DestroyOcrEngine on disposal/finalization so the native engine and its
/// ONNX sessions are always released.
/// </summary>
public sealed class OcrEngineHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public OcrEngineHandle() : base(ownsHandle: true) { }

    public OcrEngineHandle(nint handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        RugCoreNative.Rug_DestroyOcrEngine(handle);
        return true;
    }
}
