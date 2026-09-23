using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;

namespace Rug.UI.Core.Services;

/// <summary>
/// Managed template-matching facade over Rug_MatchTemplate. Loads the target via
/// Rug_LoadImageFile, passes the template path (Rug.Core decodes it with OpenCV),
/// and copies hits into managed records. Runs on a background thread.
/// </summary>
public sealed class TemplateMatchService : ITemplateMatchService
{
    private const int Capacity = 64;

    public Task<IReadOnlyList<TemplateMatchResult>> MatchAsync(
        string targetImagePath, string templateImagePath, float threshold = 0.8f,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Match(targetImagePath, templateImagePath, threshold), cancellationToken);

    private static IReadOnlyList<TemplateMatchResult> Match(string targetImagePath, string templateImagePath, float threshold)
    {
        int rc = RugCoreNative.Rug_LoadImageFile(targetImagePath, out RugFrame frame);
        if (rc != RugStatus.Ok)
            throw new RugNativeException(nameof(RugCoreNative.Rug_LoadImageFile), rc);

        try
        {
            var boxes = new RugMatchBox[Capacity];
            int count = Capacity;

            rc = RugCoreNative.Rug_MatchTemplate(in frame, templateImagePath, threshold, boxes, ref count);
            if (rc != RugStatus.Ok && rc != RugStatus.ErrBufferTooSmall)
                throw new RugNativeException(nameof(RugCoreNative.Rug_MatchTemplate), rc);

            if (count < 0) count = 0;
            var results = new List<TemplateMatchResult>(count);
            for (int i = 0; i < count; i++)
                results.Add(new TemplateMatchResult(boxes[i].X, boxes[i].Y, boxes[i].Confidence));
            return results;
        }
        finally
        {
            if (frame.Data != 0) RugCoreNative.Rug_FreeBuffer(frame.Data);
        }
    }
}
