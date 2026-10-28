#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

/// <summary>Trusted host lifetime, not a plugin command or operation parameter.</summary>
public interface IInputSession : IDisposable
{
    InputOwner Owner { get; }
    CancellationToken Token { get; }
}

public interface IInputInterventionCoordinator : IAsyncDisposable
{
    InputInterventionSnapshot Snapshot { get; }
    event EventHandler? StateChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<IInputSession> BeginSessionAsync(InputOwner owner, CancellationToken cancellationToken = default);
    Task PendingTakeover { get; }
}
