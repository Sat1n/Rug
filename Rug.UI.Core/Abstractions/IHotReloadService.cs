#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

/// <summary>Watches candidates and serializes validated publication without restarting tasks.</summary>
public interface IHotReloadService : IAsyncDisposable
{
    Task Ready { get; }
    event EventHandler<HotReloadEventArgs>? Reloaded;
    event EventHandler<HotReloadFailedEventArgs>? ReloadFailed;
    void Start();
    IDisposable RegisterUiHandler(string pluginId,
        Func<PluginSnapshot, CancellationToken, Task<IPluginUiUpdate>> prepareUiAsync);
}

public interface IPluginUiUpdate : IAsyncDisposable
{
    Task CommitAsync(CancellationToken token);
    Task RollbackAsync();
}
