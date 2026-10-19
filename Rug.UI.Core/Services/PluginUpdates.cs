#nullable enable
using Microsoft.Extensions.Logging;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

public sealed partial class TaskScheduler
{
    public const int MaxPluginSnapshots = 64;
    private Dictionary<string, PluginSnapshot>? _snapshots;
    /// <summary>@shape return: <=64 validated effective installations, detached manifest/config getters.
    /// @source candidate: Services/PluginUpdatePipeline.cs#class:PluginUpdatePipeline</summary>
    public async Task<IReadOnlyList<PluginSnapshot>> GetPluginSnapshotsAsync(CancellationToken token = default)
    {
        await _catalogGate.WaitAsync(token).ConfigureAwait(false);
        PluginSnapshot[] result;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_snapshots is null)
            {
                var snapshots = new Dictionary<string, PluginSnapshot>(StringComparer.OrdinalIgnoreCase);
                var pipeline = new PluginUpdatePipeline();
                if (Directory.Exists(_pluginsRoot)) foreach (var directory in Directory.EnumerateDirectories(_pluginsRoot).Take(MaxPluginSnapshots).Order(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var snapshot = await pipeline.ValidateAsync(await pipeline.PrepareAsync(directory, 0, token).ConfigureAwait(false), token).ConfigureAwait(false);
                        if (!snapshots.TryAdd(snapshot.PluginId, snapshot)) throw new InvalidDataException("Duplicate plugin installation identity.");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) { _logger.LogWarning(error, "Invalid initial snapshot at {Directory}", directory); }
                }
                _snapshots = snapshots;
                _catalog = snapshots.ToDictionary(p => p.Key, p => p.Value.Manifest, StringComparer.OrdinalIgnoreCase);
            }
            result = _snapshots.Values.ToArray();
        }
        finally { _catalogGate.Release(); }
        return Array.AsReadOnly(result);
    }
    public async Task<PluginUpdateStatus> ClassifyPluginUpdateAsync(PluginSnapshot snapshot, CancellationToken token = default)
    {
        var snapshots = await GetPluginSnapshotsAsync(token).ConfigureAwait(false);
        var old = snapshots.FirstOrDefault(p => string.Equals(p.PluginId, snapshot.PluginId, StringComparison.OrdinalIgnoreCase));
        if (snapshots.Any(p => string.Equals(p.Directory, snapshot.Directory, StringComparison.OrdinalIgnoreCase) && p.PluginId != snapshot.PluginId) ||
            old is not null && !string.Equals(old.Directory, snapshot.Directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update cannot replace plugin or installation identity.");
        if (old?.ContentVersion == snapshot.ContentVersion) return PluginUpdateStatus.Unchanged;
        bool runtime = old?.RuntimeVersion != snapshot.RuntimeVersion;
        if (runtime && _instances.Values.Any(c => c.PluginId.Equals(snapshot.PluginId, StringComparison.OrdinalIgnoreCase) &&
            c.TaskStatus is not (AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted))) return PluginUpdateStatus.RuntimePending;
        return runtime ? PluginUpdateStatus.RuntimeApplied : PluginUpdateStatus.UiApplied;
    }
    /// <summary>Caller owns the per-plugin update/start lease and has staged all UI trees.</summary>
    public async Task PublishPluginSnapshotAsync(PluginSnapshot snapshot, CancellationToken token = default)
    {
        var status = await ClassifyPluginUpdateAsync(snapshot, token).ConfigureAwait(false);
        if (status == PluginUpdateStatus.RuntimePending) throw new InvalidOperationException("Active runtime update is pending.");
        var manifest = snapshot.Manifest;
        await _catalogGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_snapshots!.ContainsKey(snapshot.PluginId) && _snapshots.Count >= MaxPluginSnapshots) throw new InvalidDataException("Effective snapshot capacity exceeded.");
            var nextSnapshots = new Dictionary<string, PluginSnapshot>(_snapshots, StringComparer.OrdinalIgnoreCase) { [snapshot.PluginId] = snapshot };
            var nextCatalog = new Dictionary<string, PluginManifest>(_catalog!, StringComparer.OrdinalIgnoreCase) { [snapshot.PluginId] = manifest };
            _snapshots = nextSnapshots; _catalog = nextCatalog;
        }
        finally { _catalogGate.Release(); }
        Gateway.Authorization.Register(manifest); // Never grants. Outside catalog/lifecycle locks.
    }
}
