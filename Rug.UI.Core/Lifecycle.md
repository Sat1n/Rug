---
id: rug_run_lifecycle
type: logic_node
inputs: [rug_ui_core, rug_ui]
outputs: [rug_ui_core, rug_ui]
tags: [lifecycle, pause, cancellation]
---

# Run lifecycle and confirmed pause — 3.5-06A

[TaskExecutionContext](Models/TaskExecutionContext.cs#class:TaskExecutionContext)
owns transitions, signals and actual-work counters. The scheduler is the single
Lua driver. [LifecycleSnapshot](Models/LifecycleSnapshot.cs#class:LifecycleSnapshot)
is immutable: complete `RunIdentity`, status, `PauseRequested`, `ResumeRequested`,
run-local revision starting at 1, and `HasFailure`. Accepted changes increment
revision; duplicates do not. Events may arrive late/out of order: match identity
and re-query the selected live run, or compare its revision. The scheduler retains
only live contexts, clears subscriptions at completion, and keeps no unbounded
terminal snapshot cache. A held context retains its final snapshot.

## Transitions

| Current state | Request / boundary | Result |
| --- | --- | --- |
| Starting | Initialization and on_init finish | Running |
| Starting / Running | Pause | Same status, `PauseRequested=true` |
| Pause pending | Lua segment and admitted input drain | Paused |
| Paused | Pause again | Accepted, no change |
| Paused | Valid Resume | Request accepted; driver returns to pre-pause Starting / Running |
| Pause pending / active without confirmed pause | Resume | Rejected, `InvalidLifecycleState` |
| Starting / Running / pause pending / Paused | Stop, grant/input epoch cancellation, execution fault | Stopping |
| Stopping | Real work and cleanup finish | Stopped; Faulted if execution or cleanup failed |
| Stopping / Stopped / Faulted | Pause / Resume | Rejected, cannot revive |
| Stopping | Stop again | Same completion/cleanup |
| Stopped / Faulted, absent from live collection | Stop again | Idempotent no-op |

Starting is visible before capture/runtime setup finishes. Startup failure uses
the same cleanup authority. Capture still precedes Lua execution. Loading/setup
is not a pause confirmation: requests await setup and a driver boundary. Stop
wins over late start, pause and resume callbacks. No Recovering/AwaitingHuman state
is introduced.

Gateway registers a host-only preparing executor with that identity, enabling
Pause/Stop during setup. Business operations are rejected before initialization;
Lua replaces that executor only for the same live host-owned context.

## Pause boundary

`ITaskScheduler.PauseTask(id)` returns **request accepted**, not “already Paused”.
Gateway Pause preserves its success/Boolean envelope and deduplication contract.
`GetLifecycle(id)` and `LifecycleChanged` provide confirmation. Resume is accepted
only for confirmed Paused (pending duplicates coalesce); it never executes Lua on
the caller thread. Current grants, the original window lifetime stamp and input
control are checked before accepting and again on the serial driver. Gateway
Resume also requires the current declared permissions within the caller's live
authorization/capability ceiling, just as Start does. Gateway
also checks full plugin/instance/generation/binding identity.

Pause interrupts the driver's await, not the underlying sleep/vision operation.
Lua must leave its synchronous segment. That fragment may finish synchronous
state/log work before yielding, but cannot resume or submit the next business
action after the request. An unadmitted yielded action is deferred until explicit
Resume. If pause wins Gateway admission without effects, that unexecuted action
gets a fresh ID after Resume; the rejected ID remains deduplicated. Completed
physical actions are never retried/replayed.

Already admitted input, including dispatch/per-instance/arbiter queues, finishes
under existing permission/target/cancellation/control checks. Its permit spans
foreground confirmation, the full action and mandatory release, even if Gateway
returns timeout/cancellation early. New input is rejected during pending/confirmed
pause; after Paused, no queued input can still fire. Release failure retains
arbiter quarantine and leads to Faulted cleanup, not confirmed safe pause.

Timer/vision may finish while Paused. Results/frame cache are retained without Lua
progress and consumed once after Resume. InstanceState and startup PluginConfig
snapshot survive pause. A Lua fragment that never yields cannot confirm ordinary
pause; Stop still uses the watchdog. No timeout pretends Native work has ended.

## Stop and cleanup

The transition to Stopping atomically closes business admission under the run's
transition lock, before publishing lifecycle notifications. Cancellation and
notifications execute outside that lock; their propagation speed is never an
admission condition. Gateway checks Stopping/Stopped/Faulted at receipt and
dispatch (`Rejected/InvalidLifecycleState`, no uncertainty before execution).
Run work permits and deferred-business waits enforce the same closed state.
Input rechecks it after bridge/arbiter queues and between composite action steps;
already queued input cannot start after closure even while the token remains
uncancelled. A late Resume validation cannot reopen the run. Stop requests still
share cleanup, and KeyUp/MouseUp releases bypass business admission.

Stop waits for actual service tasks (including
noncooperative capture/OCR/input and the existing anomaly logger), Lua exit and mandatory releases before `on_stop`,
capture stop, runtime/input/capture disposal and state/subscription cleanup. Every
cleanup stage is attempted independently. Prepared instances invoke `on_stop`
at most once, with a watchdog and no yield. Host logs/`LastError` preserve errors;
the terminal snapshot indicates failure. A caller/Gateway timeout cannot complete
Stop or free live handles. Late exceptions are observed; input cleanup failures
are recorded before drain signals, preventing an early terminal success.
Stop's own run cancellation does not mask a caller deadline: Gateway returns
TimedOut while the shared cleanup continues in Stopping.

The original input epoch/grant cancellation still permits clean Lua Stopped.
Unexpected Stopped without host cancellation still faults. Human candidate input
uses takeover: stop input runs, preserve timer/vision, and require explicit Return
control/new startup. Paused input remains an input session and is stopped by
takeover/revocation. A physical Pause click does not suppress detection.

Hot reload remains stop-old/start-new; it preserves no pause position/checkpoint.
The new identity's snapshot governs UI refresh. `resolve_anomaly` retains terminal
incident behavior; it is not recoverable pause. No recovery coordinator/provider
or checkpoint persistence is added; these boundaries precede 06B/06C.

## Verification

From the repository root:

```powershell
dotnet build tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-restore -warnaserror
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --lifecycle-tests
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build
dotnet build Rug.UI/Rug.UI.csproj -c Debug -p:Platform=x64 --no-restore -warnaserror
dotnet build Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64 --no-restore -warnaserror
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --lifecycle-ui --instance-ui --takeover-ui --e2e
```

Actual Lua plus controlled barriers cover init/tick, sleep/capture/OCR, KeyUp and
native work, admitted queues, timeout/Pause/concurrent Stop, dual runs, startup
stop, invalid target, grants/takeover, late results, repeats and cleanup failures.
Stopping-notification barriers deliberately retain an uncancelled token while
asserting new UI/Lua/Agent/MCP requests, bridge/arbiter queues and Gateway dispatch
queues cannot execute. Independent-thread cancellation callbacks verify locks are
not held; late Resume and all terminal states reject without physical side effects.
The isolated WinUI timer regression explicitly grants permission and verifies
labels, button enablement, independent selection, explicit Resume, a paused timer
retained across manual takeover, refreshed new runs after UI reload and cleanup.

Automated verification on 2026-10-11: full script/Gateway/grant/input/intervention
regressions, Schema/state/hot reload and all four WinUI automation scenarios PASS.
Debug/x64 Core, script/UI tests and WinUI builds have zero warnings/errors.
Final demo reload was 589 ms; UI reload 549 ms, JSON recovery 458 ms and Lua
recovery 558 ms. Desktop UI tests require the interactive desktop; sandbox-only
app startup failures are not product passes. Frozen native experiments were not
rebuilt or repeated.

**NOT RUN by the agent:** fresh real-human 06A acceptance and an actual hardware
uncancellable-input pending-label run. Fakes/UI automation do not replace these.
Use the [isolated manual fixture](InputIntervention.md): pause timer/vision through
the footer, confirm “已暂停”, switch instances and back, explicitly Resume then
Stop. With input_probe active, physical clicking/moving on Pause must trigger
takeover and stop input; Return must not revive it. For Paused input testing,
submit Pause with a trusted automated host request before human front/background
interaction (a physical click already takes over). For verified long native input,
expect “正在暂停” until releases end, or “正在停止” until actual work drains.
Record these separately from the [05B-02 user-confirmed acceptance](InputIntervention.md).
The independent Hook gap/frozen native artifacts are unchanged.
