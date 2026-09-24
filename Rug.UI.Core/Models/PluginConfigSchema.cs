#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rug.UI.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginConfigControlType { Unknown, CheckBox, TextBox, Slider, ComboBox }

/// <summary>One control in a plugin's declarative config.json UI.</summary>
public sealed record PluginConfigField
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public PluginConfigControlType Type { get; init; }
    public JsonElement Default { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public List<string> Options { get; init; } = [];
}

public sealed record PluginConfigSchema
{
    public List<PluginConfigField> Fields { get; init; } = [];
}

/// <summary>Validated UI schema and typed default values for one plugin.</summary>
public sealed record PluginConfig(string PluginId, PluginConfigSchema Schema,
    IReadOnlyDictionary<string, object?> Defaults);
