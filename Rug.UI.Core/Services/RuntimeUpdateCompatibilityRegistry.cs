#nullable enable
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

public sealed class RuntimeUpdateCompatibilityRegistry : IRuntimeUpdateCompatibilityRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(PluginInstallation, string, string, string), (RuntimeUpdateCompatibility, ICheckpointRestoreValidator)> _rules = [];
    public IDisposable Register(RuntimeUpdateCompatibility rule, ICheckpointRestoreValidator validator)
    {
        if (!CheckpointJson.ValidId(rule.RuleId) || !CheckpointJson.ValidId(rule.NodeId) || rule.StateVersion <= 0)
            throw new ArgumentException("Invalid update rule.");
        ArgumentNullException.ThrowIfNull(validator);
        var lifetime = new CancellationTokenSource();
        rule = rule with { CompletedActionIds = CheckpointJson.Actions(System.Text.Json.JsonSerializer.Serialize(rule.CompletedActionIds)), RegistrationToken = lifetime.Token };
        var key = (rule.Installation, rule.SourceSnapshot, rule.TargetSnapshot, rule.NodeId);
        var value = (rule, validator);
        lock (_gate)
        {
            if (_rules.Count >= 128) throw new InvalidOperationException("Update rule capacity exceeded.");
            if (!_rules.TryAdd(key, value)) throw new InvalidOperationException("Update rule already registered.");
        }
        return new Registration(() =>
        {
            lock (_gate) if (_rules.GetValueOrDefault(key) == value) _rules.Remove(key);
            lifetime.Cancel(); lifetime.Dispose(); // Outside the registry lock.
        });
    }
    public (RuntimeUpdateCompatibility Rule, ICheckpointRestoreValidator Validator)? Find(PluginInstallation installation,
        string sourceSnapshot, string targetSnapshot, string nodeId)
    { lock (_gate) return _rules.TryGetValue((installation, sourceSnapshot, targetSnapshot, nodeId), out var value) ? value : null; }
    private sealed class Registration(Action remove) : IDisposable
    { private int _disposed; public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) remove(); } }
}
