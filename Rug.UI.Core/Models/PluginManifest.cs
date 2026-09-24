#nullable enable
namespace Rug.UI.Core.Models;

/// <summary>Runtime inputs supplied by the plugin loader. Permissions are denied by default.</summary>
public sealed record PluginManifest(
    IReadOnlySet<string> Permissions,
    IReadOnlyDictionary<string, string>? Configuration = null,
    nint TargetWindow = 0,
    bool BackgroundInput = true);
