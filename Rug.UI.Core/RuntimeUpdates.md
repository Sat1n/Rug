---
id: rug_runtime_update_freeze
type: logic_node
inputs: [rug_ui_core]
outputs: [rug_ui]
tags: [plugins, checkpoint, lifecycle, freeze]
---

# Runtime update preparation (3.5-07B-01)

[Coordinator](Services/RuntimeUpdateCoordinator.cs#class:RuntimeUpdateCoordinator)
retains one process-local plan per installation. This unit only freezes/releases
the original coroutine: it never publishes a runtime candidate, stops a run as an
update action, migrates state, starts a generation or replays business input.
07A UI-only updates and invalid-candidate rollback remain in effect.

## Host contract

`ITaskScheduler.CreateRuntimeUpdateContextAsync(pluginId)` creates the trusted,
installation-bound caller. Through Gateway, use `PrepareRuntimeUpdate {}`,
`QueryRuntimeUpdate {}` and `CancelRuntimeUpdate {updateId}`. Ordinary UI/Lua/Agent
contexts cannot forge the host capability; Schema/CommandBus arguments cannot
approve a plan. Prepare requires all currently effective declarations plus an
explicit checkpoint grant. Query/cancel require the trusted installation identity,
but not grants that may have been revoked. Existing Gateway deadlines, operation
IDs, validation-before-deduplication, bounded records and two-minute dedup apply.
Replaying Prepare returns its original accepted result; Query reads current progress.

`RuntimeUpdateSnapshot` contains installation, host `updateId`, old effective and
candidate content versions, stage/error/reason, monotonic revision, deadline and
detached per-instance full RunIdentity/progress/receipt. A receipt fixes checkpoint
ID, submission sequence, exact record SHA-256 fingerprint, source identity and node.
No checkpoint hash/compatibility fields are rewritten. Pending candidates are the
immutable validated 07A objects; preparation never rereads disk or publishes them.

| Stage | Meaning / exits |
| --- | --- |
| Pending | Valid runtime update waiting; saves alone do not arm/freeze runs |
| WaitingCheckpoint | Every active source reserved; each waits for a new successful submission/drain |
| Frozen | All sources drained and held: **已冻结，尚未切换** |
| Cancelled | Only this plan's holds released; original Lua call may return |
| Failed | A source/commit failed or deadline expired; all owned holds released |

Prepare rejects missing candidates (`NoPendingUpdate`), duplicate active plans
(`Busy`), unsupported state/capability (`UpdateUnavailable`) or capacity exhaustion.
Sources must all be Running without pending user pause, use the effective loaded
code/version, declare checkpoint, and have trusted exact-code registered nodes.
Starting/Paused/Recovering/AwaitingHuman/Stopping are not resumed or stopped by
preparation. The short per-plugin start lease captures/arms the complete source
set. It is released immediately, never held while waiting for a checkpoint.
Normal and checkpoint starts check the reservation before creating services and
return `UpdatePreparing`. Stop, other plugins, query and cancel remain available.

## Freeze and cancellation decisions

Arming fixes a minimum checkpoint sequence. Historical records and submissions
already in progress at arming cannot count. The old script continues normally.
The real checkpoint coordinator must validate and successfully publish a new
record. While checkpoint business admission is still closed, its publication
callback checks current plan/source/cancellation/deadline and atomically installs
the hold under the plan then instance transition locks. No IO, validator, observer
or cancellation callback runs under those locks.

Only after actual checkpoint IO/observation permits and the checkpoint semaphore
are released does the Lua handler drain admitted work and acknowledge this exact
receipt. All acknowledgements are required for Frozen. The logical hold has no
Native work permit. The completed result remains in LuaRuntime's pending slot;
the existing serial driver waits outside both the Lua drive gate and VM lock,
without running Lua, and Stop can wake it. Frozen business requests, including queued
input rechecks, reject `InvalidLifecycleState`. No input resources are released
while Native still uses them. A rejected/timed-out/missing checkpoint fails the
plan, not the script; genuine Lua/permission/IO/cleanup failures retain faults.

Cancel removes ownership before signalling waiters. It returns from the same
checkpoint once, with original locals/config/state/identity; it does not run
on_init/on_stop. A user Pause remains requested/confirmed and cannot be cleared
by update cancellation. Resume cannot bypass an installed update hold. Stop,
failure, revocation, invalid target and existing unplanned takeover remain
authoritative; release never revives a terminal run. Planned recovery handoff
semantics are unchanged. One failed/timed-out source releases its peers only.

Direct original-token checks at both freeze and acknowledgement avoid relying
on cancellation propagation. Cancellation before the freeze decision cannot
install a hold; after that decision it releases only the matching hold. A newer
validated candidate, reverting to effective content, or successful UI replacement
invalidates the old plan. Receipt ID/sequence and update ID prevent old completions
and old cancel requests from touching a new plan. These are process-local
decisions, not a filesystem/OS transaction.

Bounds: eight pending installations (at most 32 MiB of candidate bytes), 32 sources
per plan, at most two minutes including frozen time (UI uses 30 seconds), one
monitor per plan and one lifecycle subscription per source. Terminal summaries retain
at most eight bounded records and are lazily removed after ten idle minutes;
replacing a candidate or host disposal releases its body. End/cancel removes all
subscriptions, completes logical waiters and disposes the monitor's linked token
and timeout. No persistent update plan or automatic retry is provided.

## UI and verification

The footer queries through Gateway, offers 准备运行更新 / 取消更新准备, and shows
per-source node/drain progress. Notifications cause fresh queries, with revision
checks against stale responses and plugin selection. Frozen is displayed apart
from ordinary Paused; failures include stable error and reason. No permission
dialog or automatic approval is added to preparation.

```powershell
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --runtime-update-tests
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --runtime-update-ui
```

The default full script suite includes the deterministic actual-Lua tests. They
use Fake input/observation, real checkpoint files and controlled validation/IO
barriers for history, cancellation propagation, pause, stop, revocation, candidate
replacement, multi-source timeout, unsupported states, duplicate requests and
repeated cleanup. They do not claim physical input or human acceptance.
The opt-in UI fixture creates a separate green target, temporary `update_probe`,
isolated record root and real WinUI host. UI Automation binds the target and
explicitly invokes the permission dialog; real WGC validates nodes. Lua log counts
prove one INIT, paired BEFORE/AFTER after cancellation, then no AFTER for the
checkpoint stopped while frozen. Only owned processes/directories are cleaned.

For manual inspection, copy the shipped sample into a test plugin root, bind a
green controlled window, grant/start, append a comment to its main.lua, then use
Prepare -> Frozen -> Cancel. The Lua log must advance to AFTER in the same run.
Prepare again and Stop: the held AFTER must not execute. The sample is pinned to
embedded source; modified code cannot acquire trusted-node support on a new run.

Actual runtime switching and version mapping are deferred to 07B-02. Checkpoints
do not atomically commit OS actions and disk records. Existing independent Hook
observation gaps and human/input-source acceptance limits are unchanged.

## Recorded verification (2026-10-20)

SDK 10.0.402. Final Debug/Release x64 WinUI (including Core), script-test and
UI-test builds: zero warnings/errors. Both configurations passed complete script
regressions (including the new freeze suite and existing lifecycle/recovery/
checkpoints/Gateway/grants/arbitration/intervention/07A) and Schema/state/watcher
tests. The freeze suite also proves its logical wait releases both Lua serial
gates and actual-work/checkpoint permits.

Actual WinUI + fresh WGC passed with one INIT, BEFORE 1, AFTER 1, BEFORE 2,
one STOP and no AFTER 2; the same run generation was retained across cancellation.
Existing HotReloadUiSmoke (481ms update), GenericInstanceUiSmoke,
LifecycleUiSmoke and Phase3E2E (490ms update) passed. These are automation results,
not new real-human or independent Hook-gap acceptance. No new human keyboard/mouse
test was run; this unit leaves input classification/takeover policy unchanged.
