#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

/// <summary>Trusted host owns one source; not exposed to Lua/schema/Gateway adapters.</summary>
public interface IInputEventSource : IAsyncDisposable
{
    InputEventSourceSnapshot Snapshot { get; }
    event EventHandler<InputSourceEvent> InputReceived;
    /// <summary>Off-message-thread notification. Consumers reread Snapshot to avoid stale revisions.</summary>
    event EventHandler StateChanged;
    /// <summary>Completes only after receiver and registrations are ready. Caller cancellation stops startup.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);
    /// <summary>Drains observations captured before the boundary; never call from a source callback.</summary>
    Task<InputEventBoundary> CaptureBoundaryAsync(CancellationToken cancellationToken = default);
    /// <summary>Stops capture, discards queued events and removes all subscriptions; awaits callbacks already entered.</summary>
    Task StopAsync();
}
