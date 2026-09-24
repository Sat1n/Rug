using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

public interface ITaskScheduler : IAsyncDisposable
{
    IReadOnlyCollection<TaskExecutionContext> Instances { get; }
    Task<Guid> StartTaskAsync(string pluginId, nint hwnd, CancellationToken cancellationToken = default);
    bool PauseTask(Guid instanceId);
    bool ResumeTask(Guid instanceId);
    Task StopTaskAsync(Guid instanceId);
}
