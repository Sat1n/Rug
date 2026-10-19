#nullable enable
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

namespace Rug.UI.Core.Abstractions;

public interface ITaskScheduler : IAsyncDisposable
{
    Task<IReadOnlyList<PluginSnapshot>> GetPluginSnapshotsAsync(CancellationToken token = default) => throw new NotSupportedException();
    Task<PluginUpdateStatus> ClassifyPluginUpdateAsync(PluginSnapshot snapshot, CancellationToken token = default) => throw new NotSupportedException();
    Task PublishPluginSnapshotAsync(PluginSnapshot snapshot, CancellationToken token = default) => throw new NotSupportedException();
    Task<IReadOnlyList<CheckpointChoice>> QueryCheckpointsAsync(string pluginId, CancellationToken token = default) =>
        throw new NotSupportedException("Checkpoint discovery is unavailable.");
    Task<AutomationCallContext> CreateCheckpointRestoreContextAsync(string pluginId, InputTarget target,
        Action<RunIdentity>? onReady = null, CancellationToken token = default) => throw new NotSupportedException("Checkpoint restore is unavailable.");
    AutomationGateway Gateway => throw new NotSupportedException("No automation gateway is bound.");
    /// <summary>Raised after a run enters or leaves the active instance collection.</summary>
    event EventHandler? InstancesChanged;
    event EventHandler<LifecycleChangedEventArgs>? LifecycleChanged { add { } remove { } }
    LifecycleSnapshot? GetLifecycle(Guid instanceId) =>
        Instances.FirstOrDefault(run => run.InstanceId == instanceId)?.Snapshot;
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
    /// <summary>Accept a request; Paused is confirmed asynchronously at a safe boundary.</summary>
    bool PauseTask(Guid instanceId);
    bool ResumeTask(Guid instanceId);
    Task StopTaskAsync(Guid instanceId);
}
