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
    /// <summary>Validate the original lifetime stamp without re-binding or issuing input.</summary>
    void ValidateTarget(InputTarget target, bool requireControl = true) =>
        throw new NotSupportedException("Target validation is not supported by this arbiter.");
    Task<IInputLease> AcquireAsync(InputOwner owner, InputTarget target, InputDeliveryMode mode,
        Action validateAdmission, CancellationToken cancellationToken = default, DateTimeOffset? deadline = null);
    IDisposable RegisterStopHandler(Func<Task> stopInputRuns);
    /// <summary>Host detector guard; true checks return-control readiness only. Invoked outside arbiter locks.</summary>
    IDisposable RegisterAdmissionGuard(Action<bool> guard);
    Task TakeOverAsync();
    void ReturnControl();
}
