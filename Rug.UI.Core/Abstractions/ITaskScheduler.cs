#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

public interface ITaskScheduler : IAsyncDisposable
{
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
