#nullable enable
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Schema;

/// <summary>Resolves one timeline's schema key against the currently selected run.</summary>
public sealed class StepFlowStateBinding(string? stateKey, IReadOnlyList<SchemaStep> defaults)
{
    public string? StateKey { get; } = stateKey;
    public IReadOnlyList<SchemaStep> Defaults { get; } = defaults.ToArray();

    public bool Matches(StateChangedEventArgs change, StateScope? activeScope) =>
        StateKey is not null && activeScope == change.Scope &&
        string.Equals(StateKey, change.Key, StringComparison.Ordinal);

    public IReadOnlyList<SchemaStep> Read(ICommandBus bus, StateScope? activeScope,
        IReadOnlyDictionary<string, IReadOnlyList<SchemaStep>>? displayFallbacks = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        if (StateKey is not null && activeScope is StateScope scope && bus.Contains(scope, StateKey))
            return (bus.GetState<SchemaStep[]?>(scope, StateKey, []) ?? []).ToArray();
        if (StateKey is not null && displayFallbacks is not null &&
            displayFallbacks.TryGetValue(StateKey, out var fallback) && fallback.Count > 0)
            return fallback.ToArray();
        return Defaults.ToArray();
    }
}
