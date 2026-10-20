#nullable enable
using System.Text.Json;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Validate JSON shapes before dispatch, without interpreting service exceptions as parse failures.</summary>
internal static class OperationParameterValidator
{
    /// <summary>@shape parameters: JSON object; return: valid required fields, scalar types and static ranges.
    /// @source parameters: Models/OperationModels.cs#class:OperationRequest</summary>
    public static bool IsValid(AutomationOperation operation, JsonElement parameters) => operation switch
    {
        AutomationOperation.PrepareRuntimeUpdate or AutomationOperation.QueryRuntimeUpdate => !parameters.EnumerateObject().Any(),
        AutomationOperation.CancelRuntimeUpdate => parameters.EnumerateObject().Count() == 1 &&
            parameters.TryGetProperty("updateId", out var update) && update.ValueKind == JsonValueKind.String && update.TryGetGuid(out var updateId) && updateId != Guid.Empty,
        AutomationOperation.RestoreCheckpoint => parameters.EnumerateObject().Count() == 1 &&
            parameters.TryGetProperty("selectionId", out var selection) && selection.ValueKind == JsonValueKind.String &&
            selection.TryGetGuid(out var selectionId) && selectionId != Guid.Empty,
        AutomationOperation.VerifyRecovery => parameters.TryGetProperty("recoveryId", out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var recoveryId) && recoveryId != Guid.Empty,
        AutomationOperation.Sleep => Integer(parameters, "ms", 0, int.MaxValue),
        AutomationOperation.Click => Integer(parameters, "x", 0, int.MaxValue) && Integer(parameters, "y", 0, int.MaxValue) &&
            EnumValue<MouseButton>(parameters, "button") && EnumValue<InputDeliveryMode>(parameters, "mode"),
        AutomationOperation.PressKey => Integer(parameters, "key", 1, 255) && Integer(parameters, "durationMs", 0, 60000, optional: true),
        AutomationOperation.FindImage => String(parameters, "path", required: true) && Threshold(parameters) && Region(parameters),
        AutomationOperation.Ocr => String(parameters, "language") && EnumValue<OcrEngineType>(parameters, "engine") && Region(parameters),
        AutomationOperation.Start => LaunchInputs(parameters),
        _ => true
    };
    private static bool Integer(JsonElement parameters, string key, int min, int max, bool optional = false)
    {
        if (!parameters.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return optional;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= min && number <= max;
    }
    private static bool EnumValue<T>(JsonElement parameters, string key) where T : struct, Enum
    {
        if (!parameters.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return true;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && Enum.IsDefined(typeof(T), number);
    }
    private static bool String(JsonElement parameters, string key, bool required = false)
    {
        if (!parameters.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return !required;
        return value.ValueKind == JsonValueKind.String && (!required || !string.IsNullOrWhiteSpace(value.GetString()));
    }
    private static bool Threshold(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("threshold", out var value) || value.ValueKind == JsonValueKind.Null) return true;
        return value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float number) && float.IsFinite(number) && number is >= 0 and <= 1;
    }
    private static bool Region(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("region", out var region) || region.ValueKind == JsonValueKind.Null) return true;
        if (region.ValueKind != JsonValueKind.Object || !Integer(region, "X", 0, int.MaxValue) ||
            !Integer(region, "Y", 0, int.MaxValue) || !Integer(region, "Width", 1, int.MaxValue) || !Integer(region, "Height", 1, int.MaxValue)) return false;
        return (long)region.GetProperty("X").GetInt32() + region.GetProperty("Width").GetInt32() <= int.MaxValue &&
            (long)region.GetProperty("Y").GetInt32() + region.GetProperty("Height").GetInt32() <= int.MaxValue;
    }
    private static bool LaunchInputs(JsonElement parameters)
    {
        try
        {
            var validation = new StateStore();
            foreach (var property in parameters.EnumerateObject()) validation.SetState(property.Name, property.Value);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or JsonException) { return false; }
    }
}
