#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

/// <summary>Composition-root registration only. No plugin/request can approve compatibility.</summary>
public interface IRuntimeUpdateCompatibilityRegistry
{
    (RuntimeUpdateCompatibility Rule, ICheckpointRestoreValidator Validator)? Find(
        PluginInstallation installation, string sourceSnapshot, string targetSnapshot, string nodeId);
}
