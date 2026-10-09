---
id: rug_device_notify_control
type: logic_node
inputs: [rug_raw_registration_control, rug_native_input_probe]
outputs: []
tags: [diagnostics, raw-input, device-notify, control]
---

# Keyboard device-notification flag on / off / on

## Reviewed registration reversal (2026-10-09)

Nine fresh A/B/C logs completed registration on/off/on with B Core OFF,
target/receiver B, startup A-B-C and Hook installation C-B. Both PREPARED
preceded C installation, C completed before B started installation, baselines
preceded emission and snapshots followed it. Every session had eight native
send records returning one, specific F8/B target down/up=1/1 per mode and 2/2
cumulative. Core, A and Hook DLL hashes stayed fixed.

| Registration | B / C / A PID | A/B/C tagged injected keyboard | A AUTO callbackTotal | Receiver input/cleanup |
|---|---|---|---|---|
| On | 22368 / 25352 / 55480 | 0/0/0 | 0 | 344/344 |
| Off | 57352 / 56100 / 56940 | 4/4/4; F8/B each 1/1 | 4 | 0/0 |
| On | 55556 / 14896 / 48032 | 0/0/0 | 0 | 388/388 |

Off retained a live receiver window/thread and advancing timer (361 -> 481 ->
510). A mouse retained ten tagged injected events each session; human keyboard
before/after counts were 124/65, 164/114, 144/61. No reported parse, thread or
ownership faults; final registrations zero. This associates keyboard registration
with the gap in this topology; it does not prove a Raw API root cause or a
production event-source failure. Raw attribution was NOT COLLECTED. Normal
operation still needs user confirmation; automatic takeover remains closed.

## Small next control

[RunRole](NativeRoles.cpp#function:RunRole) adds --device-notify on|off (default
on), independently of existing owner/load/registration selections. This manual
series ALWAYS registers keyboard usage 6 with INPUTSINK. Only DEVNOTIFY changes:
on=0x2100 (8576), off=0x0100 (256). Never NOLEGACY. Same receiver DLL, dedicated
thread, message-only window, 250ms timer, count-only WM_INPUT cleanup, target,
sender and Hook installation order. No parsing, filtering, retries, rehooking,
extra input or takeover. Flags are fixed for the receiver lifetime.

[Receiver](RawReceiver.cpp#class:Receiver) preserves six legacy exports,
Probe_CreateUnregistered and the 96-byte snapshot. Two additional TEST exports
(nine total), Probe_CreateConfigured and Probe_ReadRegistration, provide fixed
options and a detached 56-byte record. The record is captured at the actual
RegisterRawInputDevices call: requested flags, QPC before/after, success,
immediate failure error, OS thread and target HWND. In unregistered controls all
record fields remain zero; no registration/removal occurs. Existing managed
consumers retain Probe_Create's registered/DEVNOTIFY default.

RAW_REGISTER_CALL shows that record separately from queriedFlags. The system
registration query may omit DEVNOTIFY; the host checks INPUTSINK and ownership,
not equality of queriedFlags with requestedFlags. Each phase verifies actual
call evidence and receiver identity. It never registers again for snapshots or
Hook installation. STOPPED retains the archived call but reports query flags
zero after removal. Message barriers/timers do not certify Hook delivery.

The receiver has been rebuilt; ALL THREE rounds need the same new DLL SHA256:
DDF83DE04B64F57EAD678BA7E9D42CCB37FA5E440F3DF14C7AFF482C60350754.
Do not reuse the previous registration-series hash as the new baseline. Core,
A and Hook DLL keep [preceding fixed hashes](CoreLoadControl.md).

## Automated verification

Build once, without -BuildCore, then no builds between manual rounds:

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode Build
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --test
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1 -RawOwner B -TargetOwner B -CoreLoad off -RawRegistration on -DeviceNotify off
```

Debug/x64 native /W4 /WX build, role/self-tests, managed --raw-input-tests and all
32 owner/target/load/registration/notification subprocess combinations passed.
Each combination checks three A/B/C pairs (96 total), actual registration call
flags/results/clock/identity, unchanged record after installation, lifecycle,
six incomplete cleanups, 32 invalid CLI cases and 12 default/optional cases.
Native self-tests cover both notification modes, conflicts, foreign-owner
preservation, timer/barriers, invalid constructors and repeated disposal.
Real Core PostMessage self-tests passed; physical AUTO/HUMAN for this new
notification control are NOT RUN by the agent. No production/UI change.

## Three fresh manual sessions

Keep external applications, privilege, process startup A-B-C, Hook install C-B,
Raw B, target B and B Core OFF fixed. Close all three processes between sessions.
Use three terminals. A is identical EVERY round:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeInputProbe.exe --managed-target
```

Sessions 1 and 3: start B, wait PREPARED/COMMANDS, then start C:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner B --core-load off --raw-registration on --device-notify on
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c --raw-owner B --target-owner B --core-load off --raw-registration on --device-notify on
```

Session 2 uses fresh B/C with only device-notify changed:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner B --core-load off --raw-registration on --device-notify off
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c --raw-owner B --target-owner B --core-load off --raw-registration on --device-notify off
```

1. Wait BOTH PREPARED. C: install, wait INSTALLED. B: install, wait INSTALLED.
   All rounds B target=1/receiverReady=1/rawRegistrations=1/Core absent. C owns
   no target/Raw/Core. B RAW_REGISTER_CALL requestedFlags=8576/256/8576 across
   rounds, succeeded=1/error=0, ordered clocks and matching Raw thread/HWND.
2. Paste B's target HWND/PID into A. HUMAN BEFORE: target and another app,
   keyboard/mouse and normal shortcut checks. B baseline -> C baseline -> A next.
3. During FIVE-second countdown focus B's white target, release keys/buttons;
   hands off all four AUTO modes/keys. Never manually press F8/B during AUTO.
4. At A HUMAN AFTER, first B snapshot -> C snapshot BEFORE human checks. Then
   HUMAN AFTER -> A done -> B/C snapshot again -> B done -> C done.
5. Return NINE FULL logs grouped notification ON/OFF/ON, including hashes,
   actual registration call, per-key target, all Hook phases and cleanup. Also
   report normal shortcut behavior and any accidental input during AUTO.

Raw input/device/cleanup counts remain aggregates only; software/candidate
classification is NOT COLLECTED in these sessions. Device notifications may
change as intended, but their presence/absence is not a candidate verdict.

## Predeclared branches

| Complete sends/targets; correct call flags/order/hash/ownership/cleanup | Interpretation |
|---|---|
| Missing / complete / missing | DEVNOTIFY condition participates here; not proof the Raw API alone causes failure |
| Missing / missing / missing | DEVNOTIFY not necessary here; focus retained INPUTSINK registration/target/Hook relationship |
| ON controls no longer reproduce | Baseline changed/unstable; audit before inference |
| Only some observers recover | Retain each A/B/C entry, classification and per-key result separately |
| Wrong flags, target, send, fault, order, hash or interference | Inconclusive; repair evidence first |

Hooks share Windows low-level Hook infrastructure. No conclusion from a reduced
control closes the original integrated failure. Do not combine historical parsed
Raw evidence with these count-only rounds or enable automatic takeover.
