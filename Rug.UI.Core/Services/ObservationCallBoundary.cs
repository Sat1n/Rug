#nullable enable
namespace Rug.UI.Core.Services;

/// <summary>Trusted flow-local original cancellation/deadline, independent of linked-token callback propagation.</summary>
internal sealed record ObservationCallBoundary(CancellationToken Original, DateTimeOffset Deadline, TimeProvider Clock)
{
    internal static readonly AsyncLocal<ObservationCallBoundary?> Current = new();
    internal void Check()
    {
        Original.ThrowIfCancellationRequested();
        if (Clock.GetUtcNow() >= Deadline) throw new ObservationDeadlineException();
    }
}
internal sealed class ObservationDeadlineException() : TimeoutException("Observation deadline expired before publication/result.");
