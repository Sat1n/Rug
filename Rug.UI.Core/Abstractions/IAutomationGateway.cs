#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

public interface IAutomationGateway
{
    Task<OperationResult> ExecuteAsync(AutomationCallContext caller, OperationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Replaceable policy; grants come only from host-created contexts and the registered run.</summary>
public interface IAutomationPermissionChecker
{
    bool IsAllowed(AutomationCallContext caller, IReadOnlyList<string> runPermissions,
        AutomationOperation operation);
}
