#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Trusted writer-only fault injection seam. Never called on an automation producer.</summary>
public interface ITraceFileSink
{
    Task AppendAsync(string path,ReadOnlyMemory<byte> line);
    Task ReplaceSummaryAsync(string path,ReadOnlyMemory<byte> bytes);
}
public sealed class TraceFileSink : ITraceFileSink
{
    public async Task AppendAsync(string path,ReadOnlyMemory<byte> line)
    {
        await using var file=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read,4096,true);
        await file.WriteAsync(line).ConfigureAwait(false);await file.FlushAsync().ConfigureAwait(false);
    }
    public async Task ReplaceSummaryAsync(string path,ReadOnlyMemory<byte> bytes)
    {
        string temp=path+".tmp";
        await using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,4096,true))
        {await file.WriteAsync(bytes).ConfigureAwait(false);await file.FlushAsync().ConfigureAwait(false);}
        if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
    }
}

/// <summary>Host-root-only append storage. Reader is independent and can reopen after process exit.
/// @shape files: rugtrace-{session:N}-{trace:N}.events / .summary / .summary.tmp, bounded JSON envelopes.
/// @source events: Models/ExecutionTrace.cs#class:ExecutionTraceEvent</summary>
public sealed class TraceFileStore
{
    private readonly string _root;
    private readonly TraceLimits _limits;
    private readonly ITraceFileSink _sink;
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    public TraceFileStore(string root,TraceLimits? limits=null,ITraceFileSink? sink=null)
    {_root=Path.GetFullPath(root);_limits=limits??new();_sink=sink??new TraceFileSink();}
    private string Stem(Guid session,Guid trace)=>Path.Combine(_root,$"rugtrace-{session:N}-{trace:N}");
    private static bool Owned(string path)
    {
        string name=Path.GetFileName(path);
        return name.Length>=74&&name.StartsWith("rugtrace-",StringComparison.Ordinal)&&name[41]=='-'&&
            Guid.TryParseExact(name.Substring(9,32),"N",out _)&&Guid.TryParseExact(name.Substring(42,32),"N",out _)&&
            name[74..] is ".events" or ".summary" or ".summary.tmp" or ".closed";
    }
    private FileInfo[] Files()=>Directory.Exists(_root)?new DirectoryInfo(_root).EnumerateFiles("rugtrace-*")
        .Where(f=>Owned(f.Name)&&!f.Attributes.HasFlag(FileAttributes.ReparsePoint)).Take(_limits.MaxFiles+1).ToArray():[];
    private static byte[] Envelope<T>(T value)
    {
        var payload=JsonSerializer.SerializeToElement(value,Json);
        string checksum=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload,Json)));
        return JsonSerializer.SerializeToUtf8Bytes(new{checksum,payload},Json);
    }
    private static T Decode<T>(ReadOnlyMemory<byte> bytes)
    {
        using var doc=JsonDocument.Parse(bytes);var root=doc.RootElement;
        var payload=root.GetProperty("payload");string checksum=root.GetProperty("checksum").GetString()!;
        if(checksum!=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload,Json))))throw new InvalidDataException("Checksum");
        return payload.Deserialize<T>(Json)??throw new InvalidDataException("Null payload");
    }
    private static byte[]? ReadOptional(string path,long maximum)
    {
        try
        {
            // Readers can inspect a live append prefix; access/real IO failures are
            // distinct from absence. Bound reads even if a file grows after Length.
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(stream.Length>maximum)throw new TraceReadCapacityException();
            using var result=new MemoryStream();var buffer=new byte[4096];int read;
            while((read=stream.Read(buffer))>0)
            {if(result.Length+read>maximum)throw new TraceReadCapacityException();result.Write(buffer,0,read);}
            return result.ToArray();
        }
        catch(FileNotFoundException){return null;}
        catch(DirectoryNotFoundException){return null;}
    }
    private sealed class TraceReadCapacityException : Exception;
    // Only worker IO uses this lock file. Cross-host quota scans/publication cannot race.
    private FileStream LockDirectory()
    {Directory.CreateDirectory(_root);return new(Path.Combine(_root,".rugtrace.lock"),FileMode.OpenOrCreate,FileAccess.Write,FileShare.None);}
    private void Reserve(string path,int added)
    {
        if(File.Exists(path)&&File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Trace reparse point");
        var files=Files();
        if(files.Length>_limits.MaxFiles)throw new IOException("Trace file capacity");
        long total=files.Sum(f=>f.Length);int count=files.Length;
        // Cleanup only recognized component files from closed, older traces. Never touch arbitrary files.
        foreach(var summary in files.Where(f=>f.Name.EndsWith(".summary",StringComparison.Ordinal)).OrderBy(f=>f.LastWriteTimeUtc))
        {
            if(Path.GetFileName(path).StartsWith(summary.Name[..74]+".",StringComparison.Ordinal))continue;
            bool old=summary.LastWriteTimeUtc<DateTime.UtcNow.AddDays(-_limits.RetentionDays);
            if(!old&&total+added<=_limits.MaxDiskBytes&&count+(File.Exists(path)?0:1)<=_limits.MaxFiles)break;
            TraceSummary state;
            try{if(summary.Length>_limits.MaxEventBytes)continue;state=Decode<TraceSummary>(File.ReadAllBytes(summary.FullName));}
            catch{continue;}
            if(!state.RecordingClosed)continue;
            string stem=summary.FullName[..^".summary".Length];
            foreach(var item in files.Where(f=>f.FullName==stem+".events"||f.FullName==stem+".summary"||f.FullName==stem+".summary.tmp"))
            {long length=item.Length;File.Delete(item.FullName);total-=length;count--;}
            string prefix=summary.Name[..42];
            if(!new DirectoryInfo(_root).EnumerateFiles(prefix+"*").Any(f=>f.Name.EndsWith(".events",StringComparison.Ordinal)))
                foreach(var seal in files.Where(f=>f.Name.StartsWith(prefix,StringComparison.Ordinal)&&f.Name.EndsWith(".closed",StringComparison.Ordinal)&&f.Exists))
                {long length=seal.Length;File.Delete(seal.FullName);total-=length;count--;}
        }
        if(total+added>_limits.MaxDiskBytes||count+(File.Exists(path)?0:1)>_limits.MaxFiles)throw new IOException("Trace disk capacity");
    }
    internal async Task AppendAsync(ExecutionTraceEvent e)
    {
        byte[] bytes=Envelope(e);
        if(bytes.Length+1>_limits.MaxEventBytes)throw new InvalidDataException("Trace event capacity");
        using var guard=LockDirectory();string path=Stem(e.HostSessionId,e.TraceId)+".events";
        if((File.Exists(path)?new FileInfo(path).Length:0)+bytes.Length+1>_limits.MaxFileBytes)throw new IOException("Trace file capacity");
        Reserve(path,bytes.Length+1);
        var line=new byte[bytes.Length+1];bytes.CopyTo(line,0);line[^1]=(byte)'\n';
        await _sink.AppendAsync(path,line).ConfigureAwait(false);
    }
    internal async Task WriteSummaryAsync(TraceSummary summary)
    {
        byte[] bytes=Envelope(summary);using var guard=LockDirectory();string path=Stem(summary.HostSessionId,summary.TraceId)+".summary";
        if(!File.Exists(Stem(summary.HostSessionId,summary.TraceId)+".events"))return; // Retention must not resurrect an evicted summary.
        Reserve(path+".tmp",bytes.Length);await _sink.ReplaceSummaryAsync(path,bytes).ConfigureAwait(false);
    }
    private sealed record Seal(int SchemaVersion,Guid HostSessionId,bool Closed,long DroppedEvents);
    internal Task CompleteSessionAsync(Guid session,long dropped)
    {
        using var guard=LockDirectory();string path=Stem(session,Guid.Empty)+".closed";
        byte[] bytes=Envelope(new Seal(1,session,true,dropped));Reserve(path,bytes.Length);
        File.WriteAllBytes(path,bytes);return Task.CompletedTask;
    }
    public TraceCatalogPage Discover(Guid? instanceId=null,int offset=0,int limit=100)
    {
        if(limit is <1 or >256||offset<0||offset>_limits.MaxFiles)throw new ArgumentOutOfRangeException(nameof(limit));
        var entries=new List<TraceCatalogEntry>();
        foreach(var file in Files().Where(f=>f.Name.EndsWith(".events",StringComparison.Ordinal)).OrderBy(f=>f.Name,StringComparer.Ordinal))
        {
            Guid session=Guid.ParseExact(file.Name.Substring(9,32),"N"),trace=Guid.ParseExact(file.Name.Substring(42,32),"N");
            var page=Read(session,trace,limit:1);
            if(instanceId is null||page.Summary?.Run?.InstanceId==instanceId)
                entries.Add(new(session,trace,page.Summary?.Run,page.Status,page.Summary));
        }
        return new(Array.AsReadOnly(entries.Skip(offset).Take(limit).ToArray()),entries.Count>offset+limit?offset+limit:null);
    }
    public TracePage Read(Guid session,Guid trace,Guid? operationId=null,long afterSequence=0,int limit=100,long? throughSequence=null)
    {
        if(limit is <1 or >256||afterSequence<0||throughSequence<afterSequence)return new(TraceReadStatus.CapacityExceeded,[],null,null,true);
        string stem=Stem(session,trace),path=stem+".events",summaryPath=stem+".summary";
        TraceSummary? summary=null;long? sessionDropped=null;bool incomplete=File.Exists(summaryPath+".tmp");
        try
        {
            var bytes=ReadOptional(path,_limits.MaxFileBytes);
            if(bytes is null)return new(TraceReadStatus.NotFound,[],null,null,true);
            string sealPath=Stem(session,Guid.Empty)+".closed";
            var sealBytes=ReadOptional(sealPath,_limits.MaxEventBytes);
            if(sealBytes is null)incomplete=true;
            else
            {
                var seal=Decode<Seal>(sealBytes);
                if(seal.SchemaVersion!=1)return new(TraceReadStatus.UnsupportedFormat,[],null,null,true);
                if(seal.HostSessionId!=session||!seal.Closed)throw new InvalidDataException("Seal identity");
                sessionDropped=seal.DroppedEvents;incomplete|=seal.DroppedEvents!=0;
            }
            var summaryBytes=ReadOptional(summaryPath,_limits.MaxEventBytes);
            if(summaryBytes is not null)
            {
                summary=Decode<TraceSummary>(summaryBytes);
                if(summary.SchemaVersion!=1)return new(TraceReadStatus.UnsupportedFormat,[],null,summary,true);
                if(summary.HostSessionId!=session||summary.TraceId!=trace)throw new InvalidDataException("Summary identity");
            }
            else incomplete=true;
            var rows=new List<ExecutionTraceEvent>();
            long previous=0,count=0,first=0;int start=0,work=0;bool cleanup=false,cleanupFailure=false;
            for(int i=0;i<bytes.Length;i++)
            {
                if(bytes[i]!=(byte)'\n')continue;
                int length=i-start;if(length<=0||length>_limits.MaxEventBytes)throw new InvalidDataException("Line size");
                var e=Decode<ExecutionTraceEvent>(bytes.AsMemory(start,length));start=i+1;
                if(e.SchemaVersion!=1)return new(TraceReadStatus.UnsupportedFormat,[],null,summary,true);
                if(e.HostSessionId!=session||e.TraceId!=trace||e.Sequence<=previous||e.MonotonicFrequency<=0||!Enum.IsDefined(e.Kind))throw new InvalidDataException("Event identity/order");
                if(e.Authorization is {} auth)
                {
                    if(auth.Required is null||auth.Required.Count>16||auth.Missing?.Count>16)throw new InvalidDataException("Authorization shape");
                    e=e with{Authorization=auth with{Required=Array.AsReadOnly(auth.Required.ToArray()),
                        Missing=auth.Missing is null?null:Array.AsReadOnly(auth.Missing.ToArray())}};
                }
                if(e.Observation?.ValidationFrames is {} frames)
                {
                    if(frames.Count>3)throw new InvalidDataException("Validation frame count");
                    e=e with{Observation=e.Observation with{ValidationFrames=Array.AsReadOnly(frames.ToArray())}};
                }
                if(e.Kind==TraceEventKind.WorkStarted)work++;if(e.Kind==TraceEventKind.WorkCompleted)work--;
                if(e.Kind==TraceEventKind.CleanupCompleted)cleanup=true;
                if(e.Kind is TraceEventKind.CleanupFailed or TraceEventKind.ReleaseFailed)cleanupFailure=true;
                first=first==0?e.Sequence:first;previous=e.Sequence;count++;
                if(e.Sequence>afterSequence&&(throughSequence is null||e.Sequence<=throughSequence)&&(operationId is null||e.OperationId==operationId)&&rows.Count<=limit)rows.Add(e);
            }
            incomplete|=start!=bytes.Length||summary is null||summary.Incomplete||summary.LastPersisted!=previous||
                summary.FirstPersisted!=first||summary.PersistedEvents!=count||summary.ActualWorkOutstanding!=work||
                summary.RunCleanupCompleted!=cleanup||summary.RecordedEvents!=count||summary.LastRecorded!=previous||
                (summary.CleanupSucceeded is {} succeeded&&succeeded==cleanupFailure);
            return new(incomplete?TraceReadStatus.Incomplete:TraceReadStatus.Valid,Array.AsReadOnly(rows.Take(limit).ToArray()),
                rows.Count>limit?rows[limit-1].Sequence:null,summary,true,incomplete?"Missing/partial/unclosed/lost events or summary lag.":null,sessionDropped);
        }
        catch(TraceReadCapacityException){return new(TraceReadStatus.CapacityExceeded,[],null,summary,true);}
        catch(Exception e) when(e is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {return new(TraceReadStatus.Corrupt,[],null,summary,true,e.GetType().Name+": "+e.Message);}
        // Real IO/access failures remain visible to the host reader; writer failures are diagnostics.
    }
}
