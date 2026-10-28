#nullable enable
namespace Rug.UI.Core.Models;

/// <summary>One run, distinct from its plugin and OS window handle.</summary>
public sealed record RunIdentity
{
    public RunIdentity(string pluginId, Guid instanceId, long runGeneration,
        Guid? targetBindingId, nint targetHwnd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (instanceId == Guid.Empty) throw new ArgumentException("Instance ID is required.", nameof(instanceId));
        if (runGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(runGeneration));
        if ((targetHwnd == 0) != (targetBindingId is null) || targetBindingId == Guid.Empty)
            throw new ArgumentException("Windowless runs have no binding; windowed runs require a binding ID.",
                nameof(targetBindingId));
        PluginId = pluginId;
        InstanceId = instanceId;
        RunGeneration = runGeneration;
        TargetBindingId = targetBindingId;
        TargetHwnd = targetHwnd;
    }

    public string PluginId { get; }
    public Guid InstanceId { get; }
    public long RunGeneration { get; }
    public Guid? TargetBindingId { get; }
    public nint TargetHwnd { get; }
    public bool IsWindowless => TargetBindingId is null;
    public StateScope InstanceScope => StateScope.Instance(PluginId, InstanceId);
}
