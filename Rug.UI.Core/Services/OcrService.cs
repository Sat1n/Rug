#nullable enable
using System.Runtime.InteropServices;

using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;

namespace Rug.UI.Core.Services;

/// <summary>
/// Managed OCR facade over the Rug.Core C-ABI. Runs inference on a background
/// (MTA) thread via Task.Run, copies native results into managed records, and
/// releases every native allocation deterministically (SafeHandle + Rug_FreeBuffer).
/// </summary>
public sealed class OcrService : IOcrService
{
    private readonly string? _modelsOcrDir;

    /// <param name="modelsOcrDir">
    /// Directory of OCR model bundles (contains &lt;bundle&gt;/model.json). When null it is
    /// resolved by walking up from the app base directory to find models/ocr.
    /// </param>
    public OcrService(string? modelsOcrDir = null)
    {
        _modelsOcrDir = modelsOcrDir ?? ResolveModelsOcrDir();
    }

    public Task<IReadOnlyList<OcrTextBlock>> RecognizeAsync(
        string imagePath, OcrEngineType engineType, string? modelId = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Recognize(imagePath, engineType, modelId), cancellationToken);

    public Task<IReadOnlyList<OcrTextBlock>> RecognizeFrameAsync(
        CapturedFrame frame, OcrEngineType engineType, string? modelId = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => RecognizeFrame(frame, engineType, modelId), cancellationToken);

    /// <summary>Ids of the discovered Paddle bundles (empty when none / dir unset).</summary>
    public IReadOnlyList<string> ListPaddleModelIds()
    {
        if (string.IsNullOrEmpty(_modelsOcrDir)) return Array.Empty<string>();

        int rc = RugCoreNative.Rug_ScanOcrModels(_modelsOcrDir, out nint listPtr);
        if (rc != RugStatus.Ok || listPtr == 0) return Array.Empty<string>();

        using SafeModelList list = new(listPtr);
        RugCoreNative.Rug_ModelListGetCount(listPtr, out int count);

        var ids = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            if (RugCoreNative.Rug_ModelListGetInfo(listPtr, i, out RugModelInfo info) != RugStatus.Ok)
                continue;
            ids.Add(ModelId(info));
        }
        return ids;
    }

    private IReadOnlyList<OcrTextBlock> Recognize(string imagePath, OcrEngineType engineType, string? modelId)
    {
        int rc = RugCoreNative.Rug_LoadImageFile(imagePath, out RugFrame frame);
        if (rc != RugStatus.Ok)
            throw new RugNativeException(nameof(RugCoreNative.Rug_LoadImageFile), rc);

        try
        {
            return RunOcr(in frame, engineType, modelId);
        }
        finally
        {
            if (frame.Data != 0) RugCoreNative.Rug_FreeBuffer(frame.Data);  // native new[] buffer
        }
    }

    private unsafe IReadOnlyList<OcrTextBlock> RecognizeFrame(CapturedFrame frame, OcrEngineType engineType, string? modelId)
    {
        if (frame.Pixels.Length == 0)
            throw new RugNativeException(nameof(RugCoreNative.Rug_RecognizeText), RugStatus.ErrInvalidParam);

        // Pin the managed BGRA8 buffer and present it as a native RugFrame. The
        // native side only reads it during the call, so no ownership transfers and
        // Rug_FreeBuffer must NOT be called on this pointer.
        fixed (byte* p = frame.Pixels)
        {
            var native = new RugFrame
            {
                Data = (nint)p,
                DataLength = (uint)frame.Pixels.Length,
                Width = frame.Width,
                Height = frame.Height,
                Stride = frame.Stride,
                Format = RugPixelFormat.Bgra8,
            };
            return RunOcr(in native, engineType, modelId);
        }
    }

    private IReadOnlyList<OcrTextBlock> RunOcr(in RugFrame frame, OcrEngineType engineType, string? modelId)
    {
        using OcrEngineHandle engine = CreateEngine(engineType, modelId);

        int rc = RugCoreNative.Rug_RecognizeText(engine.DangerousGetHandle(), in frame, out nint resultPtr);
        if (rc != RugStatus.Ok)
            throw new RugNativeException(nameof(RugCoreNative.Rug_RecognizeText), rc);

        using SafeOcrResult result = new(resultPtr);

        rc = RugCoreNative.Rug_OcrResultGetLineCount(resultPtr, out int count);
        if (rc != RugStatus.Ok)
            throw new RugNativeException(nameof(RugCoreNative.Rug_OcrResultGetLineCount), rc);

        var blocks = new List<OcrTextBlock>(count);
        for (int i = 0; i < count; i++)
        {
            if (RugCoreNative.Rug_OcrResultGetLine(resultPtr, i, out RugOcrLine line) != RugStatus.Ok)
                continue;
            blocks.Add(new OcrTextBlock(
                LineText(line),
                line.Confidence,
                new Rect(line.X, line.Y, line.Width, line.Height)));
        }
        return blocks;
    }

    private OcrEngineHandle CreateEngine(OcrEngineType engineType, string? modelId)
    {
        int rc;
        nint h;
        if (engineType == OcrEngineType.WinRt)
        {
            rc = RugCoreNative.Rug_CreateOcrEngine(RugOcrEngineTypeNative.WinRt, null, out h);
        }
        else
        {
            if (string.IsNullOrEmpty(_modelsOcrDir))
                throw new DirectoryNotFoundException("models/ocr directory is not set; cannot create a Paddle engine.");
            string id = string.IsNullOrEmpty(modelId) ? FirstPaddleModelId() : modelId!;
            rc = RugCoreNative.Rug_CreateOcrEngineById(_modelsOcrDir, id, out h);
        }

        if (rc != RugStatus.Ok || h == 0)
            throw new RugNativeException(nameof(RugCoreNative.Rug_CreateOcrEngine), rc);
        return new OcrEngineHandle(h);
    }

    private string FirstPaddleModelId()
    {
        IReadOnlyList<string> ids = ListPaddleModelIds();
        if (ids.Count == 0)
            throw new RugNativeException(nameof(RugCoreNative.Rug_CreateOcrEngineById), RugStatus.ErrOcrModelNotFound);
        return ids[0];
    }

    private static unsafe string LineText(RugOcrLine line)
    {
        byte* p = line.Text;  // by-value param is already fixed -> direct pointer
        return Marshal.PtrToStringUTF8(new IntPtr(p)) ?? string.Empty;
    }

    private static unsafe string ModelId(RugModelInfo info)
    {
        byte* p = info.Id;
        return Marshal.PtrToStringUTF8(new IntPtr(p)) ?? string.Empty;
    }

    private static string? ResolveModelsOcrDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "models", "ocr");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
