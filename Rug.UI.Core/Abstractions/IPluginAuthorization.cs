#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

/// <summary>Trusted host service. Never export management methods to scripts or request adapters.</summary>
public interface IPluginAuthorization
{
    AuthorizationSnapshot Register(PluginManifest verifiedManifest);
    AuthorizationSnapshot Query(PluginInstallation installation);
    AuthorizationSnapshot Grant(PluginInstallation installation, IEnumerable<string> permissions);
    AuthorizationSnapshot Revoke(PluginInstallation installation, IEnumerable<string> permissions);
    AuthorizationTicket Bind(PluginManifest verifiedManifest, IEnumerable<string>? capabilities = null);
    IReadOnlyList<string> Missing(AuthorizationTicket ticket, IEnumerable<string> required);
    AuthorizationLease Admit(AuthorizationTicket ticket, IEnumerable<string> required);
    event EventHandler<AuthorizationChangedEventArgs>? Changed;
}
