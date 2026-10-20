#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

/// <summary>Trusted host control only; plugins cannot create plans or approve candidate publication.</summary>
public interface IRuntimeUpdateCoordinator : IAsyncDisposable
{
    event EventHandler<RuntimeUpdateChangedEventArgs>? Changed;
    RuntimeUpdateSnapshot? Query(PluginInstallation installation);
}
