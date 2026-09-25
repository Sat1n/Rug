#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

/// <summary>Watches plugin files and serializes safe task/UI reloads.</summary>
public interface IHotReloadService : IAsyncDisposable
{
    event EventHandler<HotReloadEventArgs>? Reloaded;
    event EventHandler<HotReloadFailedEventArgs>? ReloadFailed;
    void Start();
    IDisposable RegisterUiHandler(string pluginId,
        Func<string, CancellationToken, Task> reloadUiAsync);
}
