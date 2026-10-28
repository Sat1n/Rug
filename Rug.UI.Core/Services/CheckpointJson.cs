#nullable enable
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Canonical bounded business JSON and immutable loaded-code identity; never hashes submission-time disk content.</summary>
public static class CheckpointJson
{
    public const int SchemaVersion = 2;
    public const int StateBytes = 64 * 1024, RecordBytes = 128 * 1024, MaxActions = 128, MaxIdLength = 64;
    internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 24 };
    private static readonly HashSet<string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    { "password", "secret", "apiKey", "api_key", "accessToken", "refreshToken", "credentials", "authorization", "hwnd", "nativeHandle", "luaStack", "clrObject" };
    public static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
    public static string InstallationId(PluginInstallation installation) => Hash(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new[] { installation.PluginId, installation.Directory })));
    public static LoadedScriptContent Loaded(string entry, string source)
    {
        string name = entry.Replace('\\', '/');
        return new(name, source, Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { "rug-entry-v1", name, source }))));
    }
    public static string ConfigHash(IReadOnlyDictionary<string, object?> configuration) =>
        Hash(Canonical(Parse(JsonSerializer.Serialize(configuration, Options), sensitive: false)));
    public static JsonElement Parse(string json, bool sensitive = true)
    {
        if (Encoding.UTF8.GetByteCount(json) > StateBytes) throw new ArgumentException("Checkpoint JSON exceeds 64KiB.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        Validate(document.RootElement, sensitive);
        if (Canonical(document.RootElement).Length > StateBytes) throw new ArgumentException("Encoded checkpoint JSON exceeds 64KiB.");
        return document.RootElement.Clone();
    }
    private static void Validate(JsonElement element, bool sensitive)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || sensitive && Sensitive.Contains(property.Name))
                    throw new ArgumentException("Duplicate or internal/credential business field.");
                Validate(property.Value, sensitive);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Validate(item, sensitive);
        else if (element.ValueKind == JsonValueKind.Number && (!element.TryGetDouble(out double value) || !double.IsFinite(value)))
            throw new ArgumentException("Nonfinite checkpoint number.");
        else if (element.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("Missing checkpoint value.");
    }
    internal static void ValidateEnvelope(JsonElement element) => Validate(element, sensitive: false);
    public static bool ValidId(string? value) => value is { Length: > 0 and <= MaxIdLength } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    public static IReadOnlyList<string> Actions(string json)
    {
        var root = Parse(json);
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > MaxActions) throw new ArgumentException("Invalid completed actions.");
        string[] actions = root.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new ArgumentException("Action ID must be string.")).ToArray();
        if (actions.Any(a => !ValidId(a)) || actions.Distinct(StringComparer.Ordinal).Count() != actions.Length) throw new ArgumentException("Invalid action IDs.");
        return Array.AsReadOnly(actions);
    }
    internal static byte[] Canonical(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, element);
        return stream.ToArray();
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name); Write(writer, property.Value);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.Number:
                writer.WriteRawValue(CanonicalNumber(value.GetRawText()));
                break;
            default: value.WriteTo(writer); break;
        }
    }
    /// <summary>@shape input: valid bounded JSON number; output: exact signed integer coefficient times a base-10 exponent.
    /// @source input: System.Text.Json validated lexical token, never a rounded CLR numeric conversion.</summary>
    private static string CanonicalNumber(string raw)
    {
        bool negative = raw[0] == '-';
        int exponentAt = raw.IndexOfAny(['e', 'E']);
        string mantissa = raw[(negative ? 1 : 0)..(exponentAt < 0 ? raw.Length : exponentAt)];
        int pointAt = mantissa.IndexOf('.');
        int fractionalDigits = pointAt < 0 ? 0 : mantissa.Length - pointAt - 1;
        string digits = mantissa.Replace(".", "").TrimStart('0');
        if (digits.Length == 0) return "0";
        int end = digits.Length;
        while (digits[end - 1] == '0') end--;
        BigInteger exponent = exponentAt < 0 ? BigInteger.Zero :
            BigInteger.Parse(raw.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        exponent += digits.Length - end - fractionalDigits;
        return (negative ? "-" : "") + digits[..end] +
            (exponent.IsZero ? "" : "e" + exponent.ToString(CultureInfo.InvariantCulture));
    }
}
