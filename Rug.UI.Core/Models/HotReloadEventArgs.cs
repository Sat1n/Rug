#nullable enable

namespace Rug.UI.Core.Models;

public sealed class HotReloadEventArgs(string pluginId, string pluginDirectory,
    DateTimeOffset timestamp, int restartedInstances = 0, PluginUpdateStatus status = PluginUpdateStatus.UiApplied,
    long revision = 0, string? contentVersion = null) : EventArgs
{
    public string PluginId { get; } = pluginId;
    public string PluginDirectory { get; } = pluginDirectory;
    public DateTimeOffset Timestamp { get; } = timestamp;
    public int RestartedInstances { get; } = restartedInstances;
    public PluginUpdateStatus Status { get; } = status;
    public long Revision { get; } = revision;
    public string? ContentVersion { get; } = contentVersion;
}

public sealed class HotReloadFailedEventArgs(string pluginId, string pluginDirectory,
    Exception error) : EventArgs
{
    public string PluginId { get; } = pluginId;
    public string PluginDirectory { get; } = pluginDirectory;
    public Exception Error { get; } = error;
    public PluginUpdateStatus Status => PluginUpdateStatus.ValidationFailed;
}
