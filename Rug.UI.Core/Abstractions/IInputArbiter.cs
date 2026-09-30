#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

public interface IInputWindowValidator
{
    InputTarget Bind(nint hwnd);
    bool IsValid(InputTarget target);
}

public interface IInputLease : IDisposable
{
    InputOwner Owner { get; }
    InputTarget Target { get; }
    CancellationToken Token { get; }
    void Validate();
    void CleanupFailed(Exception error);
}

/// <summary>Trusted host service; leases and owners are not request payloads.</summary>
public interface IInputArbiter
{
    InputControlSnapshot Snapshot { get; }
    event EventHandler? StateChanged;
    CancellationToken AutomationToken { get; }
    InputTarget BindTarget(nint hwnd);
    Task<IInputLease> AcquireAsync(InputOwner owner, InputTarget target, InputDeliveryMode mode,
        Action validateAdmission, CancellationToken cancellationToken = default, DateTimeOffset? deadline = null);
    IDisposable RegisterStopHandler(Func<Task> stopInputRuns);
    Task TakeOverAsync();
    void ReturnControl();
}
