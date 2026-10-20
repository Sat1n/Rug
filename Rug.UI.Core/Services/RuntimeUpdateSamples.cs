#nullable enable
using Rug.UI.Core.Models;
using System.Text;
using System.Text.Json;
using Rug.UI.Core.Abstractions;

namespace Rug.UI.Core.Services;

/// <summary>Trusted controlled-window fixture. Installed modifications never inherit node registration.</summary>
public static class RuntimeUpdateSamples
{
    public static LoadedScriptContent Code()
    {
        using var stream = typeof(RuntimeUpdateSamples).Assembly.GetManifestResourceStream("Rug.UpdateProbe.lua")
            ?? throw new InvalidOperationException("Update sample source is missing.");
        using var reader = new StreamReader(stream);
        return CheckpointJson.Loaded("main.lua", reader.ReadToEnd());
    }
    public static CheckpointNode Node(PluginInstallation installation) => new(installation,"1.0.0",Code().ContentHash,
        "panel-ready",1,Array.AsReadOnly(new[]{"panel-ready"}));
    private static byte[] Resource(string name)
    {
        using var stream = typeof(RuntimeUpdateSamples).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Sample resource missing: " + name);
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }
    public static async Task<PluginSnapshot> SnapshotAsync(string directory, bool target)
    {
        var files = new Dictionary<string, PluginFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, resource) in new[] { ("main.lua",target?"Rug.UpdateTarget.lua":"Rug.UpdateProbe.lua"),
            ("manifest.json",target?"Rug.UpdateTarget.manifest":"Rug.UpdateProbe.manifest"), ("ui.json","Rug.UpdateProbe.ui") })
        {
            var bytes = Resource(resource);
            files.Add(path, new(path, true, Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), CheckpointJson.Hash(bytes), bytes.Length));
        }
        files.Add("config.json", new("config.json", false, null, null, 0));
        return await new PluginUpdatePipeline().ValidateAsync(new(directory, 0, files)).ConfigureAwait(false);
    }
    public static async Task<IDisposable> RegisterSwitchAsync(RuntimeUpdateCompatibilityRegistry registry, string directory)
    {
        var source = await SnapshotAsync(directory, false).ConfigureAwait(false);
        var target = await SnapshotAsync(directory, true).ConfigureAwait(false);
        return registry.Register(new("controlled-panel-direct-v2", PluginInstallation.From(source.Manifest), source.ContentVersion, target.ContentVersion,
            source.Manifest.Version, target.Manifest.Version, source.EntryCode.ContentHash, target.EntryCode.ContentHash,
            "panel-ready", 1, CheckpointJson.ConfigHash(new Dictionary<string,object?>()), Array.AsReadOnly(new[]{"panel-ready"})),
            new CheckpointSamples.GreenRestoreValidator());
    }
    /// <summary>Diagnostic fixture helper, writes only the two shipped target files into its owned installation.</summary>
    public static async Task WriteTargetAsync(string directory)
    {
        await File.WriteAllBytesAsync(Path.Combine(directory,"main.lua"),Resource("Rug.UpdateTarget.lua")).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(directory,"manifest.json"),Resource("Rug.UpdateTarget.manifest")).ConfigureAwait(false);
    }
}
