using Microsoft.Extensions.Logging;
using Rug.UI.Core.Exceptions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Security;

/// <summary>Exact-match permission gate shared by Lua and future host APIs.</summary>
public sealed class PermissionInterceptor
{
    private readonly HashSet<string> _granted;
    private readonly string _pluginId;
    private readonly ILogger _logger;

    public PermissionInterceptor(PluginManifest manifest, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(manifest.Permissions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pluginId = string.IsNullOrWhiteSpace(manifest.Id) ? "<unidentified>" : manifest.Id;
        _granted = new HashSet<string>(manifest.Permissions, StringComparer.Ordinal);
    }

    public void Demand(string permission, string api)
    {
        if (_granted.Contains(permission)) return;
        _logger.LogWarning("Permission denied: plugin={PluginId}, permission={Permission}, api={Api}",
            _pluginId, permission, api);
        throw new PermissionDeniedException(_pluginId, permission, api);
    }
}
