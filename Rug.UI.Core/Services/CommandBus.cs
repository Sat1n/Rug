#nullable enable
using System.Collections.Concurrent;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>
/// Routes named async commands and projects StateStore changes onto the host UI
/// dispatcher. The core accepts dispatcher delegates so it never references WinUI.
/// </summary>
public sealed class CommandBus : ICommandBus
{
    private readonly StateStore _store;
    private readonly Func<Action, bool>? _enqueueUi;
    private readonly Func<bool>? _hasUiAccess;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long,
        Func<object?, CancellationToken, Task>>> _handlers = new(StringComparer.Ordinal);
    private long _nextSubscriptionId;
    private volatile bool _disposed;

    public CommandBus(StateStore store, Func<Action, bool>? enqueueUi = null,
        Func<bool>? hasUiAccess = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if ((enqueueUi is null) != (hasUiAccess is null))
            throw new ArgumentException("UI enqueue and access delegates must be supplied together.");
        _enqueueUi = enqueueUi;
        _hasUiAccess = hasUiAccess;
        _store.StateChanged += OnStoreChanged;
    }

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public T GetState<T>(string key, T defaultValue = default!)
    {
        ThrowIfDisposed();
        return _store.GetState(key, defaultValue);
    }

    public void SetState<T>(string key, T value)
    {
        ThrowIfDisposed();
        _store.SetState(key, value);
    }

    public IDisposable Subscribe(string commandName, Func<object?, CancellationToken, Task> handler)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(handler);
        long id = Interlocked.Increment(ref _nextSubscriptionId);
        _handlers.GetOrAdd(commandName, _ => new()).TryAdd(id, handler);
        return new Subscription(this, commandName, id);
    }

    public async Task ExecuteCommandAsync(string commandName, object? parameter = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        if (!_handlers.TryGetValue(commandName, out var subscribers) || subscribers.IsEmpty)
            throw new KeyNotFoundException($"No handler is subscribed to command '{commandName}'.");
        foreach (var subscriber in subscribers.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await subscriber.Value(parameter, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnStoreChanged(object? sender, StateChangedEventArgs change)
    {
        if (_disposed) return;
        if (_enqueueUi is null || _hasUiAccess!())
        {
            NotifyIfCurrent(change);
            return;
        }
        if (!_enqueueUi(() => NotifyIfCurrent(change)))
            throw new InvalidOperationException("UI dispatcher rejected a state-change notification.");
    }

    private void NotifyIfCurrent(StateChangedEventArgs change)
    {
        // Concurrent writers can call StateStore handlers out of order. A delayed
        // UI callback must never replace a newer visible value with an older one.
        if (!_disposed && _store.GetRevision(change.Key) == change.Revision)
            StateChanged?.Invoke(this, change);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CommandBus));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.StateChanged -= OnStoreChanged;
        _handlers.Clear();
    }

    private sealed class Subscription(CommandBus bus, string name, long id) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (bus._handlers.TryGetValue(name, out var subscribers))
                subscribers.TryRemove(id, out _);
        }
    }
}
