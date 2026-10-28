#nullable enable
using System.Collections.ObjectModel;
using Rug.UI.Core.Services;

namespace Rug.UI.Core.Models;

public sealed record PluginFileSnapshot(string Path, bool Exists, string? Text, string? ContentHash, int Bytes);
/// <summary>Immutable bounded disk candidate; absent optional files have explicit entries.</summary>
public sealed class PluginCandidate
{
    internal PluginCandidate(string directory, long revision, IDictionary<string, PluginFileSnapshot> files)
    { Directory = directory; Revision = revision; Files = new ReadOnlyDictionary<string, PluginFileSnapshot>(new Dictionary<string, PluginFileSnapshot>(files, StringComparer.OrdinalIgnoreCase)); }
    public string Directory { get; }
    public long Revision { get; }
    public IReadOnlyDictionary<string, PluginFileSnapshot> Files { get; }
}
/// <summary>Only the trusted pipeline can produce a validated snapshot; mutable manifest values are copied.</summary>
public sealed class PluginSnapshot
{
    private readonly PluginManifest _manifest;
    internal PluginSnapshot(PluginCandidate candidate, PluginManifest manifest, string contentVersion, string runtimeVersion)
    { Candidate = candidate; _manifest = manifest; ContentVersion = contentVersion; RuntimeVersion = runtimeVersion; }
    public PluginCandidate Candidate { get; }
    public string PluginId => _manifest.Id;
    public string Directory => Candidate.Directory;
    public long Revision => Candidate.Revision;
    public string ContentVersion { get; }
    public string RuntimeVersion { get; }
    public string? UiJson => Candidate.Files["ui.json"].Text;
    public LoadedScriptContent EntryCode => CheckpointJson.Loaded(_manifest.Entry, Candidate.Files[_manifest.Entry.Replace('\\', '/')].Text!);
    public PluginManifest Manifest => _manifest with { Permissions = _manifest.Permissions.ToList(),
        Configuration = _manifest.Configuration?.ToDictionary(p => p.Key, p => StateStore.CloneValue(p.Value)) };
}
public enum PluginUpdateStatus { Unchanged, UiApplied, RuntimeApplied, RuntimePending, Superseded, ValidationFailed }
