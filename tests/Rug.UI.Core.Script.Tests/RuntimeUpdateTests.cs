using System.Text.Json;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

internal static partial class CheckpointTests
{
    private const string FreezeScript = """
        function on_init() rug.log('freeze-init'); rug.set_state('init',1) end
        function on_tick()
          if not rug.get_state('history') then
            assert(rug.checkpoint.commit('ready','{"step":0}','["panel-ready"]').status == 'committed')
            rug.set_state('history',true)
          end
          if not rug.get_state('after') then
            while not rug.get_state('go') do rug.sleep(5) end
            local retained = 42
            rug.press_key(66,1); rug.set_state('before',1)
            local saved = rug.checkpoint.commit('ready','{"step":1}','["panel-ready"]')
            rug.set_state('saved',saved.status)
            assert(retained == 42 and rug.get_config('speed') == 2)
            rug.press_key(67,1); rug.set_state('after',1)
          end
          rug.sleep(60000)
        end
        function on_stop() rug.log('freeze-stop') end
        """;
    private static async Task PendingFreezeAsync(Fixture f, string suffix = "--candidate")
    {
        string dir = Path.Combine(f.Root,"plugins","probe");
        await File.WriteAllTextAsync(Path.Combine(dir,"main.lua"), FreezeScript + "\n" + suffix);
        var pipeline = new PluginUpdatePipeline();
        var candidate = await pipeline.ValidateAsync(await pipeline.PrepareAsync(dir, DateTime.UtcNow.Ticks));
        await using var lease = await f.Scheduler.AcquirePluginReloadAsync("probe");
        Check(await f.Scheduler.ClassifyPluginUpdateAsync(candidate) == PluginUpdateStatus.RuntimePending, "runtime candidate not pending");
    }
    private static async Task<OperationResult> UpdateControlAsync(Fixture f, AutomationOperation op, Guid? id = null,
        TimeSpan? timeout = null, CancellationToken token = default, Guid? operationId = null)
    {
        var caller = await f.Scheduler.CreateRuntimeUpdateContextAsync("probe");
        var parameters = id is null ? JsonSerializer.SerializeToElement(new { }) : JsonSerializer.SerializeToElement(new { updateId = id });
        return await f.Scheduler.Gateway.ExecuteAsync(caller,new(op,parameters,operationId ?? Guid.NewGuid(),"freeze-test",new("probe"),DateTimeOffset.UtcNow+(timeout??TimeSpan.FromSeconds(10))),token);
    }
    private static RuntimeUpdateSnapshot FreezeView(Fixture f, TaskExecutionContext c) => f.Scheduler.RuntimeUpdates.Query(c.CheckpointMetadata!.Installation)!;
    private static async Task<RuntimeUpdateSnapshot> PrepareFreezeAsync(Fixture f, TimeSpan? timeout = null, CancellationToken token = default)
    {
        var result = await UpdateControlAsync(f,AutomationOperation.PrepareRuntimeUpdate,timeout:timeout,token:token);
        Check(result.Status == OperationStatus.Success, "prepare failed: " + result.Error);
        return (RuntimeUpdateSnapshot)result.Value!;
    }
    public static async Task RunRuntimeUpdatesAsync()
    {
        await RunLateCheckpointResultsAsync();
        foreach (string mode in new[] { "cancel", "pause", "stop", "revoke", "takeover", "target", "supersede", "caller-cancel" }) await FreezeLifecycleAsync(mode);
        await FreezeMultipleAsync();
        foreach (string mode in new[] { "reject", "timeout", "no-validator", "io-fault", "late-stop", "late-cancel", "late-new-plan" }) await FreezePublicationAsync(mode);
        await FreezeAdmissionAsync();
        await FreezeBlockedCancellationAsync(); await FreezeRepeatedPlansAsync(); await FreezeRecoveryAdmissionAsync();
        Console.WriteLine("Runtime update freeze PASS: actual Lua BEFORE/checkpoint/AFTER, retained identity/coroutine, no publication/restart, multi-run rollback, admission, Pause/Stop/revoke/candidate/late-IO barriers and cleanup");
    }
    public static async Task RunLateCheckpointResultsAsync()
    {
        foreach (string outcome in new[] { "rejected", "timed_out", "cancelled", "committed" }) await LateCheckpointResultAsync(outcome);
        Console.WriteLine("Late checkpoint ownership PASS: actual Lua old reject/timeout/cancel/success preserve plan B; next valid submission freezes B");
    }
    private sealed class CancellableCheckpointHost(CheckpointCoordinator inner, CancellationToken token) : ICheckpointCoordinator
    {
        public bool SupportsUpdateFreeze(TaskExecutionContext context) => inner.SupportsUpdateFreeze(context);
        public Task<CheckpointResult> CommitAsync(TaskExecutionContext context,string node,string state,string actions,CancellationToken unused=default) =>
            inner.CommitAsync(context,node,state,actions,state=="{\"step\":1}"?token:unused);
    }
    private sealed class LateResultValidator(bool firstValid) : ICheckpointValidator
    {
        public readonly TaskCompletionSource Entered=Signal(), Release=Signal();
        private int _calls;
        public async Task<bool> ValidateAsync(CheckpointObservation observation,CheckpointSubmission submission,CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls)==1)
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(token);
                if (!firstValid) return false;
            }
            var frame=await observation.CaptureFreshAsync(); return frame.Pixels[1]==200;
        }
    }
    private static async Task LateCheckpointResultAsync(string outcome)
    {
        const string script="""
            function on_init() rug.set_state('init',1) end
            function on_tick()
              assert(rug.checkpoint.commit('ready','{"step":0}','["panel-ready"]').status=='committed')
              rug.set_state('history',true)
              while not rug.get_state('go') do rug.sleep(5) end
              local retained=42
              rug.press_key(66,1)
              local first=rug.checkpoint.commit('ready','{"step":1}','["panel-ready"]')
              rug.set_state('first_result',first.status)
              while not rug.get_state('second_go') do rug.sleep(5) end
              rug.press_key(66,1)
              local second=rug.checkpoint.commit('ready','{"step":2}','["panel-ready"]')
              assert(retained==42 and second.status=='committed')
              rug.press_key(67,1); rug.set_state('after',1)
              rug.sleep(60000)
            end
            """;
        using var checkpointCancellation=new CancellationTokenSource();
        await using var f=await Fixture.CreateAsync(script:script,input:true,
            timeout:outcome=="timed_out"?TimeSpan.FromSeconds(1):null,
            checkpointHost:inner=>new CancellableCheckpointHost(inner,checkpointCancellation.Token));
        await f.Scheduler.GetPluginSnapshotsAsync(); var c=await f.StartAsync();
        await Until(()=>f.StoreState.GetState(c.InstanceScope,"history",false));
        var validator=new LateResultValidator(outcome=="committed");
        f.NodeRegistration!.Dispose(); var metadata=c.CheckpointMetadata!;
        f.NodeRegistration=f.Registry.Register(new(metadata.Installation,metadata.PluginVersion,metadata.Code.ContentHash,"ready",1,Array.AsReadOnly(new[]{"panel-ready"})),validator);
        await PendingFreezeAsync(f);
        var a=await PrepareFreezeAsync(f); f.StoreState.SetState(c.InstanceScope,"go",true); await Bound(validator.Entered.Task);
        try
        {
            await UpdateControlAsync(f,AutomationOperation.CancelRuntimeUpdate,a.UpdateId);
            var b=await PrepareFreezeAsync(f);
            Check(b.Stage==RuntimeUpdateStage.WaitingCheckpoint && b.UpdateId!=a.UpdateId,"plan B did not start waiting");
            if(outcome=="cancelled") checkpointCancellation.Cancel();
            else if(outcome!="timed_out") validator.Release.TrySetResult();
            await Until(()=>f.StoreState.Contains(c.InstanceScope,"first_result"));
            Check(f.StoreState.GetState(c.InstanceScope,"first_result","")==outcome,"wrong late checkpoint outcome");
            var unchanged=FreezeView(f,c);
            Check(unchanged.UpdateId==b.UpdateId && unchanged.Stage==RuntimeUpdateStage.WaitingCheckpoint && unchanged.Revision==b.Revision &&
                unchanged.Instances.Single().Checkpoint is null && f.Scheduler.RuntimeUpdates.ActivePlanCount==1,
                "late "+outcome+" from plan A changed plan B");
            validator.Release.TrySetResult();
            await Until(()=>f.Coordinator.ValidatorsInFlight==0);
            f.StoreState.SetState(c.InstanceScope,"second_go",true);
            await Until(()=>FreezeView(f,c).Stage==RuntimeUpdateStage.Frozen);
            Check(FreezeView(f,c).UpdateId==b.UpdateId && FreezeView(f,c).Instances.Single().Checkpoint is {SubmissionSequence:3} &&
                f.Inputs[0].KeyDownCalls==2 && !f.StoreState.Contains(c.InstanceScope,"after"),"plan B did not freeze only its next valid submission");
            await UpdateControlAsync(f,AutomationOperation.CancelRuntimeUpdate,b.UpdateId);
            await Until(()=>f.StoreState.GetState(c.InstanceScope,"after",0)==1);
            Check(f.Scheduler.Instances.Single().Identity==c.Identity && f.StoreState.GetState(c.InstanceScope,"init",0)==1 &&
                f.Inputs[0].KeyDownCalls==3 && f.Inputs[0].KeyUpCalls==3,"ownership fix restarted/replayed Lua");
            await Until(()=>f.Scheduler.RuntimeUpdates.MonitorCount==0);
            Check(f.Scheduler.RuntimeUpdates.SubscriptionCount==0,"late result left plan subscriptions");
        }
        finally {validator.Release.TrySetResult();}
    }
    private static async Task FreezeLifecycleAsync(string mode)
    {
        await using var f = await Fixture.CreateAsync(script:FreezeScript,input:true);
        await f.Scheduler.GetPluginSnapshotsAsync();
        var logs = new List<string>(); f.Log.Intercept = text => { lock(logs) logs.Add(text); };
        var c = await f.StartAsync(); await Until(() => f.StoreState.GetState(c.InstanceScope,"history",false));
        // Lua already saved a historical record before arming; it must not freeze the run.
        var beforeVersion = (await f.Scheduler.GetPluginSnapshotsAsync()).Single().ContentVersion;
        await PendingFreezeAsync(f);
        using var callerCancellation = new CancellationTokenSource();
        var plan = await PrepareFreezeAsync(f,token:callerCancellation.Token);
        Check(plan.Stage == RuntimeUpdateStage.WaitingCheckpoint && plan.Instances.Count == 1 && !plan.Instances[0].Frozen &&
            f.Inputs[0].KeyDownCalls == 0 && !c.IsBusinessClosed,"prepare used history/stopped source/sent input");
        var startCaller = await f.Scheduler.Gateway.CreateContextAsync("probe","start-during-freeze",AutomationSource.PluginUI);
        var denied = await f.Scheduler.Gateway.ExecuteAsync(startCaller,new(AutomationOperation.Start,JsonSerializer.SerializeToElement(new { }),Guid.NewGuid(),"freeze-start",new("probe"),DateTimeOffset.UtcNow.AddSeconds(5)));
        Check(denied.Error == OperationError.UpdatePreparing && f.Scheduler.Instances.Count == 1 && f.Inputs.Count == 1,"new instance bypassed plan reservation");
        f.StoreState.SetState(c.InstanceScope,"go",true);
        await Until(() => FreezeView(f,c).Stage == RuntimeUpdateStage.Frozen);
        var frozen = FreezeView(f,c);
        bool tickRejected=false;
        try { await Bound(c.Runtime.BeginTickAsync()); } catch(InvalidOperationException) { tickRejected=true; }
        Check(tickRejected && c.CheckpointGate.CurrentCount==1 && c.CheckpointWaiters==0 && c.DrainOperationsAsync().IsCompleted,
            "logical freeze retained a Lua serial gate or actual IO/Native work permit");
        Check(frozen.Instances[0].Checkpoint is { SubmissionSequence:2 } receipt && receipt.Source == c.Identity && receipt.Fingerprint.Length == 64 &&
            f.Inputs[0].KeyDownCalls == 1 && f.Inputs[0].KeyUpCalls == 1 && !f.StoreState.Contains(c.InstanceScope,"after"),"frozen before actual IO/input drain or after mainline");
        var inputCaller = await f.Scheduler.Gateway.CreateContextAsync("probe","freeze-input",AutomationSource.PluginUI,c.Identity);
        var input = await f.Scheduler.Gateway.ExecuteAsync(inputCaller,new(AutomationOperation.PressKey,JsonSerializer.SerializeToElement(new{key=67,durationMs=1}),Guid.NewGuid(),"freeze-input",OperationTarget.ForRun(c.Identity),DateTimeOffset.UtcNow.AddSeconds(3)));
        Check(input.Error == OperationError.InvalidLifecycleState && !input.OutcomeUncertain && f.Inputs[0].KeyDownCalls == 1,"frozen run admitted new input");
        if(mode=="pause") { Check(f.Scheduler.PauseTask(c.InstanceId),"pause rejected while frozen"); await Until(() => c.TaskStatus == AutomationTaskStatus.Paused); }
        if(mode=="stop") await Bound(f.Scheduler.StopTaskAsync(c.InstanceId));
        else if(mode=="revoke") {f.Authorization.Revoke(c.CheckpointMetadata!.Installation,[Permission.Checkpoint]); await Bound(c.Completion);}
        else if(mode=="takeover") {await Bound(f.Arbiter.TakeOverAsync());f.Arbiter.ReturnControl();}
        else if(mode=="supersede") await PendingFreezeAsync(f,"--newer candidate");
        else if(mode=="caller-cancel") { callerCancellation.Cancel(); await Until(() => FreezeView(f,c).Stage == RuntimeUpdateStage.Cancelled); }
        else
        {
            if(mode=="target") f.Windows.Stamp++;
            Guid operation = Guid.NewGuid();
            var cancelled = await UpdateControlAsync(f,AutomationOperation.CancelRuntimeUpdate,plan.UpdateId,operationId:operation);
            var repeated = await UpdateControlAsync(f,AutomationOperation.CancelRuntimeUpdate,plan.UpdateId,operationId:operation);
            Check(cancelled.Status == OperationStatus.Success && ReferenceEquals(cancelled,repeated),"duplicate cancel did not share result");
        }
        if(mode=="pause")
        {
            Check(c.TaskStatus == AutomationTaskStatus.Paused && !f.StoreState.Contains(c.InstanceScope,"after"),"cancel bypassed user pause");
            Check(f.Scheduler.ResumeTask(c.InstanceId),"resume rejected after plan cancellation");
        }
        if(mode=="target") {await Bound(c.Completion);Check(c.TaskStatus==AutomationTaskStatus.Faulted && f.Inputs[0].KeyDownCalls==1,"invalid target received AFTER input");}
        else if(mode is "stop" or "revoke" or "takeover") Check(c.TaskStatus == AutomationTaskStatus.Stopped && f.Inputs[0].KeyDownCalls == 1 && c.LastError is null,"stop/revoke/takeover revived or faulted source");
        else
        {
            await Until(() => f.StoreState.GetState(c.InstanceScope,"after",0)==1);
            Check(f.Scheduler.Instances.Single().Identity == c.Identity && f.Inputs[0].KeyDownCalls == 2 && f.Inputs[0].KeyUpCalls == 2 &&
                f.StoreState.GetState(c.InstanceScope,"init",0)==1 && (await f.Scheduler.GetPluginSnapshotsAsync()).Single().ContentVersion == beforeVersion,"cancel restarted/replayed/published candidate");
            lock(logs) Check(logs.Count(x=>x.Contains("freeze-init"))==1 && logs.All(x=>!x.Contains("freeze-stop")),"unexpected init/stop during preparation");
        }
        await Until(() => f.Scheduler.RuntimeUpdates.ActivePlanCount == 0 && f.Scheduler.RuntimeUpdates.MonitorCount == 0);
        Console.WriteLine("Runtime freeze lifecycle PASS: " + mode);
    }
    private static async Task FreezeMultipleAsync()
    {
        await using var f = await Fixture.CreateAsync(script:FreezeScript,input:true);
        // Another plugin continues while the complete two-instance source set is reserved.
        string timer = Path.Combine(f.Root,"plugins","timer"); Directory.CreateDirectory(timer);
        await File.WriteAllTextAsync(Path.Combine(timer,"manifest.json"),"{\"id\":\"timer\",\"name\":\"Timer\",\"version\":\"1\",\"targetProcess\":\"none\",\"permissions\":[\"timer\"]}");
        await File.WriteAllTextAsync(Path.Combine(timer,"main.lua"),"function on_tick() rug.set_state('alive',true); rug.sleep(5) end");
        TestAuthorizationHost.ApproveCatalog(f.Authorization,Path.Combine(f.Root,"plugins"));
        await f.Scheduler.GetPluginSnapshotsAsync();
        var a = await f.StartAsync(); var b = await f.StartAsync(); await Until(()=>f.StoreState.GetState(a.InstanceScope,"history",false) && f.StoreState.GetState(b.InstanceScope,"history",false));
        var timerId = await f.Scheduler.StartTaskAsync("timer",0); var other=f.Scheduler.Instances.Single(r=>r.InstanceId==timerId);
        await Until(()=>f.StoreState.GetState(other.InstanceScope,"alive",false));
        await PendingFreezeAsync(f);
        Check(f.Scheduler.PauseTask(b.InstanceId),"second source pause failed");await Until(()=>b.TaskStatus==AutomationTaskStatus.Paused);
        Check((await UpdateControlAsync(f,AutomationOperation.PrepareRuntimeUpdate)).Error==OperationError.UpdateUnavailable && f.Scheduler.RuntimeUpdates.ActivePlanCount==0,
            "prepare omitted ineligible second instance");
        f.Scheduler.ResumeTask(b.InstanceId);await Until(()=>b.TaskStatus==AutomationTaskStatus.Running);
        var plan = await PrepareFreezeAsync(f,TimeSpan.FromSeconds(3));
        Check(plan.Instances.Count==2,"prepare omitted another active instance");
        f.StoreState.SetState(a.InstanceScope,"go",true);
        await Until(()=>FreezeView(f,a).Instances.Count(p=>p.Frozen)==1);
        Check(FreezeView(f,a).Stage==RuntimeUpdateStage.WaitingCheckpoint && !other.IsBusinessClosed,"partial freeze/other-plugin isolation incorrect");
        await Until(()=>FreezeView(f,a).Stage==RuntimeUpdateStage.Failed);
        await Until(()=>f.StoreState.GetState(a.InstanceScope,"after",0)==1);
        Check(FreezeView(f,a).Error==OperationError.DeadlineExceeded && !b.IsBusinessClosed && !other.IsBusinessClosed && f.Inputs[1].KeyDownCalls==0,"multi-run timeout failed to release only owned freezes");
    }
    private static async Task FreezePublicationAsync(string mode)
    {
        var publisher = new Publisher();
        await using var f=await Fixture.CreateAsync(script:FreezeScript,input:true,publisher:publisher,timeout:mode=="timeout"?TimeSpan.FromMilliseconds(100):null);
        await f.Scheduler.GetPluginSnapshotsAsync(); var c=await f.StartAsync(); await Until(()=>f.StoreState.GetState(c.InstanceScope,"history",false));
        publisher.Hold=mode.StartsWith("late-"); publisher.Fail=mode=="io-fault";
        await PendingFreezeAsync(f); var plan=await PrepareFreezeAsync(f);
        if(mode=="no-validator") f.NodeRegistration!.Dispose();
        f.Validator.Valid=mode!="reject"; f.Validator.Hold=mode=="timeout";
        f.StoreState.SetState(c.InstanceScope,"go",true);
        try
        {
            if(mode.StartsWith("late-"))
            {
                await Until(()=>publisher.Calls==2);
                Check(FreezeView(f,c).Stage==RuntimeUpdateStage.WaitingCheckpoint && f.Inputs[0].KeyDownCalls==1,"uncompleted publish counted as frozen");
                if(mode=="late-stop")
                {
                    var stop=f.Scheduler.StopTaskAsync(c.InstanceId);
                    Check(!stop.IsCompleted && c.TaskStatus==AutomationTaskStatus.Stopping && !f.Captures[0].Disposed,"noncooperative IO released live resources");
                    publisher.Release.TrySetResult(); await Bound(stop);
                    Check(c.TaskStatus==AutomationTaskStatus.Stopped && f.Inputs[0].KeyDownCalls==1,"late publish resumed stopped Lua"); return;
                }
                await UpdateControlAsync(f,AutomationOperation.CancelRuntimeUpdate,plan.UpdateId);
                if(mode=="late-new-plan") await PrepareFreezeAsync(f);
                publisher.Release.TrySetResult();
                await Until(()=>f.StoreState.GetState(c.InstanceScope,"after",0)==1);
                Check(f.Inputs[0].KeyDownCalls==2 && (mode!="late-new-plan" || FreezeView(f,c).Stage==RuntimeUpdateStage.WaitingCheckpoint),"old completion froze a new plan/replayed actions");
            }
            else
            {
                await Until(()=>FreezeView(f,c).Stage==RuntimeUpdateStage.Failed);
                if(mode=="io-fault") { await Bound(c.Completion); Check(c.TaskStatus==AutomationTaskStatus.Faulted && c.LastError is IOException,"IO fault downgraded to update cancellation"); }
                else {await Until(()=>f.StoreState.GetState(c.InstanceScope,"after",0)==1); Check(FreezeView(f,c).Instances.All(p=>!p.Frozen),"invalid checkpoint counted as frozen");}
            }
        }
        finally {publisher.Release.TrySetResult();f.Validator.Release.TrySetResult();}
    }
    private static async Task FreezeAdmissionAsync()
    {
        await using var f=await Fixture.CreateAsync(script:FreezeScript,input:true);
        await f.Scheduler.GetPluginSnapshotsAsync();var c=await f.StartAsync();await Until(()=>f.StoreState.GetState(c.InstanceScope,"history",false));await PendingFreezeAsync(f);
        var untrusted = await f.Scheduler.Gateway.CreateContextAsync("probe","schema-command",AutomationSource.PluginUI);
        var request = new OperationRequest(AutomationOperation.PrepareRuntimeUpdate,JsonSerializer.SerializeToElement(new {}),Guid.NewGuid(),"fake-host",new("probe"),DateTimeOffset.UtcNow.AddSeconds(5));
        Check((await f.Scheduler.Gateway.ExecuteAsync(untrusted,request)).Error==OperationError.InvalidCaller,"ordinary UI context forged host preparation");
        Check(f.Scheduler.PauseTask(c.InstanceId),"pause admission failed");await Until(()=>c.TaskStatus==AutomationTaskStatus.Paused);
        Check((await UpdateControlAsync(f,AutomationOperation.PrepareRuntimeUpdate)).Error==OperationError.UpdateUnavailable && c.TaskStatus==AutomationTaskStatus.Paused,"prepare changed paused run");
        Check(f.Scheduler.ResumeTask(c.InstanceId),"resume failed");await Until(()=>c.TaskStatus==AutomationTaskStatus.Running);
        f.NodeRegistration!.Dispose();
        Check((await UpdateControlAsync(f,AutomationOperation.PrepareRuntimeUpdate)).Error==OperationError.UpdateUnavailable && f.Scheduler.RuntimeUpdates.ActivePlanCount==0,"missing trusted node accepted");
    }
    private static async Task FreezeBlockedCancellationAsync()
    {
        var publisher=new Publisher();
        await using var f=await Fixture.CreateAsync(script:FreezeScript,input:true,publisher:publisher);
        await f.Scheduler.GetPluginSnapshotsAsync();var c=await f.StartAsync();await Until(()=>f.StoreState.GetState(c.InstanceScope,"history",false));
        await PendingFreezeAsync(f);using var original=new CancellationTokenSource();await PrepareFreezeAsync(f,token:original.Token);
        publisher.Hold=true;f.StoreState.SetState(c.InstanceScope,"go",true);await Until(()=>publisher.Calls==2);
        var entered=Signal();using var release=new ManualResetEventSlim();
        using var callback=original.Token.Register(()=>{entered.TrySetResult();release.Wait();});
        var cancelling=Task.Run(original.Cancel);
        try
        {
            await Bound(entered.Task);Check(original.IsCancellationRequested,"original cancellation source not set");
            publisher.Release.TrySetResult();
            await Until(()=>f.StoreState.GetState(c.InstanceScope,"after",0)==1);
            Check(FreezeView(f,c).Stage==RuntimeUpdateStage.Cancelled && f.Scheduler.RuntimeUpdates.ActivePlanCount==0 && f.Inputs[0].KeyDownCalls==2,
                "blocked cancellation propagation admitted/retained freeze");
        }
        finally {publisher.Release.TrySetResult();release.Set();await Bound(cancelling);}
    }
    private static async Task FreezeRepeatedPlansAsync()
    {
        await using var f=await Fixture.CreateAsync(script:FreezeScript,input:true);
        await f.Scheduler.GetPluginSnapshotsAsync();var c=await f.StartAsync();await Until(()=>f.StoreState.GetState(c.InstanceScope,"history",false));await PendingFreezeAsync(f);
        for(int i=0;i<12;i++)
        {
            var plan=await PrepareFreezeAsync(f);
            Check((await UpdateControlAsync(f,AutomationOperation.PrepareRuntimeUpdate)).Error==OperationError.Busy,"concurrent prepare created another plan");
            Check(f.Scheduler.RuntimeUpdates.SubscriptionCount==1,"duplicate preparation subscribed twice");
            Check((await UpdateControlAsync(f,AutomationOperation.QueryRuntimeUpdate)).Value is RuntimeUpdateSnapshot {Stage:RuntimeUpdateStage.WaitingCheckpoint},"Gateway query unavailable during preparation");
            await UpdateControlAsync(f,AutomationOperation.CancelRuntimeUpdate,plan.UpdateId);
            await Until(()=>f.Scheduler.RuntimeUpdates.MonitorCount==0);
            Check(f.Scheduler.RuntimeUpdates.ActivePlanCount==0 && f.Scheduler.RuntimeUpdates.SubscriptionCount==0 && c.TaskStatus==AutomationTaskStatus.Running && f.Inputs[0].KeyDownCalls==0,
                "plans/waiters/subscriptions accumulated or cancellation moved Lua past unsubmitted checkpoint");
        }
    }
    private static async Task FreezeRecoveryAdmissionAsync()
    {
        var archive=new UpdateArchiveBarrier();
        await using var f=await Fixture.CreateAsync(script:"function on_init() rug.agent.resolve_anomaly('update','scene') end function on_tick() rug.sleep(60000) end",agent:true,anomalyLogger:archive);
        await f.Scheduler.GetPluginSnapshotsAsync();var c=await f.StartAsync();await Bound(archive.Entered.Task);
        try
        {
            await PendingFreezeAsync(f);
            Check((await UpdateControlAsync(f,AutomationOperation.PrepareRuntimeUpdate)).Error==OperationError.UpdateUnavailable && c.TaskStatus==AutomationTaskStatus.Recovering,"prepare disturbed recovery");
            archive.Release.TrySetResult();await Until(()=>c.TaskStatus==AutomationTaskStatus.AwaitingHuman);
            Check((await UpdateControlAsync(f,AutomationOperation.PrepareRuntimeUpdate)).Error==OperationError.UpdateUnavailable && c.TaskStatus==AutomationTaskStatus.AwaitingHuman,"prepare disturbed human recovery wait");
        }
        finally{archive.Release.TrySetResult();}
    }
}
