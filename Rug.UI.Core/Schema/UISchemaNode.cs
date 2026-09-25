#nullable enable
using System.Text.Json;

namespace Rug.UI.Core.Schema;

/// <summary>A validated, platform-neutral declarative control tree.</summary>
public sealed class UISchemaNode
{
    public required string Type { get; init; }
    public required string Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? StateKey { get; init; }
    public string? Command { get; init; }
    public JsonElement? CommandParameter { get; init; }
    public JsonElement? DefaultValue { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public string Severity { get; init; } = "Informational";
    public IReadOnlyList<SchemaOption> Options { get; init; } = [];
    public IReadOnlyList<SchemaTask> Tasks { get; init; } = [];
    public IReadOnlyList<SchemaStep> Steps { get; init; } = [];
    public IReadOnlyList<UISchemaNode> Children { get; init; } = [];
    public IReadOnlyList<UISchemaNode> Tabs { get; init; } = [];
    public string? OpenFolderCommand { get; init; }
    public string? ImportCommand { get; init; }
}

public sealed record SchemaOption(string Key, string Label);
public sealed record SchemaTask(string Id, string Title);
public sealed record SchemaStep(string Id, string Title, string Status, string? Thumbnail);

/// <summary>Named settings-card node; all cards are represented by <see cref="UISchemaNode"/>.</summary>
public static class ControlCardSchema
{
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal)
    {
        "ToggleCard", "SelectCard", "SliderCard", "TextCard", "NumberCard", "ButtonCard",
        "InfoBanner", "PresetPickerCard", "TaskSelectorCard", "StepFlowView"
    };
}

public sealed class SchemaValidationException(string message) : FormatException(message);
