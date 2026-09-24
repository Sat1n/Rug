#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

/// <summary>Persists a task's capture frame and incident metadata as a black-box record.</summary>
public interface IAnomalyLogger
{
    /// <returns>The path to the JSON record.</returns>
    Task<string> LogAnomalyAsync(TaskExecutionContext context, string reason, string detail);
}
