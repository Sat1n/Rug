using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using Rug.UI.Core.Native;
using Scheduler = Rug.UI.Core.Services.TaskScheduler;

internal static partial class CheckpointTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string text) { if (!condition) throw new Exception("Checkpoint: " + text); }
    private static Task Bound(Task task) => task.WaitAsync(TimeSpan.FromSeconds(8));
    private static Task Until(Func<bool> condition) => RecoveryTests.Until(condition);
    private const string Script = """
        function on_init()
          local retained = 42
          local result = rug.checkpoint.commit('ready', '{"step":1,"empty":null,"flag":false}', '["panel-ready"]')
          assert(type(result) == 'table' and type(result.status) == 'string' and retained == 42)
          rug.set_state('commit_status',result.status)
          rug.set_state('commit_error',result.error)
          rug.set_state('after',1)
        end
        function on_tick() rug.sleep(60000) end
        function on_stop() rug.log('checkpoint_stop') end
        """;
    internal static async Task ChildReadAsync(string[] args)
    {
        var address = JsonSerializer.Deserialize<CheckpointAddress>(args[2])!;
        var expected = JsonSerializer.Deserialize<CheckpointCompatibility>(args[3])!;
        await using var store = new FileCheckpointStore(args[1]);
        var read = await store.ReadAsync(address, expected);
        Check(read.Status == CheckpointReadStatus.Valid && read.Record!.State.GetProperty("step").GetInt32() == 1 &&
            read.Record.State.GetProperty("empty").ValueKind == JsonValueKind.Null && !read.Record.State.TryGetProperty("missing", out _), "child-process read did not preserve business data");
        Console.WriteLine("checkpoint-child-read-ok; no tasks/grants/targets created");
    }
    public static async Task RunAsync()
    {
        await LuaRoundTripAsync();
        foreach (string mode in new[] { "false", "missing", "timeout", "stop-validation", "stop-before", "stop-publish", "stop-publish-fault", "fault-publish", "fault-capture", "permission", "revoke" }) await FailureAsync(mode);
        await StoreCasesAsync(); await VersionFieldAsync(); await JsonCasesAsync(); await ExactNumbersAsync(); await IdentityAsync();
        foreach(string mode in new[]{"false","missing","timeout","stop-before"}) await RetainPreviousAsync(mode);
        await CallerCancellationAsync();
        await BlockedCancellationPropagationAsync(afterDecision: false);
        await BlockedCancellationPropagationAsync(afterDecision: true);
        await NativeObservationStopAsync();
        await ShippedSampleAsync();
        await InputFreezeAsync();
        await ConcurrentSourceAsync();
        await RunRestoreAsync();
        Console.WriteLine("Checkpoint PASS: actual Lua, trusted fresh observation, real files/reopen/child, isolated sources, Stop/IO boundaries, corruption/compatibility/limits/JSON");
    }
    private static CheckpointAddress Address(TaskExecutionContext c) => new(c.CheckpointMetadata?.InstallationId??CheckpointJson.InstallationId(c.RecoveryAuthorization!.Installation),c.PluginId,c.InstanceId,c.RunGeneration);
    private static CheckpointCompatibility Expected(TaskExecutionContext c) => new(Address(c).InstallationId,c.PluginId,
        c.CheckpointMetadata?.PluginVersion??"1",c.Runtime.LoadedCode!.ContentHash,"ready",1,c.CheckpointMetadata?.ConfigHash??CheckpointJson.ConfigHash(new Dictionary<string,object?>{["speed"]=2}));
    private static async Task LuaRoundTripAsync()
    {
        await using var f = await Fixture.CreateAsync(); var c = await f.StartAsync();
        await Until(() => f.StoreState.GetState(c.InstanceScope,"after",0) == 1);
        var read = await f.Store.ReadAsync(Address(c),Expected(c));
        Check(read.Status == CheckpointReadStatus.Valid && read.Record!.CompletedActionIds.SequenceEqual(new[]{"panel-ready"}) &&
            read.Record.State.GetProperty("step").GetInt32() == 1 && read.Record.State.GetProperty("empty").ValueKind == JsonValueKind.Null &&
            !read.Record.State.TryGetProperty("missing",out _) && read.Record.ConfigRevision == 1, "Lua checkpoint fields or atomic config revision mismatch");
        await using (var reopened = new FileCheckpointStore(f.LogRoot))
            Check((await reopened.ReadAsync(Address(c),Expected(c))).Record!.CheckpointId == read.Record!.CheckpointId,"same-process reopening lost record");
        var detached = read.Record with { State = CheckpointJson.Parse("{\"step\":99}") };
        try{((IList<string>)read.Record.CompletedActionIds)[0]="changed";throw new Exception("read action list was mutable");}catch(NotSupportedException){}
        Check((await f.Store.ReadAsync(Address(c),Expected(c))).Record!.State.GetProperty("step").GetInt32() == 1 && detached.State.GetProperty("step").GetInt32() == 99,"read aliases storage");
        string executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(CheckpointTests).Assembly.Location);
        foreach (var arg in new[]{"--checkpoint-child-read",f.LogRoot,JsonSerializer.Serialize(Address(c)),JsonSerializer.Serialize(Expected(c))}) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!; var output=child.StandardOutput.ReadToEndAsync();var errors=child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Check(child.ExitCode == 0 && (await output).Contains("checkpoint-child-read-ok"),"child process read failed: "+await errors);
        Console.WriteLine("Checkpoint real Lua/filesystem PASS: same-process reopen AND separate child-process read; fresh frames="+f.Captures[0].Calls);
    }
    private static async Task FailureAsync(string mode)
    {
        var publisher = new Publisher { Hold=mode is "stop-publish" or "stop-publish-fault", Fail=mode is "fault-publish" or "stop-publish-fault" };
        var before=Signal();var release=Signal();
        await using var f=await Fixture.CreateAsync(publisher:publisher,
            beforePublish:mode=="stop-before" ? async ct=>{before.TrySetResult();await release.Task.WaitAsync(ct);} : null,
            timeout:mode=="timeout" ? TimeSpan.FromMilliseconds(80):null, checkpoint:mode!="permission");
        f.Validator.Valid=mode!="false";f.Register=mode!="missing";
        f.Validator.Hold=mode is "timeout" or "stop-validation" or "revoke";
        f.CaptureFailure=mode=="fault-capture";
        var c=await f.StartAsync();
        try
        {
            if (mode is "timeout" or "stop-validation" or "revoke")
            {
                await Bound(f.Validator.Entered.Task);
                Check(f.StoreState.GetState(c.InstanceScope,"after",0)==0,"mainline ran during validation");
                if(mode=="stop-validation") await Bound(f.Scheduler.StopTaskAsync(c.InstanceId));
                if(mode=="revoke") f.Authorization.Revoke(c.CheckpointMetadata!.Installation,[Permission.Checkpoint]);
            }
            if(mode=="stop-before") {await Bound(before.Task);await Bound(f.Scheduler.StopTaskAsync(c.InstanceId));}
            if(mode is "stop-publish" or "stop-publish-fault")
            {
                await Bound(publisher.Entered.Task);var stop=f.Scheduler.StopTaskAsync(c.InstanceId);
                Check(c.TaskStatus==AutomationTaskStatus.Stopping&&!stop.IsCompleted&&!f.Captures[0].Disposed,"Stop released resources before noncooperative IO");
                publisher.Release.TrySetResult();await Bound(stop);
            }
            if(mode is "false" or "missing" or "timeout")
            {
                await Until(()=>f.StoreState.GetState(c.InstanceScope,"after",0)==1);
                Check(f.StoreState.GetState<string>(c.InstanceScope,"commit_status","")==(mode=="timeout"?"timed_out":"rejected"),"wrong logical failure result");
                f.Validator.Release.TrySetResult();await Until(()=>f.Coordinator.ValidatorsInFlight==0);
            }
            else {await Bound(c.Completion);Check(c.TaskStatus==(mode is "fault-publish" or "stop-publish-fault" or "fault-capture" or "permission"?AutomationTaskStatus.Faulted:AutomationTaskStatus.Stopped),"wrong fault/stop classification: "+mode+" "+c.LastError);}
            var read=await f.Store.ReadAsync(Address(c),Expected(c));
            Check(read.Status==(mode=="stop-publish"?CheckpointReadStatus.Valid:CheckpointReadStatus.Absent),"unexpected published checkpoint: "+mode);
            Check(publisher.Calls==(mode is "stop-publish" or "stop-publish-fault" or "fault-publish"?1:0),"publication occurred before admission/verification");
            Console.WriteLine("Checkpoint failure boundary PASS: "+mode);
        }
        finally {release.TrySetResult();publisher.Release.TrySetResult();f.Validator.Release.TrySetResult();}
    }
    private sealed class Publisher : ICheckpointFilePublisher
    {
        public bool Hold,Fail;public int Calls;
        public readonly TaskCompletionSource Entered=Signal(),Release=Signal();
        public async Task PublishAsync(string temp,string destination,bool replace)
        {Interlocked.Increment(ref Calls);Entered.TrySetResult();if(Hold)await Release.Task;if(Fail)throw new IOException("injected replace failure");await new AtomicCheckpointFilePublisher().PublishAsync(temp,destination,replace);}
    }
    private sealed class Validator : ICheckpointValidator
    {
        public bool Valid=true,Hold; public int Calls;
        public readonly TaskCompletionSource Entered=Signal(),Release=Signal();
        public async Task<bool> ValidateAsync(CheckpointObservation observation,CheckpointSubmission submission,CancellationToken token)
        {Interlocked.Increment(ref Calls);Entered.TrySetResult();if(Hold)await Release.Task;token.ThrowIfCancellationRequested();var frame=await observation.CaptureFreshAsync();return Valid&&frame.Pixels[1]==200;}
    }
    private sealed class Capture : ICaptureService
    {
        public bool Fail,Disposed,Hold;public int Calls;
        public readonly TaskCompletionSource Entered=Signal(),Release=Signal();
        public readonly TaskCompletionSource Blocked=Signal();
        public bool IsCapturing=>!Disposed;
        public Task StartAsync(nint hwnd,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public async Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken=default)
        {Calls++;Entered.TrySetResult();if(Hold){Blocked.TrySetResult();await Release.Task;}if(Fail)throw new IOException("capture fault");var pixels=new byte[8*8*4];for(int i=0;i<pixels.Length;i+=4){pixels[i+1]=200;pixels[i+3]=255;}return new(pixels,8,8,32);}
        public Task StopAsync()=>Task.CompletedTask;
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
    private sealed class WindowValidator : IInputWindowValidator
    {public long Stamp=1;public bool Valid=true;public InputTarget Bind(nint hwnd)=>new(hwnd,Stamp);public bool IsValid(InputTarget target)=>Valid&&target.WindowStamp==Stamp;}
    private sealed class Logger : Microsoft.Extensions.Logging.ILogger<LuaRuntime>
    {
        public Action<string>? Intercept;
        public IDisposable BeginScope<TState>(TState state)=>new Scope();
        private sealed class Scope:IDisposable {public void Dispose(){}}
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level)=>true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level,Microsoft.Extensions.Logging.EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)=>Intercept?.Invoke(formatter(state,exception));
    }
    private sealed class NoAnomaly : IAnomalyLogger
    { public Task<string> LogAnomalyAsync(TaskExecutionContext context,string reason,string detail)=>Task.FromResult("unused"); }
    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root,LogRoot;public required FileCheckpointStore Store;public required Scheduler Scheduler;public required CheckpointCoordinator Coordinator;
        public readonly StateStore StoreState=new();public required CommandBus Bus;
        public readonly PluginAuthorization Authorization=new();public readonly CheckpointNodeRegistry Registry=new();public readonly Validator Validator=new();
        public readonly RuntimeUpdateCompatibilityRegistry UpdateRules=new();
        public readonly List<Capture> Captures=[];public bool Register=true,CaptureFailure,HoldCapture;
        public readonly List<FakeInput> Inputs=[];
        public readonly WindowValidator Windows=new();
        public IInputArbiter Arbiter=null!;
        public readonly Logger Log=new();public IDisposable? NodeRegistration;
        public Action<TaskExecutionContext>? OnReady;
        public static async Task<Fixture> CreateAsync(ICheckpointFilePublisher? publisher=null,Func<CancellationToken,Task>? beforePublish=null,TimeSpan? timeout=null,bool checkpoint=true,string? script=null,string version="1",bool input=false,bool agent=false,IAnomalyLogger? anomalyLogger=null,Func<CheckpointCoordinator,ICheckpointCoordinator>? checkpointHost=null)
        {
            string root=Path.Combine(AppContext.BaseDirectory,"checkpoint-test-"+Guid.NewGuid().ToString("N"));string dir=Path.Combine(root,"plugins","probe");Directory.CreateDirectory(dir);
            var permissions=new List<string>{Permission.Capture,Permission.Timer};if(checkpoint)permissions.Add(Permission.Checkpoint);
            if(input)permissions.Add(Permission.ControlInput);
            if(agent)permissions.Add(Permission.Agent);
            await File.WriteAllTextAsync(Path.Combine(dir,"manifest.json"),JsonSerializer.Serialize(new{id="probe",name="Probe",version,targetProcess="test",permissions}));
            await File.WriteAllTextAsync(Path.Combine(dir,"main.lua"),script??Script);
            var f=new Fixture{Root=root,LogRoot=Path.Combine(root,"records"),Store=null!,Bus=null!,Scheduler=null!,Coordinator=null!};
            f.Store=new(f.LogRoot,null,publisher,beforePublish);f.Bus=new(f.StoreState);f.Bus.SetState(StateScope.Plugin("probe"),"speed",2);
            TestAuthorizationHost.ApproveCatalog(f.Authorization,Path.Combine(root,"plugins"));
            f.Coordinator=new(f.Store,f.Registry,f.Authorization,timeout);
            f.Arbiter=new InputArbiter(f.Windows);
            f.Scheduler=new(new PluginManager(NullLogger<PluginManager>.Instance),Path.Combine(root,"plugins"),
                ()=>{var c=new Capture{Fail=f.CaptureFailure,Hold=f.HoldCapture};f.Captures.Add(c);return c;},()=>{var i=new FakeInput();f.Inputs.Add(i);return i;},
                (c,i)=>new LuaRuntime(c,new FakeOcr(),i,f.Log,new FakeCoordinateMapper(),ensureForeground:_=>true,commandBus:f.Bus),NullLogger<Scheduler>.Instance,anomalyLogger??new NoAnomaly(),
                commandBus:f.Bus,authorization:f.Authorization,inputArbiter:f.Arbiter,checkpoints:checkpointHost?.Invoke(f.Coordinator)??f.Coordinator,
                checkpointStore:f.Store,checkpointRestores:f.Registry,updateCompatibility:f.UpdateRules);
            return f;
        }
        public async Task<TaskExecutionContext> StartAsync()
        {
            TaskExecutionContext? owned=null;
            var id=await Scheduler.StartTaskAsync("probe",123,new Dictionary<string,object?>(),identity=>
            {
                var c=Scheduler.Instances.Single(c=>c.InstanceId==identity.InstanceId);
                owned=c;
                if(Register&&c.CheckpointMetadata is{}m&&Registry.Find(m,"ready") is null)
                    NodeRegistration=Registry.Register(new(m.Installation,m.PluginVersion,m.Code.ContentHash,"ready",1,Array.AsReadOnly(new[]{"panel-ready"})),Validator);
                OnReady?.Invoke(c);
            });
            return owned!;
        }
        public async ValueTask DisposeAsync()
        {Validator.Release.TrySetResult();foreach(var c in Captures)c.Release.TrySetResult();await Scheduler.DisposeAsync();await Store.DisposeAsync();NodeRegistration?.Dispose();Bus.Dispose();Directory.Delete(Root,true);}
    }
    private static CheckpointRecord Record(string installation,Guid source,long sequence=1)=>new(CheckpointJson.SchemaVersion,Guid.NewGuid(),sequence,DateTime.UtcNow,
        installation,"probe","1",CheckpointJson.Hash("code"u8),source,1,"ready",1,0,CheckpointJson.Hash("config"u8),CheckpointJson.Parse("{\"step\":1}"),Array.AsReadOnly(new[]{"panel-ready"}));
    private static CheckpointAddress Address(CheckpointRecord r)=>new(r.InstallationId,r.PluginId,r.SourceInstanceId,r.SourceRunGeneration);
    private static CheckpointCompatibility Expected(CheckpointRecord r)=>new(r.InstallationId,r.PluginId,r.PluginVersion,r.ContentHash,r.NodeId,r.NodeStateVersion,r.ConfigHash);
    private static Task WriteEnvelopeAsync(string path,CheckpointRecord record)
    {
        var payload=JsonSerializer.SerializeToElement(record,CheckpointJson.Options);
        return File.WriteAllBytesAsync(path,JsonSerializer.SerializeToUtf8Bytes(new{checksum=CheckpointJson.Hash(CheckpointJson.Canonical(payload)),record},CheckpointJson.Options));
    }
    private static async Task StoreCasesAsync()
    {
        string root=Path.Combine(AppContext.BaseDirectory,"checkpoint-store-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            await using var store=new FileCheckpointStore(root);
            var record=Record(CheckpointJson.Hash("installation-one"u8),Guid.NewGuid());
            Check((await store.PublishAsync(record,()=>true,default)).Status=="committed","first create failed");
            string path=store.Locate(Address(record));
            await File.WriteAllTextAsync(Path.Combine(root,"partial.tmp"),"half written");
            Check((await store.ReadAsync(Address(record),Expected(record))).Status==CheckpointReadStatus.Valid,"temporary residue affected reader");
            foreach(var (expect,status) in new[]{
                (Expected(record) with{InstallationId=CheckpointJson.Hash("other"u8)},CheckpointReadStatus.InstallationMismatch),
                (Expected(record) with{PluginId="other"},CheckpointReadStatus.PluginMismatch),
                (Expected(record) with{ContentHash=CheckpointJson.Hash("other"u8)},CheckpointReadStatus.ContentMismatch),
                (Expected(record) with{PluginVersion="2"},CheckpointReadStatus.ContentMismatch),
                (Expected(record) with{NodeStateVersion=2},CheckpointReadStatus.NodeVersionMismatch),
                (Expected(record) with{ConfigHash=CheckpointJson.Hash("other"u8)},CheckpointReadStatus.ConfigIncompatible)})
                Check((await store.ReadAsync(Address(record),expect)).Status==status,"wrong compatibility result: "+status);
            var changedRevision=record with{ConfigRevision=99,SubmissionSequence=2};
            Check((await store.PublishAsync(changedRevision,()=>true,default)).Status=="committed"&&
                (await store.ReadAsync(Address(record),Expected(record))).Status==CheckpointReadStatus.Valid,"config revision alone made compatible hash incompatible");
            await using(var faulting=new FileCheckpointStore(root,publisher:new Publisher{Fail=true}))
            {try{await faulting.PublishAsync(record with{SubmissionSequence=3},()=>true,default);throw new Exception("replace failure swallowed");}catch(IOException){} }
            Check((await store.ReadAsync(Address(record),Expected(record))).Record!.SubmissionSequence==2,"replace failure lost previous record");
            var publisher=new Publisher{Hold=true};await using(var serial=new FileCheckpointStore(root,publisher:publisher))
            {
                var newer=serial.PublishAsync(record with{SubmissionSequence=4},()=>true,default);await Bound(publisher.Entered.Task);
                var lateOlder=serial.PublishAsync(record with{SubmissionSequence=3},()=>true,default);
                Check(!lateOlder.IsCompleted&&(await store.ReadAsync(Address(record),Expected(record))).Record!.SubmissionSequence==2,"pre-replace read or serial writer failed");
                publisher.Release.TrySetResult();Check((await newer).Status=="committed"&&(await lateOlder).Error==CheckpointError.StaleSubmission,"late write covered newer record");
            }
            foreach(var isolated in new[]{record with{SourceInstanceId=Guid.NewGuid()},record with{InstallationId=CheckpointJson.Hash("installation-two"u8)},record with{SourceRunGeneration=2}})
                Check((await store.PublishAsync(isolated,()=>true,default)).Status=="committed"&&
                    (await store.ReadAsync(Address(isolated),Expected(isolated))).Record!.CheckpointId==isolated.CheckpointId,"sources/installations/generations crossed");
            Check((await store.ReadAsync(Address(record),Expected(record))).Record!.SubmissionSequence==4,"isolated source overwrote old source");
            await File.WriteAllTextAsync(path,"{\"record\":");Check((await store.ReadAsync(Address(record),Expected(record))).Status==CheckpointReadStatus.Corrupt,"half file accepted");
            await WriteEnvelopeAsync(path,record with{SchemaVersion=CheckpointJson.SchemaVersion+1});Check((await store.ReadAsync(Address(record),Expected(record))).Status==CheckpointReadStatus.UnsupportedFormat,"unsupported version accepted");
            await File.WriteAllBytesAsync(path,new byte[CheckpointJson.RecordBytes+1]);Check((await store.ReadAsync(Address(record),Expected(record))).Status==CheckpointReadStatus.Oversized,"oversized record allocated/accepted");
            await WriteEnvelopeAsync(path,record);var text=await File.ReadAllTextAsync(path);await File.WriteAllTextAsync(path,text.Replace("\"step\":1","\"step\":2"));
            Check((await store.ReadAsync(Address(record),Expected(record))).Status==CheckpointReadStatus.Corrupt,"checksum corruption accepted");
            await WriteEnvelopeAsync(path,record);
            await using(var limited=new FileCheckpointStore(root,new(MaxRecords:4,MaxBytes:32*1024*1024)))
                Check((await limited.PublishAsync(record with{SourceInstanceId=Guid.NewGuid()},()=>true,default)).Error==CheckpointError.CapacityExceeded,"file quota ignored");
            long bytes=new DirectoryInfo(root).EnumerateFiles().Where(f=>f.Extension is ".json" or ".tmp").Sum(f=>f.Length);
            await using(var limited=new FileCheckpointStore(root,new(MaxBytes:bytes+1)))
                Check((await limited.PublishAsync(record with{SubmissionSequence=5},()=>true,default)).Error==CheckpointError.CapacityExceeded,"byte quota ignored");
            await using(var limited=new FileCheckpointStore(root,new(MaxTemps:1)))
                Check((await limited.PublishAsync(record with{SubmissionSequence=5},()=>true,default)).Error==CheckpointError.CapacityExceeded,"temporary quota ignored");
            Check((await store.ReadAsync(Address(record),Expected(record))).Record!.CheckpointId==record.CheckpointId,"quota rejection changed previous record");
            Check((await store.PublishAsync(record with{SubmissionSequence=5,PluginVersion=new string('x',CheckpointJson.RecordBytes)},()=>true,default)).Error==CheckpointError.CapacityExceeded,
                "whole-envelope cap ignored");
            Console.WriteLine("Checkpoint store PASS: first/replace, late sequence, isolation, old/new reads, corruption/half/temp/versions/compatibility and all capacity limits");
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task VersionFieldAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "checkpoint-version-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileCheckpointStore(root);
            var record = Record(CheckpointJson.Hash("version-field"u8), Guid.NewGuid());
            Check((await store.PublishAsync(record, () => true, default)).Status == "committed", "version fixture publication failed");
            string path = store.Locate(Address(record));
            byte[] original = await File.ReadAllBytesAsync(path);
            string text = System.Text.Encoding.UTF8.GetString(original);
            string versionField = "\"schemaVersion\":" + CheckpointJson.SchemaVersion;
            foreach (string version in new[] { "2147483648", "-2147483649", "2.5", "\"2\"", "null", "true", "{}", "[]", "missing" })
            {
                string malformed = version == "missing" ? text.Replace(versionField + ",", "", StringComparison.Ordinal) :
                    text.Replace(versionField, "\"schemaVersion\":" + version, StringComparison.Ordinal);
                Check(malformed != text, "version fixture did not change the file");
                await File.WriteAllTextAsync(path, malformed);
                byte[] before = await File.ReadAllBytesAsync(path);
                Check((await store.ReadAsync(Address(record), Expected(record))).Status == CheckpointReadStatus.Corrupt,
                    "malformed schema version did not return Corrupt: " + version);
                byte[] after = await File.ReadAllBytesAsync(path);
                Check(before.AsSpan().SequenceEqual(after), "reading malformed version modified original bytes");
            }
            foreach (int version in new[] { 1, CheckpointJson.SchemaVersion + 1, -1, int.MaxValue })
            {
                await File.WriteAllTextAsync(path, text.Replace(versionField, "\"schemaVersion\":" + version, StringComparison.Ordinal));
                byte[] before = await File.ReadAllBytesAsync(path);
                Check((await store.ReadAsync(Address(record), Expected(record))).Status == CheckpointReadStatus.UnsupportedFormat,
                    "unsupported integer version classification changed: " + version);
                byte[] after = await File.ReadAllBytesAsync(path);
                Check(before.AsSpan().SequenceEqual(after), "unsupported version read modified bytes");
            }
            await File.WriteAllBytesAsync(path, original);
            Check((await store.ReadAsync(Address(record), Expected(record))).Status == CheckpointReadStatus.Valid, "valid version rejected");
            using (var lockedFile = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { await store.ReadAsync(Address(record), Expected(record)); throw new Exception("real sharing IO failure was swallowed"); }
                catch (IOException) { }
            }
            byte[] afterFailure = await File.ReadAllBytesAsync(path);
            Check(original.AsSpan().SequenceEqual(afterFailure), "failed IO read modified original bytes");
            Console.WriteLine("Checkpoint version field PASS: overflow/fraction/type/missing => Corrupt; legacy/unknown integers => UnsupportedFormat; bytes unchanged; real IO failure exposed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task JsonCasesAsync()
    {
        Check(!CaptureService.HasFrame(RugStatus.ErrCaptureFailed),"native no-frame mapping changed");
        foreach(int status in new[]{RugStatus.ErrOutOfMemory,RugStatus.ErrNotInitialized,RugStatus.ErrInvalidParam})
        {try{CaptureService.HasFrame(status);throw new Exception("native resource fault became no frame");}catch(RugNativeException){} }
        foreach(string json in new[]{"", "{", "{\"x\":1,\"x\":2}", "1e500", "{\"password\":\"secret\"}","{\"HWND\":123}",new string('[',17)+"0"+new string(']',17),"\""+new string('x',CheckpointJson.StateBytes)+"\""})
        {try{CheckpointJson.Parse(json);throw new Exception("invalid business JSON accepted");}catch(Exception e)when(e is ArgumentException or JsonException){} }
        Check(CheckpointJson.Parse("null").ValueKind==JsonValueKind.Null&&CheckpointJson.Parse(new string('[',16)+"0"+new string(']',16)).ValueKind==JsonValueKind.Array,"null/depth boundary wrong");
        try{CheckpointJson.Parse(JsonSerializer.Serialize(new string('中',20000),new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));throw new Exception("expanded encoded state limit ignored");}catch(ArgumentException){}
        Check(CheckpointJson.Parse("\""+new string('x',CheckpointJson.StateBytes-2)+"\"").GetString()!.Length==CheckpointJson.StateBytes-2,"exact byte boundary rejected");
        foreach(string json in new[]{"null","{}","[null]","[\"same\",\"same\"]",JsonSerializer.Serialize(new[]{new string('x',65)}),JsonSerializer.Serialize(Enumerable.Range(0,129).Select(i=>"a"+i))})
        {try{CheckpointJson.Actions(json);throw new Exception("invalid action IDs accepted");}catch(ArgumentException){} }
        Check(CheckpointJson.Actions(JsonSerializer.Serialize(Enumerable.Range(0,128).Select(i=>"a"+i))).Count==128,"128 actions rejected");
        var a=new Dictionary<string,object?>{["b"]=2.0,["a"]=null};var b=new Dictionary<string,object?>{["a"]=null,["b"]=2};
        Check(CheckpointJson.ConfigHash(a)==CheckpointJson.ConfigHash(b)&&CheckpointJson.ConfigHash(new Dictionary<string,object?>{["b"]=2})!=CheckpointJson.ConfigHash(b),"canonical config/missing-null semantics wrong");
        var cycle=new Dictionary<string,object?>();cycle["self"]=cycle;
        foreach(var invalid in new Dictionary<string,object?>[]{cycle,new(){["bad"]=double.NaN},new(){["bad"]=(Action)(()=>{})}})
        {try{CheckpointJson.ConfigHash(invalid);throw new Exception("unsupported config object accepted");}catch(Exception e)when(e is JsonException or ArgumentException or NotSupportedException){} }
        await using var f=await Fixture.CreateAsync(script:Script.Replace("'{\"step\":1,\"empty\":null,\"flag\":false}'","'null'"));var c=await f.StartAsync();
        await Until(()=>f.StoreState.GetState(c.InstanceScope,"after",0)==1);
        Check((await f.Store.ReadAsync(Address(c),Expected(c))).Record!.State.ValueKind==JsonValueKind.Null,"actual Lua null did not persist");
        Console.WriteLine("Checkpoint JSON PASS: byte/depth/actions/null/missing/duplicates/nonfinite/cycles/unsupported objects and real Lua null");
    }
    private static async Task ExactNumbersAsync()
    {
        static string Config(string number) => CheckpointJson.ConfigHash(new Dictionary<string, object?> { ["number"] = CheckpointJson.Parse(number) });
        foreach (var (left, right) in new[] { ("1e-40", "10e-41"), ("1e-40", "0." + new string('0', 39) + "1"),
            ("1000", "1.000E+003"), ("-0.000e-999", "0"), ("1e-2147483649", "10e-2147483650"),
            ("123456789012345678901234567890123456789", "1234567890123456789012345678901234567890e-1") })
            Check(Config(left) == Config(right), "equivalent exact decimal values produced different config hashes");

        string root = Path.Combine(AppContext.BaseDirectory, "checkpoint-numbers-" + Guid.NewGuid().ToString("N"));
        await using var store = new FileCheckpointStore(root);
        try
        {
            foreach (var (original, changed) in new[] { ("1e-40", "0"), ("1e-400", "0"), ("1e-2147483649", "0"),
                ("0.123456789012345678901234567890123456789", "0.123456789012345678901234567890123456788"),
                ("9007199254740992", "9007199254740993"),
                ("123456789012345678901234567890123456789", "123456789012345678901234567890123456788") })
            {
                Check(Config(original) != Config(changed), "config numeric precision collision: " + original);
                var record = Record(CheckpointJson.Hash("exact-numbers"u8), Guid.NewGuid()) with { State = CheckpointJson.Parse("{\"number\":" + original + "}") };
                Check((await store.PublishAsync(record, () => true, default)).Status == "committed", "exact numeric record not published");
                await using (var reopened = new FileCheckpointStore(root))
                {
                    var read = await reopened.ReadAsync(Address(record), Expected(record));
                    Check(read.Status == CheckpointReadStatus.Valid && read.Record!.State.GetProperty("number").GetRawText() == original,
                        "filesystem reopen rounded numeric data");
                }
                string path = store.Locate(Address(record)); string saved = await File.ReadAllTextAsync(path);
                string tampered = saved.Replace("\"number\":" + original, "\"number\":" + changed, StringComparison.Ordinal);
                Check(tampered != saved, "numeric tamper fixture did not edit the value");
                await File.WriteAllTextAsync(path, tampered);
                Check((await store.ReadAsync(Address(record), Expected(record))).Status == CheckpointReadStatus.Corrupt,
                    "numeric tamper retained checksum validity: " + original);
            }
            var legacy = Record(CheckpointJson.Hash("legacy-numbers"u8), Guid.NewGuid());
            string legacyPath = store.Locate(Address(legacy));
            await WriteEnvelopeAsync(legacyPath, legacy with { SchemaVersion = 1 });
            string legacyBytes = await File.ReadAllTextAsync(legacyPath);
            Check((await store.ReadAsync(Address(legacy), Expected(legacy))).Status == CheckpointReadStatus.UnsupportedFormat,
                "legacy rounded-checksum format was accepted");
            try { await store.PublishAsync(legacy with { SubmissionSequence = 2 }, () => true, default); throw new Exception("legacy record silently replaced"); }
            catch (IOException) { }
            Check(await File.ReadAllTextAsync(legacyPath) == legacyBytes, "legacy rejection changed stored bytes");
            Check((await store.PublishAsync(legacy with { SchemaVersion = 1 }, () => true, default)).Error == CheckpointError.InvalidParameters,
                "legacy format was newly published");
            Console.WriteLine("Checkpoint exact numbers PASS: tiny/underflow/high-precision/large integers, equivalent forms, config hashes, real-file tamper and legacy format rejection");
        }
        finally { await store.DisposeAsync(); Directory.Delete(root, true); }
    }
    private static async Task IdentityAsync()
    {
        await using var f=await Fixture.CreateAsync(script:Script.Replace("local result =","rug.sleep(60000); local result ="));
        var a=await f.StartAsync();var b=await f.StartAsync();string oldHash=a.CheckpointMetadata!.Code.ContentHash;
        await Until(()=>a.Runtime.State==ScriptState.Yielded&&b.Runtime.State==ScriptState.Yielded);
        await File.WriteAllTextAsync(Path.Combine(f.Root,"plugins","probe","main.lua"),"function on_init() rug.sleep(60000) end");
        f.Bus.SetState(StateScope.Plugin("probe"),"speed",99);
        var tasks=new[]{a,b}.Select(c=>f.Coordinator.CommitAsync(c,"ready","{\"step\":1}","[\"panel-ready\"]")).ToArray();
        var results=await Task.WhenAll(tasks);Check(results.All(r=>r.Status=="committed"),"dual-instance submits failed");
        foreach(var c in new[]{a,b})
        {var r=(await f.Store.ReadAsync(Address(c),Expected(c))).Record!;Check(r.ContentHash==oldHash&&r.ConfigHash==a.CheckpointMetadata.ConfigHash&&r.ConfigRevision==1,"disk code/config change replaced loaded snapshot");}
        await using(var other=await Fixture.CreateAsync())
        {
            var sameId=await other.StartAsync();await Until(()=>other.StoreState.GetState(sameId.InstanceScope,"after",0)==1);
            var otherRecord=(await other.Store.ReadAsync(Address(sameId),Expected(sameId))).Record!;
            Check(sameId.PluginId==a.PluginId&&Address(sameId).InstallationId!=Address(a).InstallationId,
                "host installation identity did not isolate same-ID installations");
            Check((await f.Store.PublishAsync(otherRecord,()=>true,default)).Status=="committed"&&
                (await f.Store.ReadAsync(Address(a),Expected(a))).Record!.SourceInstanceId==a.InstanceId&&
                (await f.Store.ReadAsync(Address(sameId),Expected(sameId))).Record!.SourceInstanceId==sameId.InstanceId,
                "same root crossed same-ID installation records");
        }
        await f.Scheduler.StopTaskAsync(a.InstanceId);await f.Scheduler.RefreshPluginsAsync();var fresh=await f.StartAsync();
        Check(fresh.CheckpointMetadata!.Code.ContentHash!=oldHash&&fresh.CheckpointMetadata.ConfigHash!=a.CheckpointMetadata.ConfigHash&&fresh.InstanceId!=a.InstanceId,"new run inherited old identity/hash/config");
        Check((await f.Store.ReadAsync(Address(a),Expected(fresh))).Status==CheckpointReadStatus.ContentMismatch,"incompatible content accepted");
        try{await f.Coordinator.CommitAsync(a,"ready","{}","[\"panel-ready\"]");throw new Exception("old run submitted after Stop");}catch(OperationCanceledException){}catch(AutomationGateway.GatewayRejectedException){}
        Check((await f.Store.ReadAsync(Address(fresh),Expected(fresh))).Status==CheckpointReadStatus.Absent,"old callback created fresh-run checkpoint");
        Console.WriteLine("Checkpoint identity PASS: dual instances, immutable loaded code/config, new-generation replacement and old-callback rejection");
    }
    private static async Task RetainPreviousAsync(string mode)
    {
        var before=Signal();var releasePublish=Signal();int publications=0;
        string script=Script.Replace("rug.set_state('after',1)","rug.log('checkpoint_barrier'); local next = rug.checkpoint.commit('ready','{\"step\":2}','[\"panel-ready\"]'); rug.set_state('after',1)");
        await using var f=await Fixture.CreateAsync(script:script,timeout:mode=="timeout"?TimeSpan.FromMilliseconds(250):null,
            beforePublish:mode=="stop-before"?async ct=>{if(Interlocked.Increment(ref publications)==2){before.TrySetResult();await releasePublish.Task.WaitAsync(ct);}}:null);
        using var release=new ManualResetEventSlim();var barrier=Signal();
        f.Log.Intercept=message=>{if(message.Contains("checkpoint_barrier")){barrier.TrySetResult();release.Wait();}};
        var c=await f.StartAsync();
        try
        {
            await Bound(barrier.Task);var previous=(await f.Store.ReadAsync(Address(c),Expected(c))).Record!;
            Check(previous.SubmissionSequence==1&&f.StoreState.GetState(c.InstanceScope,"after",0)==0,"first checkpoint or frozen Lua barrier failed");
            if(mode=="false")f.Validator.Valid=false;
            if(mode=="missing")f.NodeRegistration!.Dispose();
            if(mode=="timeout")f.Validator.Hold=true;
            release.Set();
            if(mode=="stop-before"){await Bound(before.Task);await Bound(f.Scheduler.StopTaskAsync(c.InstanceId));}
            else await Until(()=>f.StoreState.GetState(c.InstanceScope,"after",0)==1);
            var retained=(await f.Store.ReadAsync(Address(c),Expected(c))).Record!;
            Check(retained.CheckpointId==previous.CheckpointId&&retained.State.GetProperty("step").GetInt32()==1,"rejected/cancelled second submission replaced prior checkpoint");
            Console.WriteLine("Checkpoint previous-record retention PASS: "+mode);
        }
        finally{release.Set();releasePublish.TrySetResult();f.Validator.Release.TrySetResult();}
    }
    private static async Task CallerCancellationAsync()
    {
        await using var f=await Fixture.CreateAsync();var c=await f.StartAsync();await Until(()=>f.StoreState.GetState(c.InstanceScope,"after",0)==1);
        var previous=(await f.Store.ReadAsync(Address(c),Expected(c))).Record!;
        f.Validator.Hold=true;using var cancellation=new CancellationTokenSource();
        var pending=f.Coordinator.CommitAsync(c,"ready","{\"step\":2}","[\"panel-ready\"]",cancellation.Token);
        await Until(()=>f.Validator.Calls==2);cancellation.Cancel();var result=await pending;
        Check(result.Status=="cancelled"&&c.LastError is null&&(await f.Store.ReadAsync(Address(c),Expected(c))).Record!.CheckpointId==previous.CheckpointId,
            "independent cancellation changed prior checkpoint or faulted owner");
        f.Validator.Release.TrySetResult();await Until(()=>f.Coordinator.ValidatorsInFlight==0);
        Check((await f.Store.ReadAsync(Address(c),Expected(c))).Record!.CheckpointId==previous.CheckpointId,"late validation committed cancelled request");
        Console.WriteLine("Checkpoint caller cancellation PASS: previous record retained; late validation ignored");
    }
    private static async Task BlockedCancellationPropagationAsync(bool afterDecision)
    {
        var before = Signal(); var allowPublish = Signal(); int publications = 0;
        var publisher = new Publisher();
        await using var f = await Fixture.CreateAsync(publisher: publisher, timeout: TimeSpan.FromSeconds(30),
            beforePublish: async ct =>
            {
                if (Interlocked.Increment(ref publications) == 2)
                {
                    before.TrySetResult(); await allowPublish.Task;
                    Check(!ct.IsCancellationRequested, "linked token propagated before the cancellation barrier was released");
                }
            });
        var c = await f.StartAsync(); await Until(() => f.StoreState.GetState(c.InstanceScope, "after", 0) == 1);
        var previous = (await f.Store.ReadAsync(Address(c), Expected(c))).Record!;
        publisher.Hold = afterDecision;
        using var caller = new CancellationTokenSource();
        var pending = f.Coordinator.CommitAsync(c, "ready", "{\"step\":2}", "[\"panel-ready\"]", caller.Token);
        await Bound(before.Task);
        if (afterDecision)
        {
            allowPublish.TrySetResult();
            await Until(() => Volatile.Read(ref publisher.Calls) == 2);
        }
        using var releaseCallback = new ManualResetEventSlim(); var callbackEntered = Signal();
        // Registered last: Cancel sets the ORIGINAL token first, then this callback blocks
        // before the coordinator's linked-token callback can run. No timing assumptions.
        using var registration = caller.Token.Register(() => { callbackEntered.TrySetResult(); releaseCallback.Wait(); });
        var cancellation = Task.Run(() => caller.Cancel());
        try
        {
            await Bound(callbackEntered.Task);
            Check(caller.IsCancellationRequested && !c.IsBusinessClosed, "original cancellation or live owner precondition failed");
            allowPublish.TrySetResult(); publisher.Release.TrySetResult();
            await Bound(pending); var result = await pending;
            var current = (await f.Store.ReadAsync(Address(c), Expected(c))).Record!;
            Check(c.LastError is null, "independent cancellation faulted the instance");
            if (afterDecision)
                Check(result.Status == "committed" && publisher.Calls == 2 && current.State.GetProperty("step").GetInt32() == 2,
                    "cancellation after publication admission incorrectly aborted real IO");
            else
                Check(result.Status == "cancelled" && result.Error == CheckpointError.Cancelled && publisher.Calls == 1 &&
                    current.CheckpointId == previous.CheckpointId && current.State.GetProperty("step").GetInt32() == 1,
                    "cancellation before publication admitted IO or replaced the previous record");
            Console.WriteLine("Checkpoint original-token barrier PASS: " + (afterDecision ? "after decision completes actual publication" : "before decision retains old record with zero new publications"));
        }
        finally
        {
            releaseCallback.Set(); allowPublish.TrySetResult(); publisher.Release.TrySetResult();
            await Bound(cancellation);
        }
    }
    private static async Task NativeObservationStopAsync()
    {
        await using var f=await Fixture.CreateAsync();f.HoldCapture=true;var c=await f.StartAsync();
        await Bound(f.Captures[0].Entered.Task);var stop=f.Scheduler.StopTaskAsync(c.InstanceId);
        try
        {
            Check(c.TaskStatus==AutomationTaskStatus.Stopping&&!stop.IsCompleted&&!f.Captures[0].Disposed,
                "noncooperative capture lost its real work/resource permit");
            f.Captures[0].Release.TrySetResult();await Bound(stop);
            Check(c.TaskStatus==AutomationTaskStatus.Stopped&&(await f.Store.ReadAsync(Address(c),Expected(c))).Status==CheckpointReadStatus.Absent,
                "late frame revived/stored stopped source");
            Console.WriteLine("Checkpoint native-observation boundary PASS: real completion before disposal; late frame discarded");
        }
        finally{f.Captures[0].Release.TrySetResult();}
    }
    private static async Task ShippedSampleAsync()
    {
        var code=CheckpointSamples.Code();await using var f=await Fixture.CreateAsync(script:code.Source,version:"1.1.0",input:true);f.Register=false;
        f.OnReady=c=>f.NodeRegistration=f.Registry.Register(CheckpointSamples.Node(c.CheckpointMetadata!.Installation),new CheckpointSamples.GreenPanelValidator());
        var c=await f.StartAsync();await Until(()=>f.StoreState.GetState(c.InstanceScope,"checkpoint_saved",false));
        var expected=Expected(c) with{NodeId="panel-ready"};var read=await f.Store.ReadAsync(Address(c),expected);
        Check(read.Status==CheckpointReadStatus.Valid&&read.Record!.State.GetProperty("position").GetString()=="after-panel-ready"&&
            c.CheckpointMetadata!.Code.ContentHash==code.ContentHash,"shipped exact-code node/sample Lua verification failed");
        Console.WriteLine("Checkpoint shipped sample PASS: embedded code binding, actual Lua and trusted green-frame/schema validation (Fake pixels)");
    }
    private static async Task InputFreezeAsync()
    {
        await using var f=await Fixture.CreateAsync(input:true);f.Validator.Hold=true;var c=await f.StartAsync();await Bound(f.Validator.Entered.Task);
        try
        {
            var caller=await f.Scheduler.Gateway.CreateContextAsync(c.PluginId,"checkpoint-ui-test",AutomationSource.PluginUI,c.Identity);
            var request=new OperationRequest(AutomationOperation.PressKey,JsonSerializer.SerializeToElement(new{key=66,durationMs=1}),Guid.NewGuid(),"checkpoint-freeze",OperationTarget.ForRun(c.Identity),DateTimeOffset.UtcNow.AddSeconds(10));
            var result=await f.Scheduler.Gateway.ExecuteAsync(caller,request);
            Check(result.Error==OperationError.InvalidLifecycleState&&!result.OutcomeUncertain&&f.Inputs[0].KeyDownCalls==0&&
                f.StoreState.GetState(c.InstanceScope,"after",0)==0,"checkpoint freeze admitted new input/mainline");
            string timer=Path.Combine(f.Root,"plugins","timer");Directory.CreateDirectory(timer);
            await File.WriteAllTextAsync(Path.Combine(timer,"manifest.json"),"{\"id\":\"timer\",\"name\":\"Timer\",\"version\":\"1\",\"targetProcess\":\"none\",\"permissions\":[\"timer\"]}");
            await File.WriteAllTextAsync(Path.Combine(timer,"main.lua"),"function on_init() rug.set_state('alive',true) end function on_tick() rug.sleep(60000) end");
            TestAuthorizationHost.ApproveCatalog(f.Authorization,Path.Combine(f.Root,"plugins"));await f.Scheduler.RefreshPluginsAsync();
            var timerId=await f.Scheduler.StartTaskAsync("timer",0);var other=f.Scheduler.Instances.Single(x=>x.InstanceId==timerId);
            await Until(()=>f.StoreState.GetState(other.InstanceScope,"alive",false));
            Check(!other.IsBusinessClosed,"checkpoint froze unrelated instance");
            f.Validator.Release.TrySetResult();await Until(()=>f.StoreState.GetState(c.InstanceScope,"after",0)==1);
            Check((await f.Store.ReadAsync(Address(c),Expected(c))).Status==CheckpointReadStatus.Valid,"frozen checkpoint failed after verification");
            Console.WriteLine("Checkpoint freeze PASS: zero new input/early mainline; unrelated timer runs independently");
        }
        finally{f.Validator.Release.TrySetResult();}
    }
    private static async Task ConcurrentSourceAsync()
    {
        await using var f=await Fixture.CreateAsync(script:"function on_init() rug.sleep(60000) end");var c=await f.StartAsync();await Until(()=>c.Runtime.State==ScriptState.Yielded);f.Validator.Hold=true;
        var first=f.Coordinator.CommitAsync(c,"ready","{\"step\":1}","[\"panel-ready\"]");await Bound(f.Validator.Entered.Task);
        var second=f.Coordinator.CommitAsync(c,"ready","{\"step\":2}","[\"panel-ready\"]");
        try
        {
            await Until(()=>Volatile.Read(ref c.CheckpointWaiters)==2);
            Check(!second.IsCompleted&&f.Validator.Calls==1,"same-source verification ran in parallel");
            f.Validator.Release.TrySetResult();var results=await Task.WhenAll(first,second);
            Check(results[0].SubmissionSequence==1&&results[1].SubmissionSequence==2&&results.All(r=>r.Status=="committed")&&
                (await f.Store.ReadAsync(Address(c),Expected(c))).Record!.State.GetProperty("step").GetInt32()==2,"serialized source sequence/publication incorrect");
            Console.WriteLine("Checkpoint same-source concurrency PASS: admission sequence 1/2; one validation at a time; latest state retained");
        }
        finally{f.Validator.Release.TrySetResult();}
    }
}
