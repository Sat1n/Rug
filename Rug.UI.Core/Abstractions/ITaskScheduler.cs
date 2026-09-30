#nullable enable
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

namespace Rug.UI.Core.Abstractions;

public interface ITaskScheduler : IAsyncDisposable
{
    AutomationGateway Gateway => throw new NotSupportedException("No automation gateway is bound.");
    /// <summary>Raised after a run enters or leaves the active instance collection.</summary>
    event EventHandler? InstancesChanged;
    IReadOnlyCollection<TaskExecutionContext> Instances { get; }
    Task<Guid> StartTaskAsync(string pluginId, nint hwnd, CancellationToken cancellationToken = default);
    Task<Guid> StartTaskAsync(string pluginId, nint hwnd,
        IReadOnlyDictionary<string, object?> launchInputs, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Launch inputs are not supported by this scheduler.");
    Task<Guid> StartTaskAsync(string pluginId, nint hwnd,
        IReadOnlyDictionary<string, object?> launchInputs, Action<RunIdentity> onReady,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Pre-run identity callback is not supported by this scheduler.");
    Task<IAsyncDisposable> AcquirePluginReloadAsync(string pluginId,
        CancellationToken cancellationToken = default);
    Task RefreshPluginsAsync(CancellationToken cancellationToken = default);
    bool PauseTask(Guid instanceId);
    bool ResumeTask(Guid instanceId);
    Task StopTaskAsync(Guid instanceId);
}
