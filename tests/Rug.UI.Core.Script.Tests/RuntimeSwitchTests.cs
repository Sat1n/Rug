using System.Text.Json;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

internal static partial class CheckpointTests
{
    private const string SwitchTarget = """
        function on_init() error('new on_init must not execute') end
        function on_restore(json)
          assert(string.find(json,'"step":1',1,true))
          assert(rug.get_config('speed')==2)
          assert(rug.get_state('init')==nil)
          rug.press_key(68,1)
          rug.set_state('restored',1)
        end
        function on_tick() rug.sleep(60000) end
        function on_stop() rug.log('new-stop') end
        """;
    private sealed class SwitchValidator : ICheckpointRestoreValidator
    {
        public int Calls, RejectAt;
        public int HoldAt { get; set; }
        public readonly TaskCompletionSource Entered=Signal(), Release=Signal();
        public async Task<bool> ValidateAsync(CheckpointObservation observation,CheckpointRecord record,CancellationToken token)
        {
            int call=Interlocked.Increment(ref Calls);
            if(call==HoldAt) {Entered.TrySetResult(); await Release.Task;}
            token.ThrowIfCancellationRequested();
            var frame=await observation.CaptureFreshAsync();
            return call!=RejectAt && frame.Pixels[1]==200;
        }
    }
    public static async Task RunRuntimeSwitchesAsync()
    {
        foreach(string mode in new[]{"success","double","pause","no-rule","hash","node","actions","config-rule","permissions",
            "definition","missing-hook","second-scene","post-scene","stop-fault","restore-fault","cancel-pre","cancel-post",
            "stop-pre","stop-post","revoke","target","supersede","disk-config","live-config","record","timeout","blocked-cancel",
            "concurrent","rule-revoked","native-pre","native-post","ui-build","ui-publish","pause-cancel","double-restore-fault","takeover-after",
            "supersede-post","revert-post","validator-timeout","ui-cleanup-fault","shutdown-commit","observations"})
        {
            await RuntimeSwitchAsync(mode); Console.WriteLine("Runtime switch scenario PASS: "+mode);
        }
        Console.WriteLine("Runtime switch PASS: actual Lua group AFTER-only, immutable receipts, direct compatibility, staged rollback, irreversible stop, pause inheritance, request races and cleanup");
    }
    public static async Task RunRuntimeSwitchRaceAsync(string mode)
    { await RuntimeSwitchAsync(mode); Console.WriteLine("Runtime switch race PASS: "+mode); }
    private static async Task RuntimeSwitchAsync(string mode)
    {
        string old=mode=="stop-fault"?FreezeScript.Replace("rug.log('freeze-stop')","error('old stop fault')"):FreezeScript;
        if(mode=="observations")old=old.Replace("rug.set_state('init',1)","rug.set_state('init',1); local f=rug.capture(); rug.set_state('old_frame',f.observation.frameId)");
        await using var f=await Fixture.CreateAsync(script:old,input:true);
        await f.Scheduler.GetPluginSnapshotsAsync();
        var sources=new List<TaskExecutionContext>{await f.StartAsync()};
        if(mode is "double" or "second-scene" or "double-restore-fault" or "supersede-post" or "revert-post" or "ui-cleanup-fault") sources.Add(await f.StartAsync());
        await Until(()=>sources.All(s=>f.StoreState.GetState(s.InstanceScope,"history",false)));
        string oldFrame=f.StoreState.GetState(sources[0].InstanceScope,"old_frame","");
        var effective=(await f.Scheduler.GetPluginSnapshotsAsync()).Single();
        string target=mode switch {"definition"=>"rug.set_state('illegal',true)\n"+SwitchTarget,"missing-hook"=>"function on_init() end",
            "restore-fault"=>SwitchTarget.Replace("rug.press_key(68,1)","error('restore fault')"),_=>SwitchTarget};
        if(mode=="double-restore-fault") target=SwitchTarget.Replace("rug.press_key(68,1)","if string.find(json,'\"sourceRunGeneration\":2',1,true) then error('second restore fault') end\n rug.press_key(68,1)");
        if(mode=="takeover-after") target=SwitchTarget.Replace("rug.set_state('restored',1)","rug.set_state('restored',1); rug.sleep(60000)");
        if(mode=="observations")target=target.Replace("rug.press_key(68,1)","local f=rug.capture(); rug.set_state('new_frame',f.observation.frameId); rug.set_state('frame_owner',f.observation.run.instanceId); rug.press_key(68,1)");
        if(mode=="ui-cleanup-fault") target="""
            function on_init() error('on_init replay') end
            function on_restore(json)
              if string.find(json,'"sourceRunGeneration":2',1,true) then
                rug.set_state('waiting_restore',true)
                while not rug.get_state('release_restore') do rug.sleep(1) end
                rug.press_key(68,1)
              end
              rug.set_state('restored',1)
            end
            function on_tick()
              while not rug.get_state('fail_tick') do rug.sleep(1) end
              error('post-restore Lua fault while peer restoring')
            end
            function on_stop() rug.log('new-stop') end
            """;
        string dir=Path.Combine(f.Root,"plugins","probe");
        await File.WriteAllTextAsync(Path.Combine(dir,"main.lua"),target);
        if(mode=="permissions")
        {
            string path=Path.Combine(dir,"manifest.json");
            var manifest=JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{id=manifest.Id,name=manifest.Name,version=manifest.Version,targetProcess="test",permissions=manifest.Permissions.Append(Permission.Agent)}));
        }
        var pipeline=new PluginUpdatePipeline();
        var candidate=await pipeline.ValidateAsync(await pipeline.PrepareAsync(dir,1));
        Check(await f.Scheduler.ClassifyPluginUpdateAsync(candidate)==PluginUpdateStatus.RuntimePending,"switch candidate not pending");
        var validator=new SwitchValidator{RejectAt=mode=="second-scene"?2:mode=="post-scene"?2:0};
        if(mode=="validator-timeout") {validator.HoldAt=1;f.Scheduler.CheckpointStartValidationTimeout=TimeSpan.FromMilliseconds(50);}
        var metadata=sources[0].CheckpointMetadata!;
        var rule=new RuntimeUpdateCompatibility("test-direct",metadata.Installation,effective.ContentVersion,candidate.ContentVersion,
            effective.Manifest.Version,candidate.Manifest.Version,effective.EntryCode.ContentHash,candidate.EntryCode.ContentHash,"ready",1,metadata.ConfigHash,Array.AsReadOnly(new[]{"panel-ready"}));
        rule=mode switch {"hash"=>rule with {TargetHash="wrong"},"node"=>rule with {StateVersion=2},"actions"=>rule with {CompletedActionIds=Array.AsReadOnly(new[]{"other"})},"config-rule"=>rule with {ConfigHash="wrong"},_=>rule};
        using var registration=mode=="no-rule"?null:f.UpdateRules.Register(rule,validator);
        var plan=await PrepareFreezeAsync(f,TimeSpan.FromSeconds(40));
        foreach(var source in sources) f.StoreState.SetState(source.InstanceScope,"go",true);
        await Until(()=>FreezeView(f,sources[0]).Stage==RuntimeUpdateStage.Frozen);
        plan=FreezeView(f,sources[0]);
        var originalFiles=Directory.GetFiles(f.LogRoot,"*.json",SearchOption.AllDirectories).ToDictionary(p=>p,File.ReadAllBytes);
        var entered=Signal();var release=Signal();
        var stage=new HeldSwitchUi(mode=="shutdown-commit");
        using var heldUi=mode is "ui-cleanup-fault" or "shutdown-commit" ?
            f.Scheduler.RuntimeUpdates.RegisterUiHandler("probe",(_,_)=>Task.FromResult<IPluginUiUpdate>(stage)):null;
        using var ui=mode is "ui-build" or "ui-publish"?f.Scheduler.RuntimeUpdates.RegisterUiHandler("probe",(_,_)=>
            mode=="ui-build"?Task.FromException<IPluginUiUpdate>(new InvalidDataException("candidate UI type conflict")):Task.FromResult<IPluginUiUpdate>(new FailingSwitchUi())):null;
        bool barrier=mode is "pause" or "cancel-pre" or "cancel-post" or "stop-pre" or "stop-post" or "revoke" or "target" or "supersede" or "disk-config" or "live-config" or "record" or "timeout" or "blocked-cancel" or "concurrent" or "rule-revoked" or "pause-cancel" or "supersede-post" or "revert-post";
        if(barrier)
        {
            Func<Task> wait=async()=>{entered.TrySetResult();await release.Task;};
            if(mode is "cancel-post" or "stop-post" or "blocked-cancel" or "supersede-post" or "revert-post") f.Scheduler.BeforeUpdateGroupCommit=wait;
            else f.Scheduler.BeforeUpdateSourceStop=wait;
        }
        using var cancellation=new CancellationTokenSource();
        if(mode=="native-pre") f.HoldCapture=true;
        if(mode=="native-post") f.Scheduler.BeforeUpdateSourceStop=()=>{f.Captures.Last().Hold=true;return Task.CompletedTask;};
        var caller=await f.Scheduler.CreateRuntimeUpdateContextAsync("probe");
        var request=new OperationRequest(AutomationOperation.SwitchRuntimeUpdate,JsonSerializer.SerializeToElement(new{updateId=plan.UpdateId,candidateVersion=plan.CandidateVersion,revision=plan.Revision}),
            Guid.NewGuid(),"switch-test",new("probe"),DateTimeOffset.UtcNow.AddSeconds(mode=="timeout"?2:30));
        var task=f.Scheduler.Gateway.ExecuteAsync(caller,request,cancellation.Token);
        Task<OperationResult>? concurrent=null;
        Task? shutdown=null;
        CancellationTokenRegistration shutdownCallback=default;
        var unblockShutdown=Signal();
        try
        {
            if(mode=="shutdown-commit")
            {
                await Bound(stage.Entered.Task);
                // Block the ORIGINAL source before any linked-token callbacks can propagate.
                var shutdownSource=(CancellationTokenSource)typeof(Rug.UI.Core.Services.TaskScheduler)
                    .GetField("_shutdown",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Scheduler)!;
                var shutdownEntered=Signal();
                shutdownCallback=shutdownSource.Token.Register(()=>{shutdownEntered.TrySetResult();unblockShutdown.Task.GetAwaiter().GetResult();});
                shutdown=Task.Run(async()=>await f.Scheduler.DisposeAsync());
                await Bound(shutdownEntered.Task);
                Check(f.Scheduler.Instances.Where(c=>!sources.Contains(c)).All(c=>!c.LifetimeToken.IsCancellationRequested),
                    "shutdown barrier did not isolate pre-propagation publication");
                stage.Release.TrySetResult();
                var stoppedResult=await task.WaitAsync(TimeSpan.FromSeconds(8));
                Check(stoppedResult.Status!=OperationStatus.Success,"host shutdown accepted but group committed");
                Check(FreezeView(f,sources[0]).Stage!=RuntimeUpdateStage.Committed && f.Inputs.Skip(sources.Count).All(i=>i.KeyDownCalls==0),
                    "host shutdown published or executed replacement AFTER");
                unblockShutdown.TrySetResult();await Bound(shutdown);
                Check(f.Scheduler.Instances.Count==0 && f.Captures.All(c=>c.Disposed),"shutdown candidate resources leaked");
                return;
            }
            if(mode=="ui-cleanup-fault")
            {
                Check((await task.WaitAsync(TimeSpan.FromSeconds(8))).Status==OperationStatus.Success,"race setup did not commit");
                await Bound(stage.Entered.Task); // Group open; worker held in UI disposal.
                var replacements=f.Scheduler.Instances.ToArray();
                var a=replacements.Single(c=>c.CheckpointOrigin?.ParentInstanceId==sources[0].InstanceId);
                var b=replacements.Single(c=>c.CheckpointOrigin?.ParentInstanceId==sources[1].InstanceId);
                await Until(()=>a.FirstHookCompleted.Task.IsCompletedSuccessfully && f.StoreState.GetState(b.InstanceScope,"waiting_restore",false));
                f.StoreState.SetState(a.InstanceScope,"fail_tick",true);
                await Until(()=>a.LastError is not null);
                await Bound(b.Completion); // Peer safe stop must not wait for UI disposal to finish.
                // A's retained first-hook success must not hide its subsequent Lua fault.
                stage.Release.TrySetResult();
                await Until(()=>FreezeView(f,sources[0]).Stage is RuntimeUpdateStage.NeedsHuman or RuntimeUpdateStage.RestoreCompleted);
                Check(FreezeView(f,sources[0]).Stage==RuntimeUpdateStage.NeedsHuman,"faulted group reported RestoreCompleted");
                await Bound(b.Completion);
                Check(a.LastError is not null && b.LastError is null && b.TaskStatus==AutomationTaskStatus.Stopped &&
                    f.Scheduler.Instances.Count==0 && f.Captures.All(c=>c.Disposed),"group fault lost real error, faulted peer cancellation or leaked resources");
                Check(f.Inputs.Take(sources.Count).All(i=>i.KeyDownCalls==1) && f.Inputs.Skip(sources.Count).All(i=>i.KeyDownCalls==0),"group race replayed business input");
                await Until(()=>f.Scheduler.RuntimeUpdates.MonitorCount==0 && f.Scheduler.RuntimeUpdates.ActivePlanCount==0);
                Check(f.Scheduler.RuntimeUpdates.SubscriptionCount==0,"group race leaked plan subscriptions");
                return;
            }
            if(mode is "native-pre" or "native-post")
            {
                await Until(()=>f.Captures.Count>sources.Count);
                var capture=f.Captures.Last(); await Bound(capture.Blocked.Task);
                cancellation.Cancel(); await task;
                Check(!capture.Disposed && f.Scheduler.Instances.Any(c=>!sources.Contains(c)),"noncooperative capture resource freed early");
                Check(f.Inputs.Skip(sources.Count).All(i=>i.KeyDownCalls==0),"blocked native capture opened business");
                capture.Release.TrySetResult();
            }
            if(barrier)
            {
                await Bound(entered.Task);
                Check(f.Inputs.Skip(sources.Count).All(i=>i.KeyDownCalls==0),"candidate input before commit");
                int services=f.Inputs.Count;
                try {await f.Scheduler.StartTaskAsync("probe",123);throw new InvalidOperationException("Switch did not reserve starts");}
                catch(AutomationGateway.GatewayRejectedException error) when(error.Error==OperationError.UpdatePreparing){}
                Check(f.Inputs.Count==services,"blocked start created services");
                if(mode is "pause" or "pause-cancel") Check(f.Scheduler.PauseTask(sources[0].InstanceId),"pause during freeze refused");
                if(mode=="pause-cancel") cancellation.Cancel();
                if(mode=="rule-revoked") registration!.Dispose();
                if(mode=="concurrent")
                {
                    concurrent=f.Scheduler.Gateway.ExecuteAsync(caller,request with {OperationId=Guid.NewGuid()});
                    Check(f.Inputs.Count==2,"concurrent switch created another candidate");
                }
                if(mode is "cancel-pre" or "cancel-post") cancellation.Cancel();
                if(mode is "stop-pre" or "stop-post") await f.Scheduler.StopTaskAsync(f.Scheduler.Instances.First(c=>!sources.Contains(c)).InstanceId);
                if(mode=="revoke") f.Authorization.Revoke(metadata.Installation,[Permission.ControlInput]);
                if(mode=="target") f.Windows.Valid=false;
                if(mode is "supersede" or "supersede-post")
                {
                    await File.AppendAllTextAsync(Path.Combine(dir,"main.lua"),"\n--newer");
                    var newer=await pipeline.ValidateAsync(await pipeline.PrepareAsync(dir,2));
                    await f.Scheduler.ClassifyPluginUpdateAsync(newer);
                }
                if(mode=="revert-post")
                    Check(await f.Scheduler.ClassifyPluginUpdateAsync(effective)==PluginUpdateStatus.Unchanged,"effective revert did not cancel switch");
                if(mode=="disk-config") await File.WriteAllTextAsync(Path.Combine(dir,"config.json"),"[]");
                if(mode=="live-config") f.Bus.SetState(StateScope.Plugin("probe"),"speed",3);
                if(mode=="record") await File.WriteAllTextAsync(originalFiles.Keys.First(p=>File.ReadAllText(p).Contains(sources[0].InstanceId.ToString())),"{}");
                if(mode=="timeout") await task; // Real request deadline, while the decision is deterministically blocked.
                if(mode=="blocked-cancel")
                {
                    var blocked=Signal();var unblock=Signal();
                    using var callback=cancellation.Token.Register(()=>{blocked.TrySetResult();unblock.Task.GetAwaiter().GetResult();});
                    var cancel=Task.Run(cancellation.Cancel); await Bound(blocked.Task); release.TrySetResult();
                    await Until(()=>FreezeView(f,sources[0]).Stage==RuntimeUpdateStage.NeedsHuman);
                    unblock.TrySetResult();await cancel;
                }
                release.TrySetResult();
            }
            var result=await task;
            if(mode is "success" or "double" or "pause" or "restore-fault" or "double-restore-fault" or "takeover-after" or "concurrent" or "observations")
            {
                Check(result.Status==OperationStatus.Success && result.Value is RuntimeUpdateSnapshot{Stage:RuntimeUpdateStage.Committed},"switch did not commit: "+result.Error+"/"+FreezeView(f,sources[0]).Reason);
                var snapshot=(RuntimeUpdateSnapshot)result.Value!;
                if(concurrent is not null) Check((await concurrent).Value==result.Value,"concurrent switch returned a different group");
                Check(snapshot.Instances.All(p=>p.Replacement is not null),"missing replacement identities");
                Check(sources.All(s=>s.TaskStatus==AutomationTaskStatus.Stopped && f.Inputs[sources.IndexOf(s)].KeyDownCalls==1),"old tail ran or source not stopped");
                if(mode=="pause")
                {
                    var paused=f.Scheduler.Instances.Single(); await Until(()=>paused.TaskStatus==AutomationTaskStatus.Paused);
                    Check(f.Inputs.Last().KeyDownCalls==0 && FreezeView(f,sources[0]).Stage==RuntimeUpdateStage.Committed,"inherited pause executed restore");
                    Check(f.Scheduler.ResumeTask(paused.InstanceId),"new paused run did not resume");
                }
                if(mode=="takeover-after")
                {
                    await Until(()=>f.Scheduler.Instances.Any(c=>f.StoreState.GetState(c.InstanceScope,"restored",0)==1));
                    var running=f.Scheduler.Instances.Single(); await f.Arbiter.TakeOverAsync();
                    Check(running.TaskStatus==AutomationTaskStatus.Stopped && running.LastError is null,"normal postcommit takeover became Faulted");
                    f.Arbiter.ReturnControl(); Check(f.Scheduler.Instances.Count==0,"Return control revived switched run");
                }
                bool failedRestore=mode is "restore-fault" or "double-restore-fault" or "takeover-after";
                await Until(()=>FreezeView(f,sources[0]).Stage==(failedRestore?RuntimeUpdateStage.NeedsHuman:RuntimeUpdateStage.RestoreCompleted));
                if(!failedRestore)
                {
                    foreach(var progress in snapshot.Instances)
                    {
                        var restored=f.Scheduler.Instances.Single(c=>c.Identity==progress.Replacement);
                        Check(restored.CheckpointOrigin?.ParentInstanceId==progress.Identity.InstanceId && restored.RuntimeUpdateOrigin?.UpdateId==plan.UpdateId &&
                            restored.RuntimeUpdateOrigin!.CheckpointFingerprint==progress.Checkpoint!.Fingerprint && restored.InstanceId!=progress.Identity.InstanceId &&
                            restored.RunGeneration>progress.Identity.RunGeneration && restored.TargetBindingId!=progress.Identity.TargetBindingId,"wrong new source/identity metadata");
                        Check(f.StoreState.GetState(restored.InstanceScope,"restored",0)==1 && !f.StoreState.Contains(restored.InstanceScope,"init"),"restore state/init replay");
                        if(mode=="observations")Check(oldFrame.Length>0 && f.StoreState.GetState(restored.InstanceScope,"new_frame","")!=oldFrame &&
                            f.StoreState.GetState(restored.InstanceScope,"frame_owner","")==restored.InstanceId.ToString(),"switched runtime inherited old frame identity");
                    }
                    Check(f.Inputs.Skip(sources.Count).All(i=>i.KeyDownCalls==1 && i.KeyUpCalls==1),"AFTER not once per replacement");
                }
                int created=f.Inputs.Count;
                var replay=await f.Scheduler.Gateway.ExecuteAsync(caller,request);
                Check(replay==result && f.Inputs.Count==created,"same request replay executed again");
                var duplicate=await f.Scheduler.Gateway.ExecuteAsync(caller,request with {OperationId=Guid.NewGuid()});
                Check(duplicate.Status==OperationStatus.Success && f.Inputs.Count==created,"same plan created a second group");
                var conflict=await f.Scheduler.Gateway.ExecuteAsync(caller,request with {Parameters=JsonSerializer.SerializeToElement(new{updateId=plan.UpdateId,candidateVersion="other",revision=plan.Revision})});
                Check(conflict.Error==OperationError.DeduplicationConflict,"operation parameter conflict lost");
            }
            else
            {
                Check(result.Status!=OperationStatus.Success,"failed switch reported success: "+mode);
                if(mode is "stop-fault" or "definition" or "ui-build" or "ui-publish")
                    Check(result.Status==OperationStatus.Failed && result.Error==OperationError.ExecutionFailed,"real failure was mapped to a rejection: "+mode);
                if(mode=="validator-timeout") Check(result.Status==OperationStatus.TimedOut && result.Error==OperationError.DeadlineExceeded,"validator timeout was mapped to cancellation");
                await Until(()=>f.Scheduler.RuntimeUpdates.ActivePlanCount==0);
                bool irreversible=mode is "post-scene" or "stop-fault" or "cancel-post" or "stop-post" or "blocked-cancel" or "native-post" or "ui-publish" or "supersede-post" or "revert-post";
                if(irreversible)
                    Check(sources.All(s=>s.IsBusinessClosed) && f.Inputs.Take(sources.Count).All(i=>i.KeyDownCalls==1),"irreversible failure released old tail");
                else if(mode is not ("revoke" or "target"))
                {
                    if(mode=="pause-cancel")
                    {
                        await Until(()=>sources[0].TaskStatus==AutomationTaskStatus.Paused);
                        Check(f.Inputs[0].KeyDownCalls==1,"switch cancellation crossed user Pause");
                        Check(f.Scheduler.ResumeTask(sources[0].InstanceId),"old paused coroutine could not resume after cancelled switch");
                    }
                    await Until(()=>sources.All(s=>f.StoreState.GetState(s.InstanceScope,"after",0)==1));
                }
                Check((await f.Scheduler.GetPluginSnapshotsAsync()).Single().ContentVersion==effective.ContentVersion,"failure published candidate");
                Check(f.Inputs.Skip(sources.Count).All(i=>i.KeyDownCalls==0),"failed precommit emitted input");
            }
            if(mode!="record")foreach(var file in originalFiles)Check(File.ReadAllBytes(file.Key).SequenceEqual(file.Value),"update rewrote original checkpoint");
            await Until(()=>f.Scheduler.RuntimeUpdates.MonitorCount==0);
            Check(f.Scheduler.RuntimeUpdates.SubscriptionCount==0,"switch subscriptions leaked");
        }
        finally
        {
            release.TrySetResult();validator.Release.TrySetResult();stage.Release.TrySetResult();unblockShutdown.TrySetResult();
            if(shutdown is not null) await Bound(shutdown);
            shutdownCallback.Dispose();
        }
    }
    private sealed class HeldSwitchUi(bool holdCommit) : IPluginUiUpdate
    {
        public readonly TaskCompletionSource Entered=Signal(),Release=Signal();
        public async Task CommitAsync(CancellationToken token)
        {if(holdCommit){Entered.TrySetResult();await Release.Task;}}
        public Task RollbackAsync()=>Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {if(!holdCommit){Entered.TrySetResult();await Release.Task;}}
    }
    private sealed class FailingSwitchUi : IPluginUiUpdate
    {
        public Task CommitAsync(CancellationToken token)=>Task.FromException(new IOException("UI publish failed"));
        public Task RollbackAsync()=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
