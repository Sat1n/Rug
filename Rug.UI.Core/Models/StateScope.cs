#nullable enable
namespace Rug.UI.Core.Models;

public enum StateDomain { AppState, PluginConfig, InstanceState }

/// <summary>A structural state address; user keys never encode tenancy.</summary>
public readonly record struct StateScope
{
    private StateScope(StateDomain domain, string? pluginId, Guid instanceId)
    {
        Domain = domain;
        PluginId = pluginId;
        InstanceId = instanceId;
    }

    public StateDomain Domain { get; }
    public string? PluginId { get; }
    public Guid InstanceId { get; }

    public static StateScope App { get; } = new(StateDomain.AppState, null, Guid.Empty);

    public static StateScope Plugin(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return new(StateDomain.PluginConfig, pluginId.ToLowerInvariant(), Guid.Empty);
    }

    public static StateScope Instance(string pluginId, Guid instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (instanceId == Guid.Empty) throw new ArgumentException("Instance ID is required.", nameof(instanceId));
        return new(StateDomain.InstanceState, pluginId.ToLowerInvariant(), instanceId);
    }
}
