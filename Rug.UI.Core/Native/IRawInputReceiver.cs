#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Native;

// Host-internal test seam; never supplied by an untrusted automation request.
internal interface IRawInputReceiver
{
    void Run(Action ready, Action<InputObservation> receive, CancellationToken cancellationToken);
    void RequestStop();
    Task SynchronizeAsync();
}

internal sealed class RawInputException(InputEventSourceFault fault, string message) : Exception(message)
{
    public InputEventSourceFault Fault { get; } = fault;
}
