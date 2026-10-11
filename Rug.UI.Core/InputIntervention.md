---
id: rug_input_intervention
type: logic_node
inputs: [rug_ui_core, rug_ui]
outputs: [rug_ui_core, rug_ui]
tags: [input, observation, takeover]
---

# Candidate external input takeover (3.5-05B-02)

[IInputInterventionCoordinator](Abstractions/IInputInterventionCoordinator.cs#interface:IInputInterventionCoordinator)
is a trusted host service implemented by
[InputInterventionCoordinator](Services/InputInterventionCoordinator.cs#class:InputInterventionCoordinator).
WinUI owns one production Raw Input source and coordinator, sharing the existing
[input arbiter](InputArbitration.md) with scheduler/Lua/Gateway and DevTools.
No Lua API, schema command or operation parameter configures detection, registers
an owner, changes the strategy or returns control. Grants remain independent.

## Session and event boundaries

The app starts its source explicitly before activation. Ready requires successful
receiver registration, not just a thread. Input-declaring plugin startup registers
its complete RunIdentity before capture/input factories or Lua initialization.
The registration lasts through starting, running, pause, sleep and other gaps,
until actual runtime/native cleanup completes. Failed startup releases it too.
The scheduler retains the run's original input epoch token. Cancellation callbacks
may wake Lua before the instance token is cancelled; a `Stopped` hook result backed
by cancellation of that epoch (or its host grant lease) propagates cancellation
and completes normal cleanup instead of recording a script fault. It never queries
a new epoch after Return control. Unexplained `Stopped` results and actual script
or cleanup errors remain faults.
DevTools registers a trusted session around an entire input test; stress testing
retains it across capture/OCR gaps. Preview, pure trajectory planning and
timer/vision-only instances do not register sessions. Session capacity is 256.

CaptureBoundaryAsync crosses a receiver barrier and waits for all captured
events through that sequence to finish consumer delivery. The first active
session records its generation, captured sequence and monotonic time as an
admission boundary. Subsequent sessions do not reset the boundary while another
session is active. Events captured before launch cannot trigger the new run even
if delivery was delayed. Returning control sets a new boundary; old requests,
old source generations and repeated/out-of-order notifications cannot revive it.
This is an observation barrier, never evidence that a diagnostic Hook is healthy.

Candidate key/button state is maintained continuously, even without a session.
Startup and first-session boundaries additionally seed held keys from the current
high bit of [GetAsyncKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate)
through Native/InputKeyState; its unreliable low transition bit is unused.
New keyboard down triggers; repeat or already-held down does not. Releases update
held state and never trigger alone. Thus the startup/return button's trailing
release is not an intervention. No fixed ignore interval or listener pause exists.
Key/button/device position tracking is bounded at 4,096 identities.

The policy is global whenever at least one input session exists:

- New candidate key or mouse-button down, nonzero wheel and effective movement
  request takeover. Relative zero movement does not; the first absolute sample
  establishes position, and later changed absolute position counts as movement.
- Coalesced motion retains first sequence, total samples and the last actual
  movement sequence/time. Out-and-back motion still triggers even when its summed
  displacement is zero; a later zero sample cannot promote pre-session movement.
- Exact Rug software tags are excluded. Device changes update bookkeeping only.
  Unknown/absent tags mean **candidate external input**, not an authenticated human.
- Without input sessions, candidate events do not trigger global takeover.
  Operations in other windows still trigger while an input session is active.

## Takeover, faults and concurrency

The first trigger latches a reason and calls the existing TakeOverAsync once.
Admission closes synchronously; subsequent events merge into that observed task.
The callback never waits for stopped instances, the source or native cleanup.
The arbiter cancels its old epoch, rejects queued input and stops input-declaring
runs. Session drain is an additional stop handler, so DevTools and starting runs
must finish cleanup before ManualControl is published. Timer/vision-only runs
continue. Manual takeover remains available and uses the same pipeline.

Existing leases cover actual noncooperative native tasks and required key/button
releases after Gateway timeout/cancellation. No handle is freed or conflicting
input admitted before those tasks settle. Cleanup failure retains the arbiter's
quarantine and CleanupFailed; it is reported separately from detector failure.
Synchronous native calls may emit residual input until completion. No force-stop,
automatic retry, new C-ABI or alternative pause mechanism is introduced.

An unready/faulted/unexpectedly stopped source, changed generation, lost event
continuity, overflow or consumer error closes detection admission. Active sessions
request safe takeover; otherwise new sessions and input are refused. Admission
guards run before queueing, after waiting and before actual lease steps, outside
the arbiter lock. Source/coordination locks never execute input or wait for UI.
A race after a validated native step follows the existing already-started outcome
uncertainty; cleanup is still mandatory. No silent detector restart is attempted.

Only explicit host Return control can create a fresh automation epoch, and it
requires a healthy detector plus completed arbiter cleanup. Regranting permission
or hot reload cannot bypass those checks or revive a stopped run. Async takeover
errors are observed and exposed. UI notifications reread current snapshots on
the dispatcher instead of applying old captured state.

Host close awaits watcher shutdown, takeover/session/native drain, source disposal
and scheduler disposal before closing the window. Coordinator disposal unregisters
source/control handlers, its admission guard and stop handler. It is idempotent.
Diagnostics and UI retain only aggregate state/counts and trigger categories;
no human key sequence, text or mouse trajectory is persisted.

## Verification

Run the normal script suite or `--intervention-tests` for deterministic policy,
launch/return boundaries, old generations, software exclusion, repeated actions,
whole-session monitoring, two runs plus DevTools, startup/factory failure, source
failure, consumer errors, net-zero coalesced motion, actual source delivery barrier,
Gateway timeout with live native input, duplicate protection and cleanup failure.
Existing arbitration, Gateway, Lua, cache and registration tests remain enabled.
The WinUI `--takeover-ui` automation checks real detector readiness, the global
rule text, manual controls and timer-only preservation. Simulated candidates
prove policy, not actual human acceptance.

To reproduce **user-confirmed manual WinUI acceptance**, build Debug/x64 UI and script tests, then:

```powershell
dotnet run --no-build --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --intervention-manual
```

This creates/cleans an isolated target and three temporary plugins, launching
the actual app/production source. It installs no diagnostic Hooks and does not
approve grants or synthesize human input. In the same app session:

1. In DevTools use the crosshair to bind the white target. Select timer_probe,
   invoke its windowless Start and explicitly approve timer permission. Optionally
   select vision_probe and use the host **运行绑定窗口任务** button; approve capture.
   Only timer_probe has a windowless Start card. Vision/input pages instead show
   binding instructions; attempting windowless capture is correctly rejected as
   InvalidParameters. The instance selector is in the bottom host bar.
   For the B-delivery check, select an English keyboard layout for the white target
   before starting input_probe. IME message processing can change what its legacy
   key counter observes ([Windows IME messages](https://learn.microsoft.com/en-us/windows/win32/intl/ime-messages));
   this counter is not production Raw Input classification evidence.
2. Select input_probe, use **运行绑定窗口任务**, and explicitly approve input/timer.
   Release the startup click and keep hands off. The detector must show monitoring.
   The fixture script waits 5 seconds before the first B and after each B; detection
   stays active during both waits. Observe passive target B down/up and filtered
   Lua phase/Gateway-root-cause/scheduler-fault logs without typing into the terminal. `issuing-B`
   marks an attempted API call; `B-call-completed` is not target-delivery proof.
   Wait for paired target counts to grow before accepting software-input exclusion.
   Rug sends B repeatedly to the target without triggering takeover. A refused
   foreground activation is NOT RUN for software input, never filtering success.
   Foreground confirmation now awaits asynchronous activation for at most one second,
   keeping detection active. If it fails, the host log distinguishes
   `activationAccepted=False`/`confirmation=declined` from accepted activation with
   `confirmation=timed-out`, including target/observed HWND. Do not switch windows
   after startup to work around failure: that is a candidate input and must trigger takeover.
3. Press/release a key or move/click/scroll while Rug or another app is foreground,
   including the 5-second gap between input actions. Repeat with fresh runs for
   foreground/background and keyboard/mouse. Expect TakingOver, then ManualControl
   only after releases/cleanup. No raw key contents appear in the host UI.
4. Confirm input_probe has no remaining run; timer_probe and optional vision_probe
   still have their running instance in the picker. While manual, new input/DevTools
   input must fail. Explicitly Return control: old runs stay stopped. Start a new
   input run and repeat. Manual takeover must remain usable.
5. Enter `status` only outside an active input test (terminal input itself triggers
   this global policy). It shows aggregate target B down/up; counts include any
   physical B typed into that target and are not an independent software verdict.
   Close via `done`; the fixture closes its app/target and deletes only its own
   verified temporary plugin root. Report visible state, preservation and cleanup.

### 05B-02 manual acceptance — 2026-10-11

User manually confirmed PASS against baseline
`81fca0b5ebb001fccc5195400696f74f200b82b4` using the fixture above.
With an English keyboard layout, four automatic B presses/releases grew the
isolated target counters from `(0,0)` to `(4,4)` without takeover. Real user
operations triggered takeover; timer/vision runs remained active, and explicit
Return control did not revive stopped input runs. `on_stop` completed normally;
`Unexpected Lua hook state: Stopped` did not recur. These are user observations,
not synthetic human input or actions performed by the development agent.

This acceptance does **not** resolve the independent low-level Hook observation
gap. Keep its missing observations and frozen reversal evidence separate.
The [05B-01 production acceptance](../tests/Rug.UI.Core.Script.Tests/ProductionSourceAcceptance.md)
is retained separately. Its Hook discrepancy remains INCOMPLETE; frozen reversal
artifacts are unchanged. This unit does not extend the root-cause investigation
or claim that absence of a Rug marker authenticates a real person, virtual HID
or every remote-session input source.
