#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

public interface IAutomationGateway
{
    Task<OperationResult> ExecuteAsync(AutomationCallContext caller, OperationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Additional host policy over capability ceilings; live grants come from IPluginAuthorization.</summary>
public interface IAutomationPermissionChecker
{
    bool IsAllowed(AutomationCallContext caller, IReadOnlyList<string> runPermissions,
        AutomationOperation operation);
}
