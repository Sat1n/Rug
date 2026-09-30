#nullable enable
using Microsoft.Extensions.Logging;
using Rug.UI.Core.Exceptions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Services;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Security;

/// <summary>Exact-match permission gate shared by Lua and future host APIs.</summary>
public sealed class PermissionInterceptor
{
    private readonly IPluginAuthorization _authorization;
    private readonly AuthorizationTicket _ticket;
    private readonly HashSet<string> _requested;
    private readonly string _pluginId;
    private readonly ILogger _logger;

    public PermissionInterceptor(PluginManifest manifest, ILogger logger, IPluginAuthorization? authorization = null,
        AuthorizationTicket? ticket = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(manifest.Permissions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pluginId = string.IsNullOrWhiteSpace(manifest.Id) ? "<unidentified>" : manifest.Id;
        _authorization = authorization ?? new PluginAuthorization();
        _ticket = ticket ?? _authorization.Bind(manifest);
        if (_ticket.Installation != PluginInstallation.From(manifest))
            throw new ArgumentException("Authorization ticket belongs to another installation.", nameof(ticket));
        _requested = new(manifest.Permissions, StringComparer.Ordinal);
    }

    public void Demand(string permission, string api)
    {
        if (_requested.Contains(permission) && _authorization.Missing(_ticket, [permission]).Count == 0) return;
        _logger.LogWarning("Permission denied: plugin={PluginId}, permission={Permission}, api={Api}",
            _pluginId, permission, api);
        throw new PermissionDeniedException(_pluginId, permission, api);
    }
}
