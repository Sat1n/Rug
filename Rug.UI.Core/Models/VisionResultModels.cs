namespace Rug.UI.Core.Models;

/// <summary>Best template hit in physical client pixels (top-left) and its normalized score.</summary>
public sealed record ImageFindResult(int X, int Y, double Similarity);

/// <summary>OCR lines with physical client-space boxes and their joined text.</summary>
public sealed record VisionOcrResult(string Text, IReadOnlyList<OcrTextBlock> Blocks);
