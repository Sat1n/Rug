---
id: rug_input_sink_control
type: logic_node
inputs: [rug_device_notify_control, rug_native_input_probe]
outputs: []
tags: [diagnostics, raw-input, input-sink, control]
---

# Keyboard INPUTSINK on / off / on

## Reviewed DEVNOTIFY reversal (2026-10-09)

Nine complete fresh logs retained B target/receiver, B Core OFF, startup A-B-C,
Hook installation C-B and keyboard registration in every round. Actual calls
used 0x2100 / 0x0100 / 0x2100, succeeded=1/error=0. Both PREPARED preceded
C installation; C completed before B started installation. Baselines preceded
emission and first snapshots followed all eight successful send calls.

| DEVNOTIFY | A / B / C PID | A AUTO keyboard entries; A/B/C tagged keyboard | Raw input/cleanup | Device changes |
|---|---|---|---|---|
| On | 63004 / 48308 / 58124 | 0; 0/0/0 | 694/694 | 9 |
| Off | 30508 / 11556 / 55364 | 0; 0/0/0 | 393/393 | 0 |
| On | 27096 / 8312 / 60888 | 0; 0/0/0 | 373/373 | 9 |

All four specific F8/B target checks per session had down/up=1/1; B cumulative
per key was 2/2 from zero baselines. A mouse observed ten tagged injected events
per AUTO, no observed unflagged AUTO activity. Human keyboard before/after
counts were 320/57, 136/62, 137/73. Receiver/Hook hashes matched across all three
rounds. No reported parse/ownership/thread faults; all threads exited and final
registrations zero. A's logs record the same Core path/size, not a loaded SHA256.

DEVNOTIFY is not necessary for this fixed reproduction. The earlier whole
registration off control recovered, but does not prove a Raw API root cause.
Raw classification was NOT COLLECTED. Normal shortcuts/interference require
user confirmation; original integrated failure and automatic takeover remain
open. Decimal 0x2100 is 8448 (earlier guide's 8576 was a documentation typo).

## Next selected condition

[RunRole](NativeRoles.cpp#function:RunRole) adds --input-sink on|off (default on).
The manual series ALWAYS has --raw-registration on and --device-notify off.
Change only INPUTSINK: on=0x0100 (256), off=0 (default foreground reception).
Zero flags is NOT removal: off must show rawRegistrations=1, succeeded=1 and
nonzero registration timestamps/thread/HWND. No NOLEGACY/EXINPUTSINK is used.

[Receiver](RawReceiver.cpp#class:Receiver) preserves the nine previous exports,
96-byte Raw snapshot and 56-byte actual registration record. A tenth TEST export
Probe_CreateWithFlags accepts three validated 0/1 options fixed for the lifetime;
all previous constructors default INPUTSINK on. Ownership checks verify the
selected INPUTSINK bit even for flags zero; foreign registrations still fault
and are not removed by a nonowner. No in-session mode switches/re-registration.

Receiver DLL, thread/window, timer, count-only handling/DefWindowProc cleanup,
target thread/window/protocol, Hook callbacks/installation order and A's actual
Core emitter are retained. Windows flags zero receives foreground input; keep B
target foreground during AUTO. The foreground-only receiver is not expected to
receive background events, and a quiet stream is not by itself a fault. See
[Microsoft's flags contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice).
No packet parsing, human text/trajectories, filters, ignore windows, retries,
automatic rehooking, production event-source changes or automatic takeover.

RAW_REGISTER_CALL records actual requested flags, result/immediate failure error,
QPC before/after and receiver thread/HWND. queriedFlags is separate (DEVNOTIFY
may not be echoed). Flags/result/owner and call timestamps stay fixed in-session.
STOPPED archives the actual call while query flags become zero after removal.
Readiness/barriers/timers do not certify Hook observation or Raw classification.

The receiver has been rebuilt; use SAME new SHA256 in ALL THREE rounds:
40D9739DC6E50816CB68D4EEEFEED90F2E121793192EB32646238825CAE084EB.
Core, A and Hook DLL retain [fixed preceding hashes](CoreLoadControl.md).
The preceding DEVNOTIFY-series receiver hash is historical, not this new baseline.

## Automated contracts

Build once, without -BuildCore; do not rebuild between manual rounds:

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode Build
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --test
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1 -RawOwner B -TargetOwner B -CoreLoad off -RawRegistration on -DeviceNotify off -InputSink off
```

The runner covers 64 owner/target/load/registration/notification/sink combinations,
three A/B/C pairs each (192), six incomplete cleanups, 37 pre-start CLI refusals
and 14 default/optional cases. Checks require actual registered ownership in
both sink modes, correct call flags/results/clocks/identity, unchanged registration
after Hook installation, A's target binding without AUTO and full cleanup.
Role self-tests cover four registered flag combinations and four unregistered
option combinations (three lifecycles each), conflicting startup, foreign owner
preservation for zero/INPUTSINK flags, invalid constructors and observable faults.
Managed regressions retain legacy defaults. Physical AUTO/HUMAN are NOT RUN by
the agent; automated lifecycle tests do not supply software classification.

Debug/x64 strict native build, both native self-tests, all 64 combinations
(192 pairs) and managed --raw-input-tests passed on 2026-10-09. Core/A/Hook hashes
were verified unchanged. WinUI/product-wide regressions were not rerun for this
test-only change. The new manual INPUTSINK reversal remains pending user input.

## Three fresh manual sessions

Keep external applications/elevation unchanged. Close ALL A/B/C probes between
rounds; start A-B-C every round, wait both PREPARED, install C then B. Only change
INPUTSINK ON/OFF/ON. Raw B, target B, Core OFF, registration ON, DEVNOTIFY OFF
remain fixed. A in all rounds:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeInputProbe.exe --managed-target
```

Rounds 1/3, B then C in separate terminals (wait B PREPARED/COMMANDS first):

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner B --core-load off --raw-registration on --device-notify off --input-sink on
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c --raw-owner B --target-owner B --core-load off --raw-registration on --device-notify off --input-sink on
```

Round 2 uses fresh processes with ONLY input-sink off:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner B --core-load off --raw-registration on --device-notify off --input-sink off
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c --raw-owner B --target-owner B --core-load off --raw-registration on --device-notify off --input-sink off
```

1. C install -> wait INSTALLED -> B install -> wait INSTALLED. B target=1,
   receiverReady=1/rawRegistrations=1 all rounds; C has no Raw/target/Core.
   Actual registration flags=256/0/256, succeeded=1/error=0, valid clock and
   receiver HWND/thread. DeviceNotify=off and no Core in B/C throughout.
2. Paste B target HWND/PID into A. HUMAN BEFORE in B target and another app,
   keyboard/mouse/normal shortcuts. B baseline -> C baseline -> A next.
3. FIVE-second countdown: focus B white target, release keys/buttons, keep hands
   off all four AUTO modes/keys. Do not manually press F8/B during AUTO.
4. A HUMAN AFTER prompt: B snapshot -> C snapshot BEFORE human checks. Complete
   HUMAN AFTER -> A done -> B/C snapshot again -> B done -> C done.
5. Return NINE complete logs grouped sink ON/OFF/ON, including hashes, actual
   registration, phases, specific target/Hook evidence and cleanup. Report any
   accidental AUTO input and normal shortcut/window behavior.

## Predeclared interpretation

| Complete sends/targets; correct flags/hash/order/ownership/lifecycle | Meaning |
|---|---|
| Missing / complete / missing | INPUTSINK condition participates here; not proof a particular API/implementation is faulty |
| Missing / missing / missing | INPUTSINK not necessary in this topology; keyboard registration without either flag still reproduces |
| ON controls no longer reproduce | Baseline changed/unstable; audit before causal inference |
| Only some observers recover | Preserve each A/B/C callback/classification/per-key result separately |
| Flags zero but registration absent, wrong target/focus/send, fault/order/hash/interference | Inconclusive; correct experiment validity first |

All Hooks share low-level Hook infrastructure. Count-only Raw evidence remains
NOT COLLECTED for software/candidate classification; do not combine old parsed
sessions. A reduced result cannot close original integrated acceptance.
