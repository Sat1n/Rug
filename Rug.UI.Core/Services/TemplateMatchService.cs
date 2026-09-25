using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;

namespace Rug.UI.Core.Services;

/// <summary>
/// Managed template-matching facade over Rug_MatchTemplate. Accepts a loaded
/// target path or a pinned managed BGRA frame, passes the template path for
/// OpenCV decoding, and copies hits into managed records on a background thread.
/// </summary>
public sealed class TemplateMatchService : ITemplateMatchService
{
    private const int Capacity = 64;

    public Task<IReadOnlyList<TemplateMatchResult>> MatchAsync(
        string targetImagePath, string templateImagePath, float threshold = 0.8f,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Match(targetImagePath, templateImagePath, threshold), cancellationToken);

    public Task<IReadOnlyList<TemplateMatchResult>> MatchFrameAsync(
        CapturedFrame frame, string templateImagePath, float threshold = 0.8f,
        CancellationToken cancellationToken = default)
        => Task.Run(() => MatchFrame(frame, templateImagePath, threshold), cancellationToken);

    private static IReadOnlyList<TemplateMatchResult> Match(string targetImagePath, string templateImagePath, float threshold)
    {
        int rc = RugCoreNative.Rug_LoadImageFile(targetImagePath, out RugFrame frame);
        if (rc != RugStatus.Ok)
            throw new RugNativeException(nameof(RugCoreNative.Rug_LoadImageFile), rc);

        try
        {
            return RunMatch(in frame, templateImagePath, threshold);
        }
        finally
        {
            if (frame.Data != 0) RugCoreNative.Rug_FreeBuffer(frame.Data);
        }
    }

    private static unsafe IReadOnlyList<TemplateMatchResult> MatchFrame(
        CapturedFrame frame, string templateImagePath, float threshold)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride < checked(frame.Width * 4) ||
            frame.Pixels.Length < checked(frame.Stride * frame.Height))
            throw new ArgumentException("Invalid BGRA frame dimensions or buffer length.", nameof(frame));

        // Native only reads this buffer during Rug_MatchTemplate; no ownership transfer.
        fixed (byte* pixels = frame.Pixels)
        {
            var native = new RugFrame
            {
                Data = (nint)pixels,
                DataLength = checked((uint)frame.Pixels.Length),
                Width = frame.Width,
                Height = frame.Height,
                Stride = frame.Stride,
                Format = RugPixelFormat.Bgra8
            };
            return RunMatch(in native, templateImagePath, threshold);
        }
    }

    private static IReadOnlyList<TemplateMatchResult> RunMatch(
        in RugFrame frame, string templateImagePath, float threshold)
    {
        var boxes = new RugMatchBox[Capacity];
        int count = Capacity;
        int rc = RugCoreNative.Rug_MatchTemplate(in frame, templateImagePath, threshold, boxes, ref count);
        if (rc != RugStatus.Ok && rc != RugStatus.ErrBufferTooSmall)
            throw new RugNativeException(nameof(RugCoreNative.Rug_MatchTemplate), rc);

        count = Math.Clamp(count, 0, Capacity);
        var results = new List<TemplateMatchResult>(count);
        for (int i = 0; i < count; i++)
            results.Add(new TemplateMatchResult(boxes[i].X, boxes[i].Y, boxes[i].Confidence));
        return results;
    }
}
