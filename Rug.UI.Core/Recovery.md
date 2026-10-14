---
id: rug_recovery
type: logic_node
inputs: [rug_ui_core]
outputs: [rug_ui]
tags: [recovery, lifecycle, cancellation]
---

# Explicit anomaly recovery — 3.5-06B-01

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
a fresh image must show a large green center panel. This is a controlled sample,
not a universal verifier. Source registration/held-input tracking never stop for
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

## Human acceptance — NOT RUN / awaiting user

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
Human recovery/normal human takeover of a resumed recovery run: NOT RUN, user
steps above. Existing 05B-02 human acceptance remains as recorded, not overwritten.
NuGet audit initially lacked sandbox network access; forced normal restore outside
the sandbox succeeded without dependency changes. Subsequent builds were clean.
