#nullable enable
using System.Text.Json.Serialization;

namespace Rug.UI.Core.Models;

/// <summary>Plugin metadata loaded from manifest.json plus host-only runtime context.</summary>
public sealed record PluginManifest
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Entry { get; init; } = "main.lua";
    public string TargetProcess { get; init; } = "";
    public List<string> Permissions { get; init; } = [];

    [JsonIgnore] public IReadOnlyDictionary<string, object?>? Configuration { get; init; }
    [JsonIgnore] public nint TargetWindow { get; init; }
    [JsonIgnore] public bool BackgroundInput { get; init; } = true;
    [JsonIgnore] public string PluginDirectory { get; init; } = "";

    public PluginManifest() { }

    /// <summary>Convenience constructor for host-created runtime manifests.</summary>
    public PluginManifest(IEnumerable<string> permissions,
        IReadOnlyDictionary<string, object?>? Configuration = null,
        nint TargetWindow = 0, bool BackgroundInput = true)
    {
        Permissions = permissions.ToList();
        this.Configuration = Configuration;
        this.TargetWindow = TargetWindow;
        this.BackgroundInput = BackgroundInput;
    }
}
