---
id: rug_checkpoint_startup
type: logic_node
inputs: [rug_ui_core]
outputs: [rug_ui]
tags: [checkpoint, restore, lifecycle]
---

# Checkpoint startup (06C-02)

This creates a NEW run; [06B coroutine recovery](Recovery.md) and ordinary Start
retain their semantics. [Checkpoint files](Checkpoints.md) are still format v2.
OS actions and file publication are not transactional. An uncertain action must
be re-observed, never blindly replayed because a disk record is absent. The
independent low-level Hook observation gap remains unresolved.
See the [host implementation](Services/CheckpointStartup.cs#class:TaskScheduler)
and [read-only validator contract](Abstractions/ICheckpointService.cs#interface:ICheckpointRestoreValidator).

## Host contracts

`ICheckpointStore.QueryAsync(PluginInstallation, token)` scans the existing fixed
root, bounded by store limits. It returns detached candidates for that verified
installation. Paths never come from requests. Complete attributable invalid
records show Corrupt/UnsupportedFormat; unreadable fragments with no safe
installation attribution are omitted rather than exposed across installations.
Real IO/link failures remain errors. This is not a general file browser.

`ITaskScheduler.QueryCheckpointsAsync(pluginId, token)` returns at most 128
summaries: selection ID, plugin/node, source ID, UTC time, sequence and status.
The host retains at most 256 opaque selection capabilities for five minutes.
Each pins the installation, startup code/config, record ID/sequence/canonical
fingerprint and exact trusted restore validator/node version. Ready means
compatible; SourceStillActive, RestoreUnavailable and existing compatibility
codes explain rejection. Config revision is informational; config hash decides.
Selection refreshes the catalog to pin the current manifest/defaults. It does not
restore grants/config or start a task. Invalid metadata never becomes a selection.

The trusted host calls `CreateCheckpointRestoreContextAsync(pluginId, InputTarget,
onReady?, token)` using the DevTools verified window lifetime (not just HWND).
It sends `RestoreCheckpoint` through Gateway with **only** `{selectionId}` and
an empty run target for that plugin. UI-only host source is required. Arbitrary
Start parameters cannot select restore mode. Requested capabilities plus exact
checkpoint/capture grants, caller ceilings, target and all existing policies apply.
Gateway retains its bounded two-minute process-local dedup/log contract. Equal
operation keys share a result; changed parameters conflict. One pending or active
descendant per installation/checkpoint is permitted in this scheduler, bounded to 128 entries;
cleanup removes the reservation. No cross-process exactly-once claim is made.

New `TaskExecutionContext.CheckpointOrigin` records parentInstanceId, checkpointId,
sequence and node. The run receives a new instance GUID, process-current generation,
new binding ID and independent InstanceState. No old lease/HWND/grant/state pool is
restored. Origin survives the source's absence from memory.

## Prepare, validate, commit

The existing per-plugin start gate serializes startup/reload. A candidate is an
ordinary Starting lifecycle record, addressable by Stop, with owned fresh services.
It has no driver and Gateway business admission stays closed. Capture may start;
input execution leases are NEVER acquired during preparation. Input detector
sessions are registered only after successful scene verification, before commit.
Other existing runs continue. Unexpected manual takeover still cancels input runs.

1. Recheck selection, installation, source not active, grants, exact code/config,
   node version and window lifetime.
2. Compile the PINNED source string used for contentHash; no second source is run.
3. Execute only the declaration chunk under the serialized VM with a watchdog.
   Yielded operations, state mutation and logging cannot execute in this phase.
   Missing on_restore rejects; real Lua errors retain failure semantics.
4. A separately registered `ICheckpointRestoreValidator` observes fresh owned WGC
   frames through `CheckpointObservation`. It has no input/handles/runtime access.
   Save validation is not evidence of current readiness. Missing/false/timeout
   validation cannot start Lua. Validators are bounded to 32 actual tasks; pure late
   advice is observed/discarded. Actual capture drains before disposal.
5. Re-read record fingerprint, code/manifest and startup config; recheck the pinned
   registry and target. Replaced records require reselection. Startup/reload gates
   and final live checks fence host-controlled changes; this is not an OS-wide
   filesystem/window transaction.
6. The FINAL decision is `CommitCheckpointStart` under the instance transition
   lock and scheduler shutdown gate: require Starting, no failure/Stop, ORIGINAL
   caller cancellation, execution/lifetime/grant/input-epoch/session cancellation
   sources and absolute deadline. No IO, validator or external observer runs here.
   Then publish a tracked identity, open Gateway and schedule the existing driver.

Before the decision, Stop/cancel/deadline/revocation/window loss prevents business.
Stop publishes Stopping and waits for REAL Native work/cleanup; one on_stop at most.
After the decision, request cancellation does not stop the new run: Gateway returns
its committed identity. Normal Stop, revocation and input takeover remain available.
Input requests after startup still route through Gateway/InputArbiter. Returning
manual control never revives a stopped run or automatically restores a checkpoint.

| Phase | Lifecycle / admission | Next transition |
| --- | --- | --- |
| Prepare / verify | Starting, business closed | Commit or Stop / fault |
| Committed restore hook | Starting, existing driver executes on_restore | Running after hook completes |
| Normal ticks | Running, existing lifecycle rules | Pause / Stop / explicit 06B recovery |
| Stop before or after commit | Stopping, business closed | Stopped after actual work/cleanup; Faulted for genuine failure |

## Lua

Normal startup: declaration chunk + on_init, then on_tick. Restore startup:
declaration chunk + **on_restore(checkpointJson)**, then on_tick; no on_init fallback.
The bounded JSON string carries nodeId, explicit state, completedActionIds,
sourceInstanceId/sourceRunGeneration, checkpointId/submissionSequence. Exact JSON
numeric tokens are retained; no implicit double/CLR userdata mapping. Only this
host-created path supplies restore data. on_restore can use existing async APIs.
Stop hooks remain bounded/non-yielding and genuine Lua/permission/IO/cleanup faults
are never converted to successful recovery.

## UI and controlled acceptance

Footer: 查看检查点 -> choose Ready -> bind current target in DevTools -> 从此检查点恢复.
The button explicitly confirms this request; missing grants require normal host
consent. Preparation shows progress and supports 取消准备. Selection/target changes
cancel preparation; duplicate clicks are blocked. No automatic Return control.

`checkpoint_probe` v1.1.0 is bound to the embedded shipped source. Normal startup
sends BEFORE B once, validates green and saves panel-ready. Restore validates a
NEW green frame and sends AFTER C once through on_restore. Both have five-second
script waits with detection fully active. Old v1.0.0 code checkpoints are incompatible.

```powershell
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --checkpoint-restore-tests
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --checkpoint-restore-native
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --checkpoint-restore-ui
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --checkpoint-restore-manual
```

The manual fixture owns a target OUTSIDE the restarted WinUI process. Bind green,
explicitly grant, start; expect B=(1,1), C=(0,0) and checkpoint_saved. Type `restart`
in the fixture, re-grant and REBIND. Select/confirm restore; expect C=(1,1), unchanged
B, new ID and AFTER log. `restart`, then `red`, bind/grant/select/restore must yield
SceneRejected with no additional B/C. `green` changes the controlled scene; `status`
prints only aggregate counters; `done` cleans the owned host/target/temp files.
Do not manually press B/C; use English input. Human actions still trigger takeover
once the input session starts, including in other windows. This fixture grants
nothing automatically and stores no human key sequence/trajectory.

Automated Fake, actual subprocess, Native/WGC and user manual evidence are recorded
separately. A re-created service is not a restarted process; Fake pixels in a child
are not WGC evidence. No automatic migration, compensation, checkpoint editor,
transactional hot reload, VLM or MCP service is introduced.

Stable rejection codes include SourceStillActive, CheckpointChanged (expired,
replaced or wrong-installation selection), CheckpointIncompatible (code/config/
manifest change), RestoreUnavailable (missing/changed node or missing hook), Busy,
SceneRejected and existing InvalidCaller/PermissionDenied/InputUnavailable.
Request or host verification deadlines return TimedOut/DeadlineExceeded; genuine
Lua/IO/cleanup faults return Failed/ExecutionFailed with details confined to host logs.

## Recorded agent verification (2026-10-17)

SDK 10.0.402; Debug and Release x64 builds of WinUI (including Core), script tests
and UI tests: zero warnings/errors. Both configurations passed `--checkpoint-tests`,
the complete script suite and the noninteractive UI suite (Schema/state/hot reload).
The final restore-specific suite additionally passed in both configurations after
the installation-scoped reservation change. Tests include actual Lua, a real child
process reopening the store with Fake observation, blocked cancellation propagation
before commit, post-commit cancellation, noncooperative capture drain, stale snapshots,
authorization/target invalidation, dedup, definition restrictions and corrupt files.

| Evidence stream | Result |
| --- | --- |
| Actual Native Lua/InputService/Rug.Core + fresh WGC | PASS: red rejected, green creates new run, B=(1,1) unchanged and C=(1,1); resources cleaned |
| Actual WinUI automation + host restart + WGC | PASS: two host PIDs (64016 then 68440), independently surviving target, renewed consent/binding, red rejected, green on_restore; B/C each (1,1) |
| User manual checkpoint startup | NOT RUN / pending user confirmation using the isolated command above |
| Independent low-level Hook gap | Unresolved; not retested or claimed fixed by this work |

The native-only fixture uses PostMessage and has no intervention event source;
the WinUI flow uses the production detector, but UIA is not human acceptance.
06B's prior user-confirmed single green Verify, persistent red refusal and Stop
while AwaitingHuman remain separate historical human evidence in [Recovery](Recovery.md).
