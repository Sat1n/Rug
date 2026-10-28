#nullable enable
using System.Text.Json;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Read-only host-root incident access. No paths from JSON are opened.
/// @shape pages: <=100 detached summaries, scan <=4096 entries; detail: <=1 MiB.
/// @source archives: Services/AnomalyLogger.cs#class:AnomalyLogger</summary>
public sealed class IncidentStore
{
    public const int MaxRecordBytes=1024*1024,MaxScan=4096;
    internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
    private readonly string _root;
    public IncidentStore(string root)=>_root=Path.GetFullPath(root);
    internal string Resolve(string key)
    {
        if(key.Length is <1 or >240||key!=Path.GetFileName(key)||key.IndexOfAny(['/', '\\', ':'])>=0||!key.EndsWith(".json",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid incident file key.");
        string path=Path.Combine(_root,key);
        if(File.Exists(path)&&File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))throw new ArgumentException("Incident reparse point rejected.");
        return path;
    }
    internal static byte[] ReadBounded(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(file.Length>MaxRecordBytes)throw new IncidentCapacityException();
        using var result=new MemoryStream();var buffer=new byte[4096];int n;
        while((n=file.Read(buffer))!=0){if(result.Length+n>MaxRecordBytes)throw new IncidentCapacityException();result.Write(buffer,0,n);}
        return result.ToArray();
    }
    internal sealed class IncidentCapacityException : Exception;
    internal static IncidentReadStatus Version(JsonElement root)
    {
        if(root.ValueKind!=JsonValueKind.Object)return IncidentReadStatus.Corrupt;
        if(!root.TryGetProperty("schemaVersion",out var version))return IncidentReadStatus.Legacy;
        if(version.ValueKind!=JsonValueKind.Number||!version.TryGetInt32(out int value))return IncidentReadStatus.Corrupt;
        return value==1?IncidentReadStatus.Valid:IncidentReadStatus.UnsupportedFormat;
    }
    public IncidentDetail Read(string fileKey)
    {
        try
        {
            string path=Resolve(fileKey);byte[] bytes=ReadBounded(path);
            using var doc=JsonDocument.Parse(bytes);var version=Version(doc.RootElement);
            if(version is not (IncidentReadStatus.Valid or IncidentReadStatus.Legacy))return new(fileKey,version,null);
            var record=doc.RootElement.Deserialize<AnomalyLogModel>(Json)??throw new JsonException();
            if(record.PluginId is null||record.Reason is null||record.ScriptContext is null||record.ScreenshotPath is null)
                return new(fileKey,IncidentReadStatus.Corrupt,null,"Null required field");
            if(version==IncidentReadStatus.Valid&&(record.IncidentId is null||record.IncidentId==Guid.Empty))return new(fileKey,IncidentReadStatus.Corrupt,null,"Missing incident identity");
            if(record.TraceLink is {Run:{} run} link&&(run.PluginId!=record.PluginId||run.InstanceId!=record.InstanceId||
                run.RunGeneration!=record.RunGeneration||run.TargetBindingId!=record.TargetBindingId||link.SequenceUpperBound<0))
                return new(fileKey,IncidentReadStatus.Corrupt,null,"Trace/run identity mismatch");
            string? image=record.ScreenshotPath;
            // Display only the safe basename. No thumbnail/image/file navigation is introduced.
            if(image is null||image!=Path.GetFileName(image)||image.IndexOfAny(['/', '\\', ':'])>=0||!image.EndsWith(".png",StringComparison.OrdinalIgnoreCase))image=null;
            return new(fileKey,version,record,image is null?"Screenshot reference rejected":null,image);
        }
        catch(FileNotFoundException){return new(fileKey,IncidentReadStatus.NotFound,null);}
        catch(DirectoryNotFoundException){return new(fileKey,IncidentReadStatus.NotFound,null);}
        catch(IncidentCapacityException){return new(fileKey,IncidentReadStatus.CapacityExceeded,null);}
        catch(Exception error) when(error is JsonException or InvalidOperationException or FormatException or ArgumentException or NotSupportedException)
        {return new(fileKey,IncidentReadStatus.Corrupt,null,error.GetType().Name);}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException)
        {return new(fileKey,IncidentReadStatus.ReadFailed,null,error.GetType().Name);}
    }
    public IncidentPage List(int offset=0,int limit=50)
    {
        if(offset is <0 or >MaxScan||limit is <1 or >100)throw new ArgumentOutOfRangeException(nameof(limit));
        if(!Directory.Exists(_root))return new([],null,false);
        var names=new DirectoryInfo(_root).EnumerateFiles("*.json").Take(MaxScan+1).Select(f=>f.Name).ToArray();
        bool limited=names.Length>MaxScan;
        var sorted=names.Take(MaxScan).OrderByDescending(n=>n,StringComparer.Ordinal).ToArray();
        var entries=sorted.Skip(offset).Take(limit).Select(key=>
        {
            var detail=Read(key);var r=detail.Record;
            return new IncidentEntry(key,detail.Status,r?.IncidentId,ExecutionTraceRecorder.Text(r?.PluginId,128),r?.InstanceId,
                r?.Timestamp,ExecutionTraceRecorder.Text(r?.Reason),r?.TraceLink?.TraceId is not null);
        }).ToArray();
        return new(Array.AsReadOnly(entries),sorted.Length>offset+limit?offset+limit:null,limited);
    }
}
