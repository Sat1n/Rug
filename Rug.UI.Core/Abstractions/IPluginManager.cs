using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

public interface IPluginManager
{
    /// <summary>Scan immediate child directories and cache valid plugin metadata/configuration.</summary>
    IEnumerable<PluginManifest> ScanPlugins(string pluginsRootPath);

    /// <summary>Get the validated UI configuration for a previously scanned plugin.</summary>
    PluginConfig GetPluginConfig(string pluginId);
}
