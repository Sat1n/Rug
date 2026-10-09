---
id: rug_raw_registration_control
type: logic_node
inputs: [rug_core_load_control, rug_native_input_probe]
outputs: []
tags: [diagnostics, raw-input, registration, control]
---

# Native keyboard registration on / off / on

## Reviewed Core-load result (2026-10-09)

Nine fresh A/B/C logs completed B Core on/off/on, target/Raw B and Hook C-B.
Observed B coreLoaded=1/0/1, C=0 throughout. Both PREPARED preceded C installation,
C completed before B installed, baselines preceded sending and snapshots followed
sending. All eight send records per session returned one; specific F8/B target
down/up=1/1 in each mode, 2/2 cumulative per key. A retained its real Core sender.
All A/B/C observers had zero tagged keyboard events; A AUTO callbackTotal=0 in
each session, while mouse taggedInjected=10. Human keyboard before/after counts
were 124/70, 136/71, 127/73. No parse/thread/registration faults; Raw input/cleanup
totals 354/354, 426/426, 246/246, final registrations zero. B diagnostic Core load
is not necessary for this native observation gap. A's Core emission is NOT ruled
out. Normal operation still requires user confirmation. Raw attribution was NOT
COLLECTED; original integrated acceptance and automatic takeover remain open.

## Single selected condition

[RunRole](NativeRoles.cpp#function:RunRole) accepts --raw-registration on|off,
default on, independently of rawOwner, targetOwner and coreLoad. The selected Raw
owner ALWAYS loads the SAME receiver DLL and creates its SAME dedicated thread,
message-only window, 250ms timer, barriers, count-only handler and cleanup path.
On registers keyboard usage 6 with INPUTSINK|DEVNOTIFY. Off makes NO registration
or removal call, and requires zero relevant process-local registrations. Both
check ownership on timer/input/device messages; a foreign registration faults
and must survive cleanup. The other role does not load the receiver in either mode.

[Receiver](RawReceiver.cpp#class:Receiver) retains legacy Probe_Create and the
96-byte snapshot layout. New TEST-ONLY Probe_CreateUnregistered selects off at
construction; no in-session toggles. Rug.Core C-ABI and production event source,
input emitter, Hook forwarding and takeover are unchanged. The receiver DLL was
rebuilt, so a new on baseline is mandatory. Its hash must match ALL three rounds,
not the preceding Core-load experiment. Current SHA256:
8E269D953C32791B6B0CA5D2F8C5D06E207AB8E88D2A9520D8ED66F9D593C4CB.
Core, A sender and Hook DLL hashes retain [preceding values](CoreLoadControl.md).

Every phase prints rawRegistration (requested), actual rawRegistrations,
receiverReady, Raw thread/HWND/timer/fault and ownership. In OFF, receiverReady=1
means diagnostic INFRASTRUCTURE readiness, NOT registered-device/event-source
readiness. Input/device counters must stay zero, timer must advance, barriers
work, window/thread identity remains fixed within session. Readiness/timers do
not certify Hook health. Snapshot counters are detached aggregates, not an atomic
cross-thread transaction. Software/candidate attribution remains NOT COLLECTED.

Invalid/missing/duplicate options reject before module/thread/window startup;
old commands default on and flags may reorder. No added key sequences, human
text/trajectories, filtering, ignores, retries or automatic Hook reinstallation.

## Build and automated contracts

Build ONCE before manual sessions; no -BuildCore and no rebuild between rounds:

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode Build
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --test
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1 -RawOwner B -TargetOwner B -CoreLoad off -RawRegistration on
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1 -RawOwner B -TargetOwner B -CoreLoad off -RawRegistration off
```

The runner supports sixteen owner/load/registration combinations. Each checks
three actual A/B/C order pairs, unchanged A target binding before EOF (no AUTO),
actual registration/module state in each phase, preserved receiver/target identity,
six incomplete session cleanups, 27 pre-start CLI rejections and ten optional/
default/reordered cases. Native tests verify both modes, off timer/barriers,
preexisting registration conflicts, post-ready foreign registration faults and
preservation, repeated disposal and on's existing target/Hook/lifecycle contracts.
Managed native receiver regressions continue using default Probe_Create.
Debug/x64 strict native build, both native self-tests, all sixteen subprocess
combinations (48 A/B/C pairs) and managed --raw-input-tests passed on 2026-10-09.
Physical SendInput and HUMAN acceptance are NOT RUN by the development agent.

## Three fresh manual sessions

FIX process start A-B-C, Hook install C-B, target B, receiver B and B Core OFF.
Change only registration ON/OFF/ON. Keep privilege/external apps unchanged;
close all old probes each round. A in ALL rounds:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeInputProbe.exe --managed-target
```

Sessions 1/3, separate B/C terminals:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner B --core-load off --raw-registration on
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c --raw-owner B --target-owner B --core-load off --raw-registration on
```

Session 2, fresh B/C:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner B --core-load off --raw-registration off
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c --raw-owner B --target-owner B --core-load off --raw-registration off
```

Wait B PREPARED/COMMANDS before starting C. Wait BOTH before C install; await C
INSTALLED, then B install/INSTALLED. B always target=1, receiverReady=1, Raw DLL
loaded; C target=0/receiverReady=0, no Raw/Core. Neither B nor C loads Core.
On B rawRegistrations=1; off B=0 with live Raw window/thread/timer. C always=0.

Paste B target HWND/PID to A; perform HUMAN BEFORE in target and another app,
checking normal shortcuts. B baseline -> C baseline -> A next. Focus B white
target during the FIVE-second countdown, release buttons/keys and remain hands
off all four AUTO rounds. At A HUMAN AFTER: B snapshot -> C snapshot BEFORE
human checks. Then HUMAN AFTER -> A done -> B/C snapshot again -> B done -> C
done. If actual input intrudes during AUTO, report interference rather than
software misclassification. Return NINE FULL logs grouped ON/OFF/ON, including
module hashes, timing, per-key target/Hook evidence and cleanup, plus normal
operation/accidental input confirmation. Do not manually press F8/B during AUTO.

## Predeclared interpretation

| Complete sends/targets and correct ownership/mode/order/hash/lifecycle | Meaning |
|---|---|
| Missing / complete / missing | Keyboard registration condition participates in this fixed topology; not proof that Raw API alone is faulty |
| Missing / missing / missing | Registration is not necessary here; next reduce retained target/Hook relationship |
| Both ON controls do not reproduce missing | Baseline unstable or changed; audit before causal inference |
| Only some observers recover | Preserve each A/B/C callback and tagged-key result separately |
| Wrong registration, hash, send, target, fault, order or interference | Inconclusive; fix experiment evidence first |

All observers share Windows low-level Hook infrastructure. This control neither
classifies production Raw packets nor authenticates human origin. Do not merge
older full-Raw data into these sessions; automatic integration stays closed.
