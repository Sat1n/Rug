#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Contracts.Services;

/// <summary>High-level OCR over an image file, on a background (MTA) thread.</summary>
public interface IOcrService
{
    /// <summary>
    /// Recognize text in <paramref name="imagePath"/>.
    /// </summary>
    /// <param name="engineType">WinRT (system) or Paddle (models/ocr bundle).</param>
    /// <param name="modelId">
    /// For Paddle: the discovered model id (e.g. "ppocr_v6_tiny"). Null selects the
    /// first discovered bundle. Ignored for WinRT.
    /// </param>
    Task<IReadOnlyList<OcrTextBlock>> RecognizeAsync(
        string imagePath,
        OcrEngineType engineType,
        string? modelId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recognize text in an already-captured in-memory frame (BGRA8). Used by the
    /// live preview to overlay OCR boxes on WGC frames without touching disk.
    /// </summary>
    Task<IReadOnlyList<OcrTextBlock>> RecognizeFrameAsync(
        CapturedFrame frame,
        OcrEngineType engineType,
        string? modelId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Ids of the discovered Paddle model bundles (empty when none).</summary>
    IReadOnlyList<string> ListPaddleModelIds();
}
