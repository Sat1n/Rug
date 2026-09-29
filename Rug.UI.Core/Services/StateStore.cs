#nullable enable
using System.Collections.Concurrent;
using System.Text.Json;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Bounded JSON state partitioned by structural scope.</summary>
public sealed class StateStore
{
    public const int MaxValueBytes = 64 * 1024;
    public const int MaxDepth = 16;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = MaxDepth };
    private readonly ConcurrentDictionary<StateScope, Bucket> _buckets = new();

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public static object? CloneValue(object? value)
    {
        byte[] json = Encode(value);
        return value is null ? null : JsonSerializer.Deserialize(json, value.GetType(), JsonOptions);
    }

    // Host-only AppState compatibility surface.
    public T GetState<T>(string key, T defaultValue = default!) => GetState(StateScope.App, key, defaultValue);
    public void SetState<T>(string key, T value) => SetState(StateScope.App, key, value);

    public T GetState<T>(StateScope scope, string key, T defaultValue = default!)
    {
        Validate(scope, key);
        if (!_buckets.TryGetValue(scope, out Bucket? bucket)) return defaultValue;
        lock (bucket.Gate)
        {
            if (!bucket.Values.TryGetValue(key, out StoredValue? stored)) return defaultValue;
            if (stored.Json.AsSpan().SequenceEqual("null"u8)) return default!;
            try
            {
                if (typeof(T) == typeof(object))
                    return (T)JsonSerializer.Deserialize(stored.Json, stored.ValueType, JsonOptions)!;
                return JsonSerializer.Deserialize<T>(stored.Json, JsonOptions)!;
            }
            catch (JsonException error)
            {
                throw new InvalidCastException($"State '{key}' cannot be read as {typeof(T).Name}.", error);
            }
        }
    }

    public bool Contains(StateScope scope, string key)
    {
        Validate(scope, key);
        if (!_buckets.TryGetValue(scope, out Bucket? bucket)) return false;
        lock (bucket.Gate) return bucket.Values.ContainsKey(key);
    }

    public void SetState<T>(StateScope scope, string key, T value)
    {
        Validate(scope, key);
        byte[] encoded = Encode(value);
        Bucket bucket = _buckets.GetOrAdd(scope, _ => new Bucket());
        StateChangedEventArgs change;
        lock (bucket.Gate)
        {
            if (bucket.Closed) throw new ObjectDisposedException(nameof(StateScope), "Instance state has stopped.");
            bucket.Values.TryGetValue(key, out StoredValue? previous);
            if (previous is not null && previous.Json.AsSpan().SequenceEqual(encoded)) return;
            long revision = ++bucket.Revision;
            var stored = new StoredValue(encoded, value?.GetType() ?? typeof(object), revision);
            bucket.Values[key] = stored;
            change = new StateChangedEventArgs(scope, key, EventValue(previous), EventValue(stored), revision,
                previous is not null);
        }
        StateChanged?.Invoke(this, change);
    }

    /// <summary>Return detached values; absent keys do not appear, explicit null keys do.</summary>
    public IReadOnlyDictionary<string, object?> Snapshot(StateScope scope)
    {
        ValidateScope(scope);
        if (!_buckets.TryGetValue(scope, out Bucket? bucket)) return new Dictionary<string, object?>();
        lock (bucket.Gate)
            return bucket.Values.ToDictionary(pair => pair.Key, pair => Decode(pair.Value), StringComparer.Ordinal);
    }

    /// <summary>Close a stopped instance permanently and invalidate queued notifications.</summary>
    public void CloseInstance(StateScope scope)
    {
        if (scope.Domain != StateDomain.InstanceState) throw new ArgumentException("Only instances can close.", nameof(scope));
        Bucket bucket = _buckets.GetOrAdd(scope, _ => new Bucket());
        lock (bucket.Gate)
        {
            bucket.Closed = true;
            bucket.Values.Clear();
        }
    }

    internal long GetRevision(StateScope scope, string key)
    {
        if (!_buckets.TryGetValue(scope, out Bucket? bucket)) return 0;
        lock (bucket.Gate)
            return !bucket.Closed && bucket.Values.TryGetValue(key, out StoredValue? value)
                ? value.Revision : 0;
    }

    internal long GetRevision(string key) => GetRevision(StateScope.App, key);

    internal bool IsClosed(StateScope scope)
    {
        if (!_buckets.TryGetValue(scope, out Bucket? bucket)) return false;
        lock (bucket.Gate) return bucket.Closed;
    }

    private static byte[] Encode<T>(T value)
    {
        if (value is Delegate or nint or nuint)
            throw new ArgumentException("State value is not JSON data.", nameof(value));
        byte[] json;
        try { json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions); }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        { throw new ArgumentException("State value is not bounded JSON data.", nameof(value), error); }
        if (json.Length > MaxValueBytes)
            throw new ArgumentOutOfRangeException(nameof(value), $"State value exceeds {MaxValueBytes} bytes.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        return json;
    }

    private static object? Decode(StoredValue? stored) => stored is null ||
        stored.Json.AsSpan().SequenceEqual("null"u8) ? null :
        JsonSerializer.Deserialize(stored.Json, stored.ValueType, JsonOptions);

    private static object? EventValue(StoredValue? stored)
    {
        if (stored is null) return null;
        using var document = JsonDocument.Parse(stored.Json);
        JsonElement value = document.RootElement;
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => Decode(stored),
            _ => value.Clone()
        };
    }

    private static void Validate(StateScope scope, string key)
    {
        ValidateScope(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
    }

    private static void ValidateScope(StateScope scope)
    {
        if (scope.Domain == StateDomain.AppState && scope.PluginId is null && scope.InstanceId == Guid.Empty) return;
        if (string.IsNullOrWhiteSpace(scope.PluginId)) throw new ArgumentException("Invalid state scope.", nameof(scope));
        if (scope.Domain == StateDomain.PluginConfig && scope.InstanceId == Guid.Empty) return;
        if (scope.Domain == StateDomain.InstanceState && scope.InstanceId != Guid.Empty) return;
        throw new ArgumentException("Invalid state scope.", nameof(scope));
    }

    private sealed class Bucket
    {
        public object Gate { get; } = new();
        public Dictionary<string, StoredValue> Values { get; } = new(StringComparer.Ordinal);
        public long Revision;
        public bool Closed;
    }

    private sealed record StoredValue(byte[] Json, Type ValueType, long Revision);
}
