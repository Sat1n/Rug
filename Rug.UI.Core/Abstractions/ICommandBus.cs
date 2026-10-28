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
    T GetState<T>(StateScope scope, string key, T defaultValue = default!);
    void SetState<T>(StateScope scope, string key, T value);
    bool Contains(StateScope scope, string key);
    IReadOnlyDictionary<string, object?> Snapshot(StateScope scope);
    (long Revision, IReadOnlyDictionary<string, object?> Values) SnapshotWithRevision(StateScope scope) => (0, Snapshot(scope));
    void OpenInstance(StateScope scope);
    void CloseInstance(StateScope scope);
    IDisposable SubscribeState(StateScope scope, EventHandler<StateChangedEventArgs> handler);
    IDisposable Subscribe(string commandName, Func<object?, CancellationToken, Task> handler);
    Task ExecuteCommandAsync(string commandName, object? parameter = null,
        CancellationToken cancellationToken = default);
}
