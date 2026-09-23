// =============================================================================
//  Rug.Core.Native.cs — the SINGLE P/Invoke boundary to native Rug.Core.dll.
//  All native imports and their blittable struct mappings live here (AGENTS.md
//  §3.3). Uses LibraryImport with UTF-8 string marshalling so CJK text and
//  paths cross the boundary without mojibake. Fixed-size char buffers are mapped
//  as `fixed byte` (UTF-8) and decoded explicitly by the service layer.
// =============================================================================

#nullable enable
using System.Runtime.InteropServices;

namespace Rug.UI.Core.Native;

/// <summary>Status codes mirrored from RugCoreAbi.h (RugStatus).</summary>
public static class RugStatus
{
    public const int Ok = 0;
    public const int ErrUnknown = -1;
    public const int ErrInvalidParam = -2;
    public const int ErrNotInitialized = -3;
    public const int ErrCaptureFailed = -4;
    public const int ErrOcrFailed = -5;
    public const int ErrInputFailed = -6;
    public const int ErrOutOfMemory = -7;
    public const int ErrUnsupported = -8;
    public const int ErrBufferTooSmall = -9;
    public const int ErrTimeout = -10;
    public const int ErrOcrModelNotFound = -11;
}

/// <summary>Pixel formats mirrored from RugCoreAbi.h (RugPixelFormat).</summary>
public static class RugPixelFormat
{
    public const int Bgra8 = 0;
    public const int Rgba8 = 1;
    public const int Gray8 = 2;
}

/// <summary>OCR back-end selection, mirrors RugOcrEngineType.</summary>
public static class RugOcrEngineTypeNative
{
    public const int WinRt = 0;
    public const int Paddle = 1;
}

/// <summary>Maps C `RugFrame`. `Data` is core-owned when produced by native.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RugFrame
{
    public nint Data;      // uint8_t*
    public uint DataLength;
    public int Width;
    public int Height;
    public int Stride;
    public int Format;     // RugPixelFormat
}

/// <summary>Maps C `RugOcrLine`. Text is a UTF-8 fixed buffer.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct RugOcrLine
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
    public float Confidence;
    public fixed byte Text[256];  // RUG_OCR_LINE_TEXT_MAX, NUL-terminated UTF-8
}

/// <summary>Maps C `RugMatchBox`.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RugMatchBox
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
    public float Confidence;
}

/// <summary>Maps C `RugModelInfo`. All fields are UTF-8 fixed buffers.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct RugModelInfo
{
    public fixed byte Id[64];       // RUG_MODEL_ID_MAX
    public fixed byte Version[32];  // RUG_MODEL_VERSION_MAX
    public fixed byte Engine[32];   // RUG_MODEL_ENGINE_MAX
}

/// <summary>
/// Raw native imports. Do not call these directly from app code — use the
/// service layer (OcrService / TemplateMatchService) and SafeHandle wrappers.
/// </summary>
public static partial class RugCoreNative
{
    private const string Lib = "Rug.Core";

    // --- Memory ownership (native frees its own memory) ---
    [LibraryImport(Lib)]
    public static partial void Rug_FreeBuffer(nint ptr);

    [LibraryImport(Lib)]
    public static partial void Rug_FreeOcrResult(nint result);

    [LibraryImport(Lib)]
    public static partial void Rug_FreeModelList(nint list);

    // --- Image I/O ---
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Rug_LoadImageFile(string path, out RugFrame outFrame);

    // --- OCR ---
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Rug_CreateOcrEngine(int engineType, string? modelPath, out nint outHandle);

    [LibraryImport(Lib)]
    public static partial int Rug_DestroyOcrEngine(nint handle);

    [LibraryImport(Lib)]
    public static partial int Rug_RecognizeText(nint handle, in RugFrame frame, out nint outResult);

    [LibraryImport(Lib)]
    public static partial int Rug_OcrResultGetLineCount(nint result, out int outCount);

    [LibraryImport(Lib)]
    public static partial int Rug_OcrResultGetLine(nint result, int index, out RugOcrLine outLine);

    // --- Model discovery ---
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Rug_ScanOcrModels(string ocrDir, out nint outList);

    [LibraryImport(Lib)]
    public static partial int Rug_ModelListGetCount(nint list, out int outCount);

    [LibraryImport(Lib)]
    public static partial int Rug_ModelListGetInfo(nint list, int index, out RugModelInfo outInfo);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Rug_CreateOcrEngineById(string ocrDir, string id, out nint outHandle);

    // --- Template matching ---
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Rug_MatchTemplate(in RugFrame frame, string templatePath, float threshold,
                                                RugMatchBox[] boxes, ref int inoutCount);
}
