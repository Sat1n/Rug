using Rug.UI.Core.Models;

namespace Rug.UI.Core.Contracts.Services;

/// <summary>High-level OpenCV template matching over image files.</summary>
public interface ITemplateMatchService
{
    /// <summary>
    /// Locate every occurrence of <paramref name="templateImagePath"/> inside
    /// <paramref name="targetImagePath"/> scoring at least <paramref name="threshold"/>.
    /// </summary>
    Task<IReadOnlyList<TemplateMatchResult>> MatchAsync(
        string targetImagePath,
        string templateImagePath,
        float threshold = 0.8f,
        CancellationToken cancellationToken = default);
}
