using System.Text.Json;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Services;

/// <summary>Host binds UI callers; schema parameters supply only launch inputs, never grants or HWND.</summary>
internal static class PluginAutomationCommands
{
    public static async Task<object?> ExecuteAsync(ITaskScheduler scheduler, string pluginId,
        AutomationOperation operation, RunIdentity? target = null,
        IReadOnlyDictionary<string, object?>? inputs = null, Action<RunIdentity>? onReady = null,
        CancellationToken cancellationToken = default)
    {
        if (operation == AutomationOperation.Start &&
            !await App.GetService<PluginPermissionConsent>().ConfirmStartAsync(pluginId, cancellationToken))
            throw new OperationCanceledException("Plugin permission confirmation was declined.");
        var caller = await scheduler.Gateway.CreateContextAsync(pluginId, "plugin-ui", AutomationSource.PluginUI,
            onReady: onReady, cancellationToken: cancellationToken);
        var request = new OperationRequest(operation, JsonSerializer.SerializeToElement(inputs ?? new Dictionary<string, object?>()),
            Guid.NewGuid(), Guid.NewGuid().ToString("N"), target is null ? new(pluginId) : OperationTarget.ForRun(target),
            DateTimeOffset.UtcNow.AddSeconds(30));
        OperationResult result = await scheduler.Gateway.ExecuteAsync(caller, request, cancellationToken);
        return result.RequireSuccess();
    }
}
