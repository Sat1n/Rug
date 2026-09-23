namespace Rug.UI.Core.Models;

/// <summary>Axis-aligned rectangle in image pixel coordinates.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height);

/// <summary>One recognized OCR text block (managed, UTF-8 correct).</summary>
public record OcrTextBlock(string Text, float Score, Rect BoundingBox);

/// <summary>One template-match hit.</summary>
public record TemplateMatchResult(int X, int Y, double Score);

/// <summary>OCR back-end selection (mirrors the native RugOcrEngineType).</summary>
public enum OcrEngineType
{
    WinRt = 0,
    Paddle = 1,
}
