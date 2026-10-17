#nullable enable
using System.Text.Json;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

public sealed class AtomicCheckpointFilePublisher : ICheckpointFilePublisher
{
    public Task PublishAsync(string temporaryPath, string destinationPath, bool replace) => Task.Run(() =>
    {
        if (replace) File.Replace(temporaryPath, destinationPath, null); else File.Move(temporaryPath, destinationPath);
    });
}

/// <summary>Bounded, exclusive-writer, same-directory integrity-checked publication; detached read-only records.</summary>
public sealed class FileCheckpointStore : ICheckpointStore
{
    private readonly string _root;
    private readonly CheckpointStoreLimits _limits;
    private readonly ICheckpointFilePublisher _publisher;
    private readonly SemaphoreSlim _writer = new(1);
    private readonly Func<CancellationToken, Task>? _beforePublish;
    private int _waiting, _disposed;
    public FileCheckpointStore(string? root = null, CheckpointStoreLimits? limits = null, ICheckpointFilePublisher? publisher = null)
        : this(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rug", "checkpoints"), limits, publisher, null) { }
    internal FileCheckpointStore(string root, CheckpointStoreLimits? limits, ICheckpointFilePublisher? publisher, Func<CancellationToken, Task>? beforePublish)
    {
        _root = Path.GetFullPath(root); _limits = limits ?? new(); _publisher = publisher ?? new AtomicCheckpointFilePublisher(); _beforePublish = beforePublish;
        if (_limits.MaxRecords is <= 0 or > 4096 || _limits.MaxBytes is <= 0 or > 256 * 1024 * 1024 || _limits.MaxTemps is <= 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limits));
        Directory.CreateDirectory(_root); CheckRoot();
    }
    private void CheckRoot()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        for (DirectoryInfo? directory = new(_root); directory is not null; directory = directory.Parent)
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Checkpoint root crosses a link.");
    }
    internal string Locate(CheckpointAddress address)
    {
        if (address.SourceInstanceId == Guid.Empty || address.SourceRunGeneration <= 0 || !CheckpointJson.ValidId(address.PluginId) ||
            address.InstallationId.Length != 64 || address.InstallationId.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Invalid checkpoint source.");
        return Path.Combine(_root, CheckpointJson.Hash(JsonSerializer.SerializeToUtf8Bytes(address, CheckpointJson.Options)) + ".json");
    }
    public async Task<CheckpointResult> PublishAsync(CheckpointRecord record, Func<bool> beginPublish, CancellationToken token)
    {
        CheckRoot(); ArgumentNullException.ThrowIfNull(beginPublish);
        if (record.SchemaVersion != CheckpointJson.SchemaVersion) return new("rejected", CheckpointError.InvalidParameters);
        if (Interlocked.Increment(ref _waiting) > 32) { Interlocked.Decrement(ref _waiting); return new("rejected", CheckpointError.CapacityExceeded); }
        try { await _writer.WaitAsync(token).ConfigureAwait(false); }
        catch { Interlocked.Decrement(ref _waiting); throw; }
        string? temporary = null;
        try
        {
            CheckRoot(); token.ThrowIfCancellationRequested();
            string destination = Locate(new(record.InstallationId, record.PluginId, record.SourceInstanceId, record.SourceRunGeneration));
            string ownerPath = Path.Combine(_root, ".writer.lock");
            if (File.Exists(ownerPath) && File.GetAttributes(ownerPath).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked writer lock.");
            await using var owner = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var files = new DirectoryInfo(_root).EnumerateFiles().Take(_limits.MaxRecords + _limits.MaxTemps + 2).ToArray();
            if (files.Length >= _limits.MaxRecords + _limits.MaxTemps + 2) return new("rejected", CheckpointError.CapacityExceeded);
            if (files.Any(f => f.Attributes.HasFlag(FileAttributes.ReparsePoint))) throw new IOException("Linked checkpoint file.");
            bool replace = File.Exists(destination);
            if (replace)
            {
                var previous = await ReadRawAsync(destination, token).ConfigureAwait(false);
                if (previous.Status != CheckpointReadStatus.Valid) throw new IOException("Existing record is not valid; repair requires host action.");
                if (previous.Record!.SubmissionSequence >= record.SubmissionSequence) return new("rejected", CheckpointError.StaleSubmission);
            }
            var element = JsonSerializer.SerializeToElement(record, CheckpointJson.Options);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { checksum = CheckpointJson.Hash(CheckpointJson.Canonical(element)), record }, CheckpointJson.Options);
            if (bytes.Length > CheckpointJson.RecordBytes) return new("rejected", CheckpointError.CapacityExceeded);
            if (files.Count(f => f.Extension == ".json") + (replace ? 0 : 1) > _limits.MaxRecords ||
                files.Count(f => f.Extension == ".tmp") >= _limits.MaxTemps ||
                files.Where(f => f.Extension is ".json" or ".tmp").Sum(f => f.Length) + bytes.Length > _limits.MaxBytes)
                return new("rejected", CheckpointError.CapacityExceeded);
            temporary = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, token).ConfigureAwait(false); await file.FlushAsync(token).ConfigureAwait(false); file.Flush(flushToDisk: true);
            }
            var checkedRecord = await ReadRawAsync(temporary, token).ConfigureAwait(false);
            if (checkedRecord.Status != CheckpointReadStatus.Valid || checkedRecord.Record!.CheckpointId != record.CheckpointId)
                throw new IOException("Temporary checkpoint failed integrity validation.");
            if (_beforePublish is not null) await _beforePublish(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!beginPublish()) throw new OperationCanceledException("Checkpoint publication was not admitted.");
            // Publication has won: actual IO must finish, even if Stop/Gateway no longer waits.
            await _publisher.PublishAsync(temporary, destination, replace).ConfigureAwait(false);
            temporary = null;
            return new("committed", CheckpointError.None, record.CheckpointId, record.SubmissionSequence);
        }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); }
            finally { _writer.Release(); Interlocked.Decrement(ref _waiting); }
        }
    }
    public async Task<CheckpointReadResult> ReadAsync(CheckpointAddress address, CheckpointCompatibility compatibility, CancellationToken token = default)
    {
        CheckRoot(); var read = await ReadRawAsync(Locate(address), token).ConfigureAwait(false);
        if (read.Record is not { } record) return read;
        if (record.SourceInstanceId != address.SourceInstanceId || record.SourceRunGeneration != address.SourceRunGeneration) return new(CheckpointReadStatus.Corrupt);
        var status = record.InstallationId != address.InstallationId || record.InstallationId != compatibility.InstallationId ? CheckpointReadStatus.InstallationMismatch :
            record.PluginId != address.PluginId || record.PluginId != compatibility.PluginId ? CheckpointReadStatus.PluginMismatch :
            record.PluginVersion != compatibility.PluginVersion || record.ContentHash != compatibility.ContentHash ? CheckpointReadStatus.ContentMismatch :
            record.NodeId != compatibility.NodeId || record.NodeStateVersion != compatibility.NodeStateVersion ? CheckpointReadStatus.NodeVersionMismatch :
            record.ConfigHash != compatibility.ConfigHash ? CheckpointReadStatus.ConfigIncompatible : CheckpointReadStatus.Valid;
        return new(status, status == CheckpointReadStatus.Valid ? record : null);
    }
    /// <summary>@shape return: bounded detached candidates attributed to one verified installation; paths never escape the store.
    /// @source installation: Models/AuthorizationModels.cs#class:PluginInstallation</summary>
    public async Task<IReadOnlyList<CheckpointCandidate>> QueryAsync(PluginInstallation installation, CancellationToken token = default)
    {
        CheckRoot(); string installationId = CheckpointJson.InstallationId(installation);
        var result = new List<CheckpointCandidate>();
        foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*.json").Take(_limits.MaxRecords + 1))
        {
            token.ThrowIfCancellationRequested();
            if (result.Count >= _limits.MaxRecords) break;
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked checkpoint candidate.");
            byte[] bytes;
            await using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous))
            {
                bytes = new byte[(int)Math.Min(stream.Length, CheckpointJson.RecordBytes)];
                await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            }
            if (BelongsTo(bytes, installationId, installation.PluginId))
            {
                var read = await ReadRawAsync(file.FullName, token).ConfigureAwait(false);
                if (read.Record is { } record)
                {
                    if (record.InstallationId != installationId || !string.Equals(record.PluginId, installation.PluginId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Locate(new(record.InstallationId, record.PluginId, record.SourceInstanceId, record.SourceRunGeneration)) != file.FullName) read = new(CheckpointReadStatus.Corrupt);
                }
                result.Add(new(read.Status, read.Record));
            }
        }
        return result.AsReadOnly();
    }
    // Bounded prefix attribution is only a display hint; restored records still need full integrity/address checks.
    private static bool BelongsTo(byte[] bytes, string installationId, string pluginId)
    {
        bool installation = false, plugin = false, inRecord = false;
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: false, new JsonReaderState(new JsonReaderOptions { MaxDepth = 24 }));
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("record")) inRecord = true;
                if (!inRecord || reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 2) continue;
                bool isInstallation = reader.ValueTextEquals("installationId"), isPlugin = reader.ValueTextEquals("pluginId");
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) continue;
                if (isInstallation) installation = reader.GetString() == installationId;
                if (isPlugin) plugin = string.Equals(reader.GetString(), pluginId, StringComparison.OrdinalIgnoreCase);
                if (installation && plugin) return true;
            }
        }
        catch (JsonException) { }
        return false;
    }
    private static async Task<CheckpointReadResult> ReadRawAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return new(CheckpointReadStatus.Absent);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked checkpoint record.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (file.Length > CheckpointJson.RecordBytes) return new(CheckpointReadStatus.Oversized);
        byte[] bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            CheckpointJson.ValidateEnvelope(doc.RootElement);
            var payload = doc.RootElement.GetProperty("record");
            if (!payload.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int schemaVersion)) return new(CheckpointReadStatus.Corrupt);
            // V1's rounded number checksums are unsafe to verify under either algorithm.
            // Reject legacy/unknown formats explicitly; never silently rehash or migrate.
            if (schemaVersion != CheckpointJson.SchemaVersion) return new(CheckpointReadStatus.UnsupportedFormat);
            if (doc.RootElement.GetProperty("checksum").GetString() != CheckpointJson.Hash(CheckpointJson.Canonical(payload))) return new(CheckpointReadStatus.Corrupt);
            foreach (string name in new[] { "checkpointId", "submissionSequence", "timestampUtc", "installationId", "pluginId", "pluginVersion", "contentHash",
                "sourceInstanceId", "sourceRunGeneration", "nodeId", "nodeStateVersion", "configRevision", "configHash", "state", "completedActionIds" })
                if (!payload.TryGetProperty(name, out _)) return new(CheckpointReadStatus.Corrupt);
            var record = payload.Deserialize<CheckpointRecord>(CheckpointJson.Options)!;
            if (record.CheckpointId == Guid.Empty || record.SubmissionSequence <= 0 || record.SourceRunGeneration <= 0 || record.SourceInstanceId == Guid.Empty ||
                record.TimestampUtc.Kind != DateTimeKind.Utc || record.ConfigRevision < 0 || record.NodeStateVersion <= 0 || !CheckpointJson.ValidId(record.NodeId) ||
                string.IsNullOrWhiteSpace(record.PluginVersion) || !CheckpointJson.ValidId(record.PluginId) ||
                new[] { record.ContentHash, record.ConfigHash, record.InstallationId }.Any(h => h is null || h.Length != 64 || h.Any(c => !char.IsAsciiHexDigit(c)))) return new(CheckpointReadStatus.Corrupt);
            var state = CheckpointJson.Parse(record.State.GetRawText());
            var actions = CheckpointJson.Actions(JsonSerializer.Serialize(record.CompletedActionIds));
            return new(CheckpointReadStatus.Valid, record with { State = state, CompletedActionIds = actions });
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or NullReferenceException)
        {
            return new(CheckpointReadStatus.Corrupt);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _writer.WaitAsync().ConfigureAwait(false); try { Interlocked.Exchange(ref _disposed, 1); } finally { _writer.Release(); }
    }
}
