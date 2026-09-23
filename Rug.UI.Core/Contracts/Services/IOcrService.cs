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
}
