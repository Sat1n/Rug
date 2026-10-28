#nullable enable
using System.Text;
using System.Text.Json;
using NLua;
using Rug.UI.Core.Models;
using Rug.UI.Core.Schema;

namespace Rug.UI.Core.Services;

/// <summary>Prepare reads each bounded file once; Validate compiles, never runs plugin code.</summary>
public sealed class PluginUpdatePipeline
{
    public const int MaxFileBytes = 1024 * 1024, MaxTotalBytes = 4 * 1024 * 1024, MaxFiles = 128, MaxDirectories = 128;
    internal Func<PluginCandidate, CancellationToken, Task>? BeforeValidation { get; set; }
    internal Func<PluginSnapshot, CancellationToken, Task>? AfterValidation { get; set; }
    /// <summary>@shape return: <=128 detached text files, <=4MiB total, optional absence recorded.
    /// @source directory: host-verified immediate plugin installation</summary>
    public async Task<PluginCandidate> PrepareAsync(string directory, long revision, CancellationToken token = default)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Plugin directory is a link.");
        var files = new Dictionary<string, PluginFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        int total = 0, directories = 0, entries = 0;
        async Task Read(string path, bool required)
        {
            token.ThrowIfCancellationRequested(); string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (files.ContainsKey(relative)) return;
            if (files.Count >= MaxFiles) throw new InvalidDataException("Candidate file capacity exceeded.");
            try
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Candidate file is a link.");
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
                if (stream.Length > MaxFileBytes || stream.Length > MaxTotalBytes - total) throw new InvalidDataException("Candidate size exceeded.");
                byte[] bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                if (stream.ReadByte() != -1) throw new IOException("Candidate file grew during capture.");
                total += bytes.Length;
                using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
                files.Add(relative, new(relative, true, reader.ReadToEnd(), CheckpointJson.Hash(bytes), bytes.Length));
            }
            catch (FileNotFoundException) when (!required) { files.Add(relative, new(relative, false, null, null, 0)); }
        }
        async Task Walk(string path, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (++directories > MaxDirectories || depth > 8 || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Candidate directory limit/link.");
            foreach (var file in System.IO.Directory.EnumerateFiles(path).Take(4097).Order(StringComparer.Ordinal))
            {
                if (++entries > 4096) throw new InvalidDataException("Candidate directory entry limit exceeded.");
                if (Path.GetExtension(file).Equals(".lua", StringComparison.OrdinalIgnoreCase)) await Read(file, true).ConfigureAwait(false);
            }
            foreach (var child in System.IO.Directory.EnumerateDirectories(path).Take(MaxDirectories + 1).Order(StringComparer.Ordinal)) await Walk(child, depth + 1).ConfigureAwait(false);
        }
        await Read(Path.Combine(directory, "manifest.json"), true).ConfigureAwait(false);
        var declaration = JsonSerializer.Deserialize<PluginManifest>(files["manifest.json"].Text!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Null manifest.");
        string entry = declaration.Entry;
        if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) || entry.Split('/', '\\').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("Entry must remain inside the candidate.");
        await Walk(directory, 0).ConfigureAwait(false);
        await Read(Path.Combine(directory, entry), true).ConfigureAwait(false);
        await Read(Path.Combine(directory, "config.json"), false).ConfigureAwait(false);
        await Read(Path.Combine(directory, "ui.json"), false).ConfigureAwait(false);
        return new(directory, revision, files);
    }
    public async Task<PluginSnapshot> ValidateAsync(PluginCandidate candidate, CancellationToken token = default)
    {
        if (BeforeValidation is not null) await BeforeValidation(candidate, token).ConfigureAwait(false);
        var manifest = PluginManager.ParseManifest(candidate.Directory, candidate.Files["manifest.json"].Text!,
            candidate.Files.Where(p => p.Value.Exists).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var config = PluginManager.ParseConfig(manifest.Id, candidate.Files["config.json"].Text);
        foreach (var value in config.Defaults.Values) _ = StateStore.CloneValue(value);
        manifest = manifest with { Configuration = config.Defaults };
        if (candidate.Files["ui.json"].Text is { } json) new SchemaParser().Parse(json);
        await Task.Run(() =>
        {
            using var lua = new Lua(); lua.State.Encoding = Encoding.UTF8;
            foreach (var file in candidate.Files.Values.Where(f => f.Exists && (f.Path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) || f.Path.Equals(manifest.Entry.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))))
            { token.ThrowIfCancellationRequested(); using var compiled = lua.LoadString(file.Text!, "@" + file.Path); }
        }, token).ConfigureAwait(false);
        string Hash(bool runtime) => CheckpointJson.Hash(JsonSerializer.SerializeToUtf8Bytes(candidate.Files.Values
            .Where(f => !runtime || !f.Path.Equals("ui.json", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => new { f.Path, f.Exists, f.ContentHash })));
        var snapshot = new PluginSnapshot(candidate, manifest, Hash(false), Hash(true));
        if (AfterValidation is not null) await AfterValidation(snapshot, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); return snapshot;
    }
}
