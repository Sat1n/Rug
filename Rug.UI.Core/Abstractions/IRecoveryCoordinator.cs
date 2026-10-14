#nullable enable
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
namespace Rug.UI.Core.Abstractions;

/// <summary>Trusted host injection; never construct recovery identities from client data.</summary>
public interface IRecoveryCoordinator
{
    Task<RecoveryResult> RequestAsync(TaskExecutionContext context, string reason, string detail);
    Task<RecoveryResult> VerifyAndContinueAsync(TaskExecutionContext context, Guid recoveryId, CancellationToken cancellationToken = default);
}
/// <summary>Bounded local suggestion only: no input/capture/services exposed.</summary>
public interface IRecoveryExecutor
{
    Task<RecoveryResult> TryRecoverAsync(RecoveryRequest request, CancellationToken cancellationToken);
}
public interface IRecoveryValidator
{
    Task<bool> ValidateAsync(RecoveryValidationContext context, CancellationToken cancellationToken);
}
public interface IRecoveryValidatorRegistry
{
    IRecoveryValidator? Find(PluginInstallation installation, string scenario);
}
