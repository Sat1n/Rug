namespace Rug.UI.Core.Models;

/// <summary>
/// A captured window frame copied into managed memory (detached from native buffers).
/// Pixels are BGRA8, row-major, <see cref="Stride"/> bytes per row.
/// </summary>
public sealed record CapturedFrame(byte[] Pixels, int Width, int Height, int Stride);
