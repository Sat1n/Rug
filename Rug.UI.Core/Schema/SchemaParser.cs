#nullable enable
using System.Text.Json;

namespace Rug.UI.Core.Schema;

public sealed class SchemaParser
{
    private const int MaxDepth = 24;
    private const int MaxNodes = 1000;

    public UISchemaNode Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth + 4 });
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            return ParseNode(document.RootElement, "$", 0, ids, ref count);
        }
        catch (JsonException ex)
        {
            throw new SchemaValidationException($"Invalid UI JSON: {ex.Message}");
        }
    }

    public async Task<UISchemaNode> ParseFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
    }

    private static UISchemaNode ParseNode(JsonElement element, string path, int depth,
        HashSet<string> ids, ref int count)
    {
        if (depth > MaxDepth || ++count > MaxNodes)
            throw new SchemaValidationException($"{path}: UI tree is too large.");
        if (element.ValueKind != JsonValueKind.Object)
            throw new SchemaValidationException($"{path}: expected an object.");
        string type = RequiredString(element, "type", path);
        if (type is not ("Page" or "Group" or "SelectorBarGroup" or "Tab") && !ControlCardSchema.Types.Contains(type))
            throw new SchemaValidationException($"{path}: unsupported control type '{type}'.");
        string id = RequiredString(element, "id", path);
        if (!ids.Add(id)) throw new SchemaValidationException($"{path}: duplicate id '{id}'.");

        string? stateKey = OptionalString(element, "stateKey", path);
        string? command = OptionalString(element, "command", path);
        if (type is "ToggleCard" or "SelectCard" or "SliderCard" or "TextCard" or "NumberCard" or "PresetPickerCard" or "TaskSelectorCard")
            Require(!string.IsNullOrWhiteSpace(stateKey), path, "stateKey is required.");
        if (type is "ButtonCard" or "TaskSelectorCard")
            Require(!string.IsNullOrWhiteSpace(command), path, "command is required.");
        if (type == "PresetPickerCard")
            Require(!string.IsNullOrWhiteSpace(OptionalString(element, "openFolderCommand", path))
                && !string.IsNullOrWhiteSpace(OptionalString(element, "importCommand", path)), path,
                "openFolderCommand and importCommand are required.");

        double? min = OptionalNumber(element, "min", path);
        double? max = OptionalNumber(element, "max", path);
        double? step = OptionalNumber(element, "step", path);
        if (type is "SliderCard" or "NumberCard")
        {
            Require(min.HasValue && max.HasValue && min < max, path, "min and max must define a range.");
            Require(!step.HasValue || step > 0, path, "step must be positive.");
        }
        string severity = OptionalString(element, "severity", path) ?? "Informational";
        if (type == "InfoBanner")
            Require(severity is "Informational" or "Success" or "Warning" or "Error", path, "invalid severity.");

        var options = ReadObjects(element, "options", path, (item, itemPath) =>
            new SchemaOption(RequiredString(item, "key", itemPath), RequiredString(item, "label", itemPath)));
        var tasks = ReadObjects(element, "tasks", path, (item, itemPath) =>
            new SchemaTask(RequiredString(item, "id", itemPath), RequiredString(item, "title", itemPath)));
        var steps = ReadObjects(element, "steps", path, (item, itemPath) =>
        {
            string status = OptionalString(item, "status", itemPath) ?? "Waiting";
            Require(status is "Waiting" or "Running" or "Succeeded" or "Failed", itemPath, "invalid status.");
            return new SchemaStep(RequiredString(item, "id", itemPath), RequiredString(item, "title", itemPath),
                status, OptionalString(item, "thumbnail", itemPath));
        });
        if (type is "SelectCard" or "PresetPickerCard")
        {
            Require(options.Count > 0, path, "options must contain at least one item.");
            Require(options.Select(option => option.Key).Distinct(StringComparer.Ordinal).Count() == options.Count,
                path, "option keys must be unique.");
        }
        if (type == "TaskSelectorCard")
            Require(tasks.Select(task => task.Id).Distinct(StringComparer.Ordinal).Count() == tasks.Count,
                path, "task IDs must be unique.");

        var children = ReadNodes(element, "children", path, depth, ids, ref count);
        var tabs = ReadNodes(element, "tabs", path, depth, ids, ref count);
        if (type == "SelectorBarGroup")
            Require(tabs.Count > 0 && tabs.All(tab => tab.Type == "Tab"), path, "tabs must contain Tab nodes.");
        else Require(tabs.Count == 0, path, "only SelectorBarGroup accepts tabs.");
        if (type is not ("Page" or "Group" or "Tab"))
            Require(children.Count == 0, path, "this control cannot have children.");

        JsonElement? defaultValue = element.TryGetProperty("default", out var value) ? value.Clone() : null;
        if (defaultValue.HasValue)
        {
            var kind = defaultValue.Value.ValueKind;
            bool valid = type switch
            {
                "ToggleCard" => kind is JsonValueKind.True or JsonValueKind.False,
                "SliderCard" or "NumberCard" => kind == JsonValueKind.Number,
                "SelectCard" or "PresetPickerCard" or "TextCard" => kind == JsonValueKind.String,
                "TaskSelectorCard" => kind == JsonValueKind.Array && defaultValue.Value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String),
                _ => true
            };
            Require(valid, path, "default has the wrong type.");
            if (type is "SelectCard" or "PresetPickerCard")
                Require(options.Any(option => option.Key == defaultValue.Value.GetString()), path,
                    "default must be an option key.");
            if (type is "SliderCard" or "NumberCard")
                Require(defaultValue.Value.GetDouble() >= min && defaultValue.Value.GetDouble() <= max,
                    path, "default is outside the numeric range.");
            if (type == "TaskSelectorCard")
            {
                var taskIds = tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
                Require(defaultValue.Value.EnumerateArray().All(item => taskIds.Contains(item.GetString()!)),
                    path, "default contains an unknown task ID.");
            }
        }
        return new UISchemaNode
        {
            Type = type, Id = id, Title = OptionalString(element, "title", path) ?? string.Empty,
            Description = OptionalString(element, "description", path) ?? string.Empty,
            StateKey = stateKey, Command = command,
            CommandParameter = element.TryGetProperty("parameter", out var parameter) ? parameter.Clone() : null,
            DefaultValue = defaultValue, Min = min, Max = max, Step = step, Severity = severity,
            Options = options, Tasks = tasks, Steps = steps, Children = children, Tabs = tabs,
            OpenFolderCommand = OptionalString(element, "openFolderCommand", path),
            ImportCommand = OptionalString(element, "importCommand", path)
        };
    }

    private static List<UISchemaNode> ReadNodes(JsonElement parent, string name, string path,
        int depth, HashSet<string> ids, ref int count)
    {
        var result = new List<UISchemaNode>();
        if (!parent.TryGetProperty(name, out var array)) return result;
        Require(array.ValueKind == JsonValueKind.Array, path, $"{name} must be an array.");
        int index = 0;
        foreach (var item in array.EnumerateArray())
            result.Add(ParseNode(item, $"{path}.{name}[{index++}]", depth + 1, ids, ref count));
        return result;
    }

    private static List<T> ReadObjects<T>(JsonElement parent, string name, string path,
        Func<JsonElement, string, T> parse)
    {
        var result = new List<T>();
        if (!parent.TryGetProperty(name, out var array)) return result;
        Require(array.ValueKind == JsonValueKind.Array, path, $"{name} must be an array.");
        int index = 0;
        foreach (var item in array.EnumerateArray())
        {
            string itemPath = $"{path}.{name}[{index++}]";
            Require(item.ValueKind == JsonValueKind.Object, itemPath, "expected an object.");
            result.Add(parse(item, itemPath));
        }
        return result;
    }

    private static string RequiredString(JsonElement element, string name, string path) =>
        OptionalString(element, name, path) is { Length: > 0 } result ? result :
            throw new SchemaValidationException($"{path}: {name} is required.");

    private static string? OptionalString(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        Require(value.ValueKind == JsonValueKind.String, path, $"{name} must be a string.");
        return value.GetString();
    }

    private static double? OptionalNumber(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out _), path,
            $"{name} must be a number.");
        return value.GetDouble();
    }

    private static void Require(bool condition, string path, string message)
    {
        if (!condition) throw new SchemaValidationException($"{path}: {message}");
    }
}
