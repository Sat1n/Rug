---
id: rug_runtime_switch
type: logic_node
inputs: [rug_ui_core, rug_runtime_update_freeze]
outputs: [rug_ui]
tags: [plugins, checkpoint, update, lifecycle]
---

# Explicit compatible runtime switching (3.5-07B-02)

The [host worker](Services/RuntimeUpdateSwitch.cs#class:TaskScheduler) consumes an
already [frozen plan](RuntimeUpdates.md). It never edits a source checkpoint or
replays its completed steps. Ordinary [checkpoint startup](CheckpointStartup.md)
still requires exact content/version/config and an ended source; its public
selection/SourceStillActive checks have not been relaxed.

## Trusted compatibility and API

The composition root injects
[IRuntimeUpdateCompatibilityRegistry](Abstractions/IRuntimeUpdateCompatibilityRegistry.cs#interface:IRuntimeUpdateCompatibilityRegistry).
[RuntimeUpdateCompatibilityRegistry](Services/RuntimeUpdateCompatibilityRegistry.cs#class:RuntimeUpdateCompatibilityRegistry)
has at most 128 explicitly registered direct-continuation rules. A rule binds
ruleId, verified installation, exact source/target snapshot content versions,
plugin versions, loaded entry-code hashes, node/state version, configHash and
completed action IDs. State structure and action meanings must remain identical;
there is no migration callback. Registry withdrawal invalidates a host token that
is also checked directly at final commit. Requests, Lua and plugin state cannot
register rules or manufacture approval. No rule means UpdateIncompatible.

Use `CreateRuntimeUpdateContextAsync(pluginId)` and Gateway
`SwitchRuntimeUpdate { updateId, candidateVersion, revision }`, with the usual
host-only plugin target, operationId, correlationId and deadline. Additional
fields, paths, permissions and skip flags are rejected. Admission requires the
whole exact source set Frozen under this plan and current explicit grants,
including checkpoint and capture for fresh scene observation.
Permissions must be unchanged; configHash must equal each source's checkpoint
and current candidate configuration with PluginConfig overrides. Source records
are reread with original compatibility and compared by ID, sequence and canonical
fingerprint. Candidate files are never reloaded for execution; disk rereads only
invalidate a changed selection. No implicit migration/regrant/config restore.

One switch worker/result is retained per plan. Concurrent requests share it;
same operationId/changed parameters still conflict in Gateway. Different request
IDs for the same plan cannot create a second group, including after a committed
restore fault. Gateway's two-minute dedup and eight-slot/ten-minute lazy plan
retention are process-local, not cross-process exactly-once guarantees. All
replacement identities remain in the bounded plan summary even after cleanup.

## Stages and decisions

| Stage | Business execution / cancellation |
| --- | --- |
| Frozen | Sources held; cancel releases the original checkpoint call |
| PreparingSwitch | New independent Starting candidates, declaration-only; cancellation can still release sources |
| StoppingSources | Irreversible source-stop decision won; cancellation cannot release any old coroutine |
| ValidatingStopped | All old cleanup complete; fresh scene is checked again |
| Committed | One group publication won; new on_restore may execute, or wait for inherited user Pause |
| RestoreCompleted | Every replacement's first on_restore hook completed |
| Failed/Cancelled before source stop | Candidate resources cleaned; source holds released, preserving user Pause |
| NeedsHuman after source stop | Old runs remain genuinely stopped/faulted; candidate/new group safely stopped; no replay/retry |

Preparation does not hold the plugin startup semaphore across validators or IO.
The plan continues blocking ordinary/checkpoint starts before service creation;
query, Stop and pre-stop cancel remain available. Candidates are distinct,
queryable/stoppable Starting instances. Each owns capture/input/runtime/state,
configuration and new instance/generation/binding IDs, using the original
host-verified window lifetime rather than only its HWND. No input session/lease
or Lua business is started during declaration or observation. Top-level restore
definitions permit declarations/read-only reads; existing Lua restrictions reject
state mutation, logging, business APIs and checkpoint submission there.

Trusted read-only validators capture fresh frames before old Stop **and again
after actual source cleanup**. Save-time observations are not evidence for either
check. Validation uses independent work permits and bounded deadlines (existing
10-second default, capped by request and preparation deadline), <=32 validators,
no input tools. Late/noncooperative proposals are observed and cannot commit;
actual native observations drain before handles are released. Permission, Lua,
capture, IO and cleanup failures retain real error/fault semantics.

The source-stop decision is under the plan lock after full-group rechecks. It
detaches ordinary source lifecycle failure handlers so the update's own Stop
cannot release frozen peers. Cancel, deadline, candidate replacement, effective
content reversion and host shutdown after this point signal failure but retain
the reservation/holds until the complete old group has actually stopped.
Source Pause is captured atomically with source business closure and transferred
to the corresponding new run. Normal Stop still waits real work/releases,
bounded on_stop and all resource cleanup. Failure of one cleanup never skips peers.

Final checks revalidate plan, fixed files/config, unchanged original checkpoint,
registered rule, current grants, target/control, original caller cancellation and
deadline. Staged UI builds happen during preparation; failed construction retains
the old tree/subscriptions/config. UI commit/rollback/disposal runs outside
transition locks; defaults and old subscription disposal happen only on success.

Group publication holds the short plugin/catalog transition lease and fixed-order
candidate lifecycle locks, then the plan lock. It directly checks raw cancellation,
deadline, candidate status, grant/input epochs and rule tokens, publishes the
fixed catalog snapshot, marks the complete candidate group prepared, records the
committed result, then opens one shared business gate. Only assignments/token
checks/signals happen there, never IO, validation, UI or external notification.
Before the gate opens, even externally submitted candidate requests cannot enter
business work. Notifications run after those locks are released.

After group commit request cancellation cannot convert the result into “nothing
happened”; Gateway returns the retained committed snapshot. Query distinguishes
commit from hook completion. A new hook fault or Stop before group restoration
finishes safely stops its peers and reports NeedsHuman; normal Stop/takeover stays
cancellation, not a fabricated Lua fault. No old instance is revived. Restored
runs then follow the existing Gateway/input-arbiter/grant/takeover paths.

## Origin and Lua data

`TaskExecutionContext.CheckpointOrigin` records parent instance/checkpoint/sequence/
node. Separate `RuntimeUpdateOrigin` records updateId, ruleId, source checkpoint
fingerprint and source/target snapshot versions. Startup code metadata describes
the actual **new** loaded code; the original checkpoint still describes old code.
The existing bounded JSON `on_restore(checkpointJson)` input contains only explicit
business state/actions and source information. New InstanceState starts empty;
on_init never runs, on_restore supports existing async APIs, then normal ticks
begin. An inherited Pause blocks the first restore instruction until Resume.

Stable failures include InvalidParameters, UpdateSuperseded (selection/recorded
candidate changed), UpdateUnavailable (not fully frozen), UpdateIncompatible
(rule/config/permission/action incompatibility), CheckpointChanged, SceneRejected,
PermissionDenied, InputUnavailable, InvalidLifecycleState, DeadlineExceeded,
Cancelled and ExecutionFailed. Actual execution/cleanup errors map to Failed,
validation deadline expiry to TimedOut, and compatibility/admission failures to
Rejected; a previously committed result remains committed. Cancel after the stop decision returns
UpdateIrreversible; explicit Stop remains available. Failed requests retain their
plan stage/reason; internal exceptions are logged, not exposed as adapter stacks.

## Verification and manual steps

```powershell
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --runtime-switch-tests
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --runtime-switch-ui
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --runtime-switch-manual
```

Build the Debug/x64 app first for the two UI modes. The manual fixture owns its
green/red window, temporary update_probe installation/record root and WinUI process:

1. Bind its green window in DevTools; select **Update freeze probe**, Start bound
   task, explicitly Allow permissions. Logs show old INIT and BEFORE/AFTER cycles.
2. In the fixture terminal type `update` once. The exact embedded v2 candidate is
   installed. In WinUI wait for pending, choose Prepare, then Frozen (not switched).
3. Optionally Pause the frozen source. Choose Switch and explicitly confirm the
   irreversible warning. Verify old STOP once and new identity/generation in the
   summary. A paused replacement must show Committed without new AFTER until Resume.
4. Expect exactly one `[update-v2] AFTER only`, no new INIT, no old frozen tail.
5. Use a fresh fixture for failure: green -> freeze -> terminal `red` -> Switch.
   Expect visible SceneRejected and no v2 business. Type `green` promptly; old
   coroutine may continue normally. Stop normally; `done` cleans only owned resources.

The sample grants no input capability, allowing host UI inspection without
bypassing the unchanged global external-input policy. The source and target are
pinned to embedded files; editing them cannot inherit the trusted rule. The
automatic suite uses actual Lua/real files with Fake input and controlled barriers;
WinUI tests separately exercise real WGC, confirmation, new identities, pause
inheritance and red-scene rejection. Neither is new real-human input acceptance.

No cross-version state transformation, automatic retry/compensation, rollback of
OS effects or cross-process switch transaction is provided. The checkpoint and
an OS action are not atomic; unknown results require new observation. Independent
Hook observation limitations remain unresolved; input detection/classification is
unchanged. Human manual confirmation is recorded separately after user execution.

## Recorded agent verification (2026-10-20)

SDK 10.0.402. All six managed projects, including WinUI, built Debug/Release x64
with zero warnings/errors. Both configurations passed the complete script suite
(39 new switch scenarios plus existing 07A/07B-01 ownership/lifecycle/recovery/
checkpoint/Gateway/grants/arbitration/intervention regressions) and Schema/state/
real-watcher tests. New barriers cover concurrent requests, cancellation before/
after source Stop, blocked cancellation propagation, rule withdrawal, candidate
replacement/effective-content reversion, user Pause, checkpoint replacement,
noncooperative actual capture-task cleanup and genuine failure/timeout mapping.
Fake input counters are not physical-input evidence.

Real Debug WinUI + WGC passed explicit confirmation/new generation/AFTER-only,
inherited Pause (no AFTER before explicit Resume), and red-scene SceneRejected
with no target-version business. HotReloadUiSmoke passed (478ms UI update,
464ms JSON recovery, 443ms unchanged Lua correction); GenericInstanceUiSmoke
retained old UI/config/paused instance on staged conflicts; LifecycleUiSmoke passed.
These are agent automation results. The new manual procedure is NOT RUN / awaits
user confirmation. Minecraft/local-model acceptance and independent Hook root-
cause experiments were not run or changed by this task.
