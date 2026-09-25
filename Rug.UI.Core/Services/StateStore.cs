#nullable enable
using System.Collections.Concurrent;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Thread-safe key/value state with one notification per actual change.</summary>
public sealed class StateStore
{
    private static readonly object NullValue = new();
    private readonly ConcurrentDictionary<string, object> _values = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _revisions = new(StringComparer.Ordinal);
    private readonly object _writeGate = new();
    private long _nextRevision;

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public T GetState<T>(string key, T defaultValue = default!)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!_values.TryGetValue(key, out object? stored)) return defaultValue;
        if (ReferenceEquals(stored, NullValue)) return default!;
        return stored is T typed ? typed : throw new InvalidCastException(
            $"State '{key}' contains {stored.GetType().Name}, not {typeof(T).Name}.");
    }

    public void SetState<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        object stored = value is null ? NullValue : value;
        StateChangedEventArgs? change;
        lock (_writeGate)
        {
            bool existed = _values.TryGetValue(key, out object? previous);
            if (existed && Equals(previous, stored)) return;
            _values[key] = stored;
            long revision = ++_nextRevision;
            _revisions[key] = revision;
            change = new StateChangedEventArgs(key,
                existed && !ReferenceEquals(previous, NullValue) ? previous : null, value,
                revision);
        }
        // Subscribers may re-enter the store or dispatch to another thread.
        StateChanged?.Invoke(this, change);
    }

    internal long GetRevision(string key) => _revisions.TryGetValue(key, out long revision) ? revision : 0;
}
