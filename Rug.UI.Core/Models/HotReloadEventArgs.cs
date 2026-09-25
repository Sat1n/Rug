#nullable enable

namespace Rug.UI.Core.Models;

public sealed class HotReloadEventArgs(string pluginId, string pluginDirectory,
    DateTimeOffset timestamp, int restartedInstances) : EventArgs
{
    public string PluginId { get; } = pluginId;
    public string PluginDirectory { get; } = pluginDirectory;
    public DateTimeOffset Timestamp { get; } = timestamp;
    public int RestartedInstances { get; } = restartedInstances;
}

public sealed class HotReloadFailedEventArgs(string pluginId, string pluginDirectory,
    Exception error) : EventArgs
{
    public string PluginId { get; } = pluginId;
    public string PluginDirectory { get; } = pluginDirectory;
    public Exception Error { get; } = error;
}
