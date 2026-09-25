#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

/// <summary>Shared state and asynchronous named-command routing.</summary>
public interface ICommandBus : IDisposable
{
    /// <summary>UI-facing event; the host dispatcher controls its callback thread.</summary>
    event EventHandler<StateChangedEventArgs>? StateChanged;
    T GetState<T>(string key, T defaultValue = default!);
    void SetState<T>(string key, T value);
    IDisposable Subscribe(string commandName, Func<object?, CancellationToken, Task> handler);
    Task ExecuteCommandAsync(string commandName, object? parameter = null,
        CancellationToken cancellationToken = default);
}
