---
id: rug_recovery
type: logic_node
inputs: [rug_ui_core]
outputs: [rug_ui]
tags: [recovery, lifecycle, cancellation]
---

# Explicit anomaly recovery — 3.5-06B-01

08C-02 composes this unchanged protocol in the [persistent visual recovery sample](../samples/controlled-visual/RECOVERY.md):
real observed BEFORE, planned handoff, explicit fresh capture verification, retained
on_init locals, AFTER-only input and new visual confirmation. Actual PNG/v1 JSON
identity/link preservation and late validation/Stop barriers are checked separately
from WinUI interaction, human acceptance and known CLR shutdown failure.

The composition root injects `IRecoveryCoordinator`, optional `IRecoveryExecutor`
and installation/scenario-bound `IRecoveryValidatorRegistry`. No executor or
validator implies human waiting, never production success. Executors receive
detached identities/context strings; validators receive a guarded, read-only fresh
capture capability. Neither receives input tools or a native/CLR script interface.

## Fixed contract and transitions

`rug.agent.resolve_anomaly(reason, contextString)` still requires live `agent`.
The host assigns recoveryId and generation, pins the original RunIdentity and
installation/capability ceiling, and permits one active recovery per run.
Starting/Running -> Recovering (Freezing/Archiving/Attempting) -> AwaitingHuman or
Recovering (Validating) -> original Starting/Running only after verification.
Failure -> Stopping -> Faulted. Stop from either recovery state -> Stopping ->
Stopped (unless real execution/cleanup failure). Ordinary Pause/Resume reject.

Results have status resolved/needs_human/failed, recoveryId, bounded reason and a
stable RecoveryError. needs_human is published to the immutable lifecycle snapshot
but never returned to Lua: the original coroutine stays yielded. Only verified
resolved is returned as an ordinary Lua table. No on_init/tick restart/replay.
Stop wins result application; identity/id/generation/state/grants/target are checked
again before any continuation. Revision/state publication use the existing run
transition lock, and delayed UI notifications re-query the selected run.

The final commit follows all service/registry lookups. Under the run transition
lock it checks full identity/installation, recovery and validation generations,
current phase, failure/Stop, request cancellation, and the earlier of the Gateway
request deadline or validator deadline. The successful check is the commit
linearization point: state and asynchronous Lua result publication form one
decision. Cancellation/deadline observed before this point leaves AwaitingHuman
and cannot publish resolved; cancellation after commit cannot undo that decision.
No registry, service, UI observer or token cancellation runs under this lock.
Cancelled commitment also corrects provisional archive verification to needs_human.
Gateway passes the original deadline, so expiry does not depend on timer callback
propagation. Duplicate requests keep their original outcomes.

## Input and cancellation boundaries

Recovery closes ordinary business admission before draining the current Lua
segment and admitted input/releases. Real capture/archive work has separate
counted permits. Advice and human waits do not hold native-work permits. Bounded
executor/validator tasks are observed after timeout; stale advice is discarded.
Stop interrupts logical waiting, but keeps handles alive until actual native
capture/input/archive tasks and releases finish.

For planned human control, a trusted atomic arbiter handoff retires the drained
input session/epoch and registers preservation before global takeover. Other
input runs still stop; timer/vision survive. Only a registered drained recovery
may preserve its coroutine. Unplanned candidate input during attempts/verification
uses unchanged 05B stopping. Raw Input stays enabled with no ignore windows.

Return control restores global availability only. Explicit host VerifyRecovery
through Gateway validates identity/install/grants/recoveryId, reacquires a fresh
detector input session, and reads new capture through the trusted validator.
Failed/missing verification returns to AwaitingHuman. Old epoch callbacks carry
session versions and cannot affect new sessions. No input recovery shortcut.

## Persistence and boundaries

Existing PNG/JSON records gain recovery association and a final verification
summary. The new recovery path stores bounded context text, never a Lua VM stack,
native handle, internal object or provider secrets. Images/context stay local.
Permission denial, archive IO, Lua errors and cleanup faults remain failures.
This unit has no VLM, checkpoint, MCP, notification, remote or persistent recovery.

## Interfaces and limits

The [coordinator contract](Abstractions/IRecoveryCoordinator.cs#interface:IRecoveryCoordinator)
and [implementation](Services/RecoveryCoordinator.cs#class:RecoveryCoordinator)
are injected; the scheduler keeps the authoritative TaskExecutionContext.
Gateway adds `AutomationOperation.VerifyRecovery` with JSON `{recoveryId}` and
trusted PluginUI source only. Ordinary operations/Pause/Resume cannot approve.
Same operationId returns the retained request outcome; concurrent distinct
continue requests join one verification. After a failed check, a deliberate new
request may retry; after resolution, repeats return the same recovery result.
A stale recoveryId is rejected. Control cancellation/deadline cannot later resume
Lua; caller cancellation does not destroy the retained coroutine.

Default attempt/verification deadlines are 5s, host configurable in `(0,60s]`.
Human waiting has no automatic timeout/continuation. There are at most 32 actual
executor/validator advice tasks per coordinator; noncooperative tasks retain a
slot until real completion. One actual verification per recovery; timeout with
live capture returns Busy on retry, preventing queued native-work accumulation.
No native permit is held for user waiting/advice. Capture/archive permits cover
actual work, not Gateway's wait. Late real resource failure closes/faults the run;
late suggestions are observed/discarded. WGC `null` means no new frame: poll within
the validation deadline, never substitute the old VisualEngineBridge cache.

The trusted registry is keyed by normalized installation and exact reason/scenario.
The shipped `recovery_probe` uses `controlled-ready` and FreshGreenFrameValidator:
a fresh image must show a large green center panel. Within one request, it samples
with a 1s observation budget (32ms cancellable polling), ending immediately on
green; sustained red returns ValidationRejected. No further grab starts after
that budget. An already-started native grab may finish later; the coordinator's
deadline and counted-work rules still apply, including noncooperative late work.
A fresh grab bypasses the managed cache but the unchanged C-ABI provides no frame
presentation timestamp; its first returned image may precede the user's change.
This bounded observation belongs only to the controlled validator, not a universal
verification/retry policy. It never repeats input or hides candidate events.
Source registration/held-input tracking never stop for
human recovery. No source marker authenticates a human. The independent low-level
Hook observation gap remains unresolved and is not a production classification verdict.

## Verification commands and results

From repository root after Debug/x64 builds:

```powershell
dotnet build tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Rug.UI/Rug.UI.csproj -c Debug -p:Platform=x64 --no-restore
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --recovery-tests
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --recovery-native
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --recovery-ui
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64 --no-build
```

`--recovery-tests` uses actual Lua and controlled Fake services/barriers. It covers
init/tick locals, immutable table return, missing/false verification, deduplication,
concurrent runs, advice/validation/native timeouts, Stop, caller cancellation,
late native busy boundaries, real faults, identity/grants/target replacement,
planned takeover with other-input stopping/pure-vision preservation, unexpected
candidate takeover and new-epoch old-event rejection. Fake results are not human evidence.
`--recovery-native` uses real InputService→Rug.Core→isolated PostMessage target,
production Raw source, WGC, PNG/JSON. Target B=(1,1) before anomaly and (2,2) after
fresh green validation; red validation keeps the coroutine frozen. A trusted host
changes the panel; this does not count as human detection or approval acceptance.
UIA consent/selection/verification is separately reported by `--recovery-ui`.
Existing lifecycle/Gateway/grants/arbitration/intervention/cache/state/hot-reload
regressions remain enabled. Frozen reversal records/binaries are not rebuilt.

## Human acceptance — user-confirmed

06B-01 was accepted by the user after dabceb2. The user explicitly confirmed
single-click green verification/continuation, persistent-red rejection and Stop
while AwaitingHuman. This is human evidence, separate from agent UIA/Fake/native
automation. The independent Hook observation gap is unchanged.

Use **one command**, not two parallel terminal processes:

```powershell
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --recovery-manual
```

1. Select an English input layout. The fixture creates a red/green window,
   isolated WinUI app and temporary plugins. In DevTools drag the crosshair to
   the red target. Optionally start timer_probe and bound vision_probe.
2. Select recovery_probe, click **运行绑定窗口任务**, explicitly confirm permissions.
   Keep keyboard/mouse still during the 5s script sleep and BEFORE B. Expect
   B=(1,1), **等待人工 · controlled-ready**, **人工控制已生效**, and the same instance
   still selectable. Other input runs stop; timer/vision remain.
3. While waiting, click the target or press Space to turn it green. These are
   real human actions; the registered recovery must retain its coroutine.
4. Click **交还控制**. Expect input available but Lua still waiting and no new B.
   Click **验证并继续**. Expect **正在验证**, fresh screenshot verification, then
   **运行中** and B=(2,2). Original on_init local/state assertions must pass; the
   BEFORE action must not repeat. Do not type B yourself.
5. Now operate keyboard/mouse in another window. This is unplanned takeover and
   must stop the input run, execute on_stop once, preserve timer/vision. Return
   control must not resurrect it. Report actual observed states/logs separately.
6. For red rejection or Stop while waiting, type `done`, relaunch a fresh fixture
   and repeat with red unchanged; Return+Verify must not continue the mainline.
   Stop ends waiting without requiring confirmation. `done` closes only owned
   UI/window and removes verified temporary plugin paths; archive stays local.

No real VLM, recovery input authority, provider, checkpoint, MCP or whole-3.5.6
acceptance is claimed. 06C will need a separate persistence contract; this unit
retains an in-process coroutine only. A native call that ignores cancellation can
keep Stopping until it really finishes; there is no forced handle disposal.

### Recorded automated evidence (2026-10-14)

PASS: Debug/x64 Core, script harness, UI tests and WinUI build, 0 warnings/errors.
PASS: complete script regression (including 06B barriers and 06A/direct anomaly
Stop classification), Schema/state/hot reload; actual native red/green WGC and
B=(1,1)→(2,2); actual recovery WinUI consent/selected identity/Verify/Stop.
PASS: existing instance/grant, lifecycle, manual takeover and demo UI regressions;
demo reload 545ms; UI JSON reload 483ms, JSON correction 461ms, Lua correction 499ms.
Setup mouse actions in recovery UIA are genuine software-tagged test setup before
session startup, not human evidence. Native input business actions still use Rug.Core.
User confirmed the actual manual main loop: Return alone does not continue;
explicit validation resumes the original on_init, B=(1,1)→(2,2); later human input
stops that run, timer/vision survive, and Return does not revive it. The user also
reported that green sometimes required another Verify request; the single-frame
controlled validator is now replaced by bounded fresh-frame observation. The
updated manual green transition and red-rejection/Stop checks were NOT RUN by
the agent; the user has now confirmed all three. Existing 05B-02 and independent Hook-gap
records remain unchanged. Native/UI automation now requires one green Verify
request, removing the earlier retry-until-success test behavior.
NuGet audit initially lacked sandbox network access; forced normal restore outside
the sandbox succeeded without dependency changes. Subsequent builds were clean.

### Green-transition follow-up (2026-10-14)

PASS: seven deterministic actual-Lua scenarios with controlled time/capture:
red/red/green in one request (3 frames), persistent red (32 frames over 1s), caller
cancel, Stop, capture IO fault, shorter coordinator deadline, and noncooperative
late green. Mainline/dedup/work permits remain correct; faults are not hidden.
PASS: complete script regression and Debug/x64 WinUI build, 0 warnings/errors.
PASS: real production Raw/WGC/InputService native recovery and actual WinUI
recovery UIA, each requiring one green Verify request and final B=(2,2).
Agent NOT RUN: post-fix human single-click green observation (subsequently user-confirmed); previous user-confirmed
main-loop/control semantics stand. No frozen binary or Hook reversal was rebuilt.

### Commit and paused-stop race follow-up (2026-10-14)

Deterministic final-registry barriers cover caller cancellation, both request and
validator expiry without timer propagation, Stop, validator replacement, genuine
lookup failure and one successful actual-Lua/Fake-input commit. Cancelled/expired
requests retain AwaitingHuman with zero subsequent key down/up, corrected archive
status and no replay on duplicate IDs. Stop remains terminal; real failures fault.
Paused-wake barriers hold cancellation propagation and distinguish Stop, genuine
resume failure and Stop during explicit resume checking. The actual scheduler
stops a paused Lua run normally with zero resume checks/input and one on_stop.
UI lifecycle presentation tests cover stale Validating metadata with terminal
statuses; the WinUI composition root has one recovery registry/coordinator.
PASS: `--recovery-tests`, `--lifecycle-tests`, two consecutive complete script
regressions, Schema/state/hot reload and actual `--lifecycle-ui` automation.
PASS: Debug/x64 Core/script/UI-test/WinUI builds, 0 warnings/errors. The sandboxed
WinUI launch failed; the actual-desktop UI run passed. NOT RUN this follow-up:
human recovery and the red/green native recovery fixture; no claim of new human
acceptance or whole-unit approval is made.
