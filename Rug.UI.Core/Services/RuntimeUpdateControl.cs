#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

public sealed partial class TaskScheduler
{
    public async Task<AutomationCallContext> CreateRuntimeUpdateContextAsync(string pluginId, CancellationToken token = default)
    {
        var caller = await Gateway.CreateContextAsync(pluginId, "host.runtime-update", AutomationSource.PluginUI, cancellationToken: token).ConfigureAwait(false);
        caller.RuntimeUpdateHost = true; return caller;
    }
    /// <summary>@shape request: host-only plugin scope; preparation holds the startup lease only while arming.
    /// @source checkpoint: CheckpointCoordinator.cs#class:CheckpointCoordinator</summary>
    private async Task<object?> ControlRuntimeUpdateAsync(AutomationCallContext caller, OperationRequest request,
        CancellationToken execution, CancellationToken original, CheckpointStartBoundary boundary)
    {
        var installation = caller.Authorization.Installation;
        if (request.Operation == AutomationOperation.QueryRuntimeUpdate) return RuntimeUpdates.Query(installation);
        if (request.Operation == AutomationOperation.CancelRuntimeUpdate)
            return RuntimeUpdates.Cancel(installation, request.Parameters.GetProperty("updateId").GetGuid());
        if (request.Operation == AutomationOperation.SwitchRuntimeUpdate)
            return await SwitchRuntimeUpdateAsync(caller, request, execution, original, boundary).ConfigureAwait(false);
        var observed = _instances.Values.Where(r => r.PluginId.Equals(caller.PluginId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (observed.Length is 0 or > RuntimeUpdateCoordinator.MaxInstances || observed.Any(r => r.TaskStatus != AutomationTaskStatus.Running || r.PauseRequested))
            throw new AutomationGateway.GatewayRejectedException(OperationError.UpdateUnavailable);
        // Even replaceable trusted node lookups must not hold the startup lease.
        foreach (var run in observed)
            if (_checkpoints?.SupportsUpdateFreeze(run) != true) throw new AutomationGateway.GatewayRejectedException(OperationError.UpdateUnavailable);
        await using var lease = await AcquirePluginReloadAsync(caller.PluginId, execution).ConfigureAwait(false);
        var manifest = await GetPluginAsync(caller.PluginId, execution).ConfigureAwait(false);
        if (PluginInstallation.From(manifest) != installation) throw new AutomationGateway.GatewayRejectedException(OperationError.InvalidCaller);
        var runs = _instances.Values.Where(r => r.PluginId.Equals(caller.PluginId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!runs.Select(r => r.Identity).ToHashSet().SetEquals(observed.Select(r => r.Identity)) || runs.Any(r => r.TaskStatus != AutomationTaskStatus.Running ||
            r.PauseRequested || r.CheckpointMetadata?.Installation != installation || !manifest.Permissions.Contains(Permission.Checkpoint)))
            throw new AutomationGateway.GatewayRejectedException(OperationError.UpdateUnavailable);
        using var grant = Gateway.Authorization.Admit(caller.Authorization, manifest.Permissions.Concat([Permission.Checkpoint]));
        original.ThrowIfCancellationRequested(); execution.ThrowIfCancellationRequested(); grant.Token.ThrowIfCancellationRequested();
        return RuntimeUpdates.Prepare(installation, runs, request.Deadline, original, execution);
    }
}
