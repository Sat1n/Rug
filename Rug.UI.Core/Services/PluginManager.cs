#nullable enable
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Loads plugin declarations from immediate subdirectories of a root.</summary>
public sealed class PluginManager(ILogger<PluginManager> logger) : IPluginManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, PluginConfig> _configs = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<PluginManifest> ScanPlugins(string pluginsRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRootPath);
        _configs.Clear();
        if (!Directory.Exists(pluginsRootPath)) return [];

        var accepted = new List<PluginManifest>();
        foreach (string directory in Directory.EnumerateDirectories(pluginsRootPath).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (IsReparsePoint(directory)) throw new InvalidDataException("Plugin directory is a link.");
                string manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                if (IsReparsePoint(manifestPath)) throw new InvalidDataException("Manifest is a link.");
                PluginManifest manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), JsonOptions)
                    ?? throw new InvalidDataException("manifest.json is null.");
                ValidateManifest(manifest, directory);
                if (_configs.ContainsKey(manifest.Id)) throw new InvalidDataException($"Duplicate plugin id '{manifest.Id}'.");
                PluginConfig config = ReadConfig(directory, manifest.Id);
                manifest = manifest with { Configuration = config.Defaults, PluginDirectory = Path.GetFullPath(directory) };
                _configs.Add(manifest.Id, config);
                accepted.Add(manifest);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                logger.LogWarning(ex, "Skipping invalid plugin at {Directory}", directory);
            }
        }
        return accepted;
    }

    public PluginConfig GetPluginConfig(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _configs.TryGetValue(pluginId, out PluginConfig? config)
            ? config : throw new KeyNotFoundException($"Plugin '{pluginId}' has not been scanned or is invalid.");
    }

    private static void ValidateManifest(PluginManifest manifest, string directory)
    {
        if (!Regex.IsMatch(manifest.Id ?? "", "^[A-Za-z0-9][A-Za-z0-9._-]*$"))
            throw new InvalidDataException("Plugin id must be a safe nonempty identifier.");
        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.TargetProcess))
            throw new InvalidDataException("Plugin name, version and targetProcess are required.");
        if (manifest.Permissions is null || manifest.Permissions.Any(string.IsNullOrWhiteSpace) ||
            manifest.Permissions.Count != manifest.Permissions.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidDataException("Permissions must be a distinct list of nonempty names.");
        if (manifest.InputDelivery is { } mode && !Enum.IsDefined(mode))
            throw new InvalidDataException("Input delivery mode is not supported.");
        if (string.IsNullOrWhiteSpace(manifest.Entry) || Path.IsPathRooted(manifest.Entry) ||
            manifest.Entry.Split('/', '\\').Any(part => part is ".." or "." or ""))
            throw new InvalidDataException("Entry must be a relative path inside the plugin directory.");

        string fullDirectory = Path.GetFullPath(directory);
        string entry = Path.GetFullPath(Path.Combine(fullDirectory, manifest.Entry));
        if (!entry.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(entry) || IsReparsePoint(entry))
            throw new InvalidDataException("Entry script is missing or outside the plugin directory.");
        string? parent = Path.GetDirectoryName(entry);
        while (parent is not null && !parent.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (IsReparsePoint(parent)) throw new InvalidDataException("Entry path crosses a link.");
            parent = Path.GetDirectoryName(parent);
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static PluginConfig ReadConfig(string directory, string pluginId)
    {
        string path = Path.Combine(directory, "config.json");
        if (!File.Exists(path)) return new(pluginId, new PluginConfigSchema(), new Dictionary<string, object?>());
        if (IsReparsePoint(path)) throw new InvalidDataException("Config is a link.");
        PluginConfigSchema schema = JsonSerializer.Deserialize<PluginConfigSchema>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("config.json is null.");
        if (schema.Fields is null) throw new InvalidDataException("config.json fields must be an array.");
        var defaults = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (PluginConfigField field in schema.Fields)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Key) ||
                !Regex.IsMatch(field.Key, "^[A-Za-z][A-Za-z0-9_.-]*$") || defaults.ContainsKey(field.Key))
                throw new InvalidDataException("Config field keys must be unique safe identifiers.");
            defaults.Add(field.Key, ValidateDefault(field));
        }
        return new(pluginId, schema, defaults);
    }

    private static object? ValidateDefault(PluginConfigField field)
    {
        JsonElement value = field.Default;
        switch (field.Type)
        {
            case PluginConfigControlType.CheckBox when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return value.GetBoolean();
            case PluginConfigControlType.TextBox when value.ValueKind == JsonValueKind.String:
                return value.GetString();
            case PluginConfigControlType.Slider when value.ValueKind == JsonValueKind.Number:
                double number = value.GetDouble();
                if (!double.IsFinite(number) || field.Min is null || field.Max is null ||
                    !double.IsFinite(field.Min.Value) || !double.IsFinite(field.Max.Value) ||
                    field.Min >= field.Max || number < field.Min || number > field.Max ||
                    field.Step is double step && (!double.IsFinite(step) || step <= 0))
                    break;
                return number;
            case PluginConfigControlType.ComboBox when value.ValueKind == JsonValueKind.String:
                string selected = value.GetString()!;
                if (field.Options is { Count: > 0 } &&
                    field.Options.All(option => !string.IsNullOrWhiteSpace(option)) &&
                    field.Options.Count == field.Options.Distinct(StringComparer.Ordinal).Count() &&
                    field.Options.Contains(selected, StringComparer.Ordinal))
                    return selected;
                break;
        }
        throw new InvalidDataException($"Invalid type, metadata or default for config field '{field.Key}'.");
    }
}
