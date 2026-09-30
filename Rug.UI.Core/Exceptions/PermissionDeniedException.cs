#nullable enable
namespace Rug.UI.Core.Exceptions;

public sealed class PermissionDeniedException : UnauthorizedAccessException
{
    public string PluginId { get; }
    public string Permission { get; }
    public string Api { get; }
    public IReadOnlyList<string> MissingPermissions { get; }

    public PermissionDeniedException(string pluginId, string permission, string api, IEnumerable<string>? missingPermissions = null)
        : base($"Plugin '{pluginId}' is not permitted to call {api} (requires '{permission}').")
    {
        PluginId = pluginId;
        Permission = permission;
        Api = api;
        MissingPermissions = Array.AsReadOnly((missingPermissions ?? new[] { permission }).Distinct(StringComparer.Ordinal).ToArray());
    }
}
