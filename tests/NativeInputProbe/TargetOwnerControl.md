---
id: rug_target_owner_control
type: logic_node
inputs: [rug_raw_owner_control, rug_native_input_probe]
outputs: []
tags: [diagnostics, hooks, target, ownership]
---

# Target owner B / C / B control

## Evidence motivating this control (2026-10-08)

The user returned nine fixed C-B Hook-order logs, Raw owner B/C/B. All preparation
preceded installs, C installed before B, both installed/baselined before sends,
snapshots followed sends. Every session's eight key sends returned one; specific
SendInput/PostMessage F8 and B target down/up were 1/1 per mode. Same DLL hashes,
no CLR, actual Raw ownership correct, no parse/thread/registration/cleanup fault.

| Raw owner | C install QPC | B install QPC | Tagged keyboard A/B/C |
|---|---|---|---|
| B | 96562705537..96562705643 | 96591507457..96591507555 | 0/0/0; A AUTO callbackTotal=0 |
| C | 98582685859..98582685975 | 98612036686..98612036786 | 4/4/4, F8/B each down/up 1/1 |
| B | 100315391311..100315391398 | 100348771700..100348772033 | 0/0/0; A AUTO callbackTotal=0 |

A mouse AUTO retained ten injected events each session, with no unflagged AUTO
events observed. Human before/after keyboard callbacks persisted (131/100,
182/91, 130/76). Raw received/cleaned 406/406, 322/322, 335/335; final registrations
zero. Missing Hook evidence remains INCONCLUSIVE for acceptance despite complete
sends/target delivery. Normal operation still needs user confirmation.

Combined with the earlier fixed B-C owner comparison (all complete), this narrows
correlation to a combination of B-last installation and Raw/target/Core in B.
Neither B-last nor Raw-owner-last alone explains the observations. Raw attribution
was NOT COLLECTED; shared low-level Hooks are not independent sensor mechanisms.
Original integrated failure remains open, automatic takeover disabled.

## Contract and experiment boundary

[RunRole](NativeRoles.cpp#function:RunRole) now accepts independent --raw-owner
and --target-owner B|C; both default B. With the default --core-load on, Core
stays in B. The separate [Core load control](CoreLoadControl.md) can omit B's
diagnostic load; C always excludes Core. ONLY the selected
target owner creates/shows the same [RoleWindow](NativeRoles.cpp#class:RoleWindow)
with unchanged class prefix/title, procedure, dedicated thread and metric v2
protocol. Target, Hook and Raw threads remain separate when co-located. Both
roles print targetOwner and actual target presence every phase. Owner verifies
the target is live; C continues rejecting any Core load. Both roles must specify
the SAME two owner values; local validation cannot validate foreign registrations.

Invalid values, missing/duplicate/unknown flags and --test owner options reject
before module/thread/window startup. Flag order is interchangeable; prior CLI
commands/defaults remain valid. No sender, Hook/Raw DLL, C-ABI, production input,
grant or takeover code changes. A's existing messages still say "B"/"MANAGED";
these are historical labels, NOT a hard-coded B binding: paste the selected
owner's actual diagnostic HWND/PID and keep that process alive/foreground.

Moving target also moves its native thread/window and foreground process, and
changes target creation relative to that role's Hook/Raw preparation. This
control cannot distinguish target ownership from foreground-process relation
or all startup differences, nor establish a Windows/Raw API root cause.

## Build and automated checks

Build once BEFORE starting the sessions, no -BuildCore:

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode Build
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --test
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1 -RawOwner B -TargetOwner B
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1 -RawOwner B -TargetOwner C
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1 -RawOwner C -TargetOwner B
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1 -RawOwner C -TargetOwner C
```

Each variant covers three actual A/B/C install-order pairs, six incomplete-session
cleanups; current runner includes 32 invalid CLI and 12 default/optional checks.
Assertions check actual target/Raw/module ownership, separate thread identities,
prepared-before-install QPC, preserved target HWND/thread/generation and Raw
HWND/thread, one-shot installs and cleanup. UNCHANGED A binds the selected target,
then EOF BEFORE next; no physical input or human acceptance inferred. Existing
role self-tests check target-specific messages/layouts, Raw conflicts and faults.

Core/Raw/Hook hashes must retain [the baseline](StartupOrderControl.md); A EXE
stays E323A5F1F2FD784D2BDEEE35E03CF5D50944A37DAC2775D907182AF1EAF27A06.
Only the role EXE changes. The user's target comparison reproduced
missing/complete/missing; [reviewed clocks and next Core load control](CoreLoadControl.md)
record the evidence. Core on/off/on also missed in all three sessions; the next
[registration control](RawRegistrationControl.md) missed/recovered/missed. The next [notification control](DeviceNotifyControl.md) is pending. Historical fixed
hashes here apply to the target experiment; the new test receiver is rebuilt and
must use one matching hash across that new registration series.

## Three fresh manual sessions

FIX process START order A-B-C, Hook INSTALL order C-B and Raw owner B in ALL
sessions. Elevation/external programs stay fixed. Close old probes. A every session:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeInputProbe.exe --managed-target
```

Sessions 1/3: B then C in separate terminals, waiting each PREPARED/COMMANDS:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner B
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c --raw-owner B --target-owner B
```

Session 2: B then C in separate terminals:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner C
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c --raw-owner B --target-owner C
```

Check BOTH PREPARED: targetOwner agrees (B/C/B); B rawOwner=B, registrations=1,
Core/Raw loaded, nonzero Raw readiness; C rawOwner=B, registrations=0, no Core/Raw.
Only owner has target=1/NATIVE TARGET/TARGET KEYS; both clrLoaded=0, hookInstalled=0.
WAIT BOTH prepared, C install -> WAIT C INSTALLED -> B install -> WAIT B INSTALLED.
C afterTicks < B beforeTicks, both later than both preparation timestamps.

Paste selected target's HWND/PID to A: B in sessions 1/3, C in session 2. HUMAN
BEFORE in that white target and another app; confirm shortcuts. B baseline, C
baseline, A next. During five seconds focus THAT target, release keys/buttons,
hands off through all four AUTO rounds. When A reaches HUMAN AFTER, B snapshot
and C snapshot BEFORE human checks. HUMAN AFTER -> A done -> B/C snapshot again
-> B done -> C done. Return nine complete logs grouped target B/C/B and confirm
ordinary operation/accidental input. Never bind/focus the old B target in session 2.

## Predeclared branches

| Valid sends/targets/clocks/hashes/ownership/lifecycle, fixed C-B and Raw B | Meaning |
|---|---|
| Missing / complete / missing | Supports target/foreground co-location in Raw B as part of current missing combination; Core/Raw/Hook B alone not sufficient here |
| Missing / missing / missing | Target/Raw co-location not necessary here; next isolate Core load or other B process conditions |
| All complete or B-target controls differ | Missing baseline not stably reproduced; audit preparation/environment before more changes |
| Partial observer recovery | Preserve separate A/B/C callback/tagged-key evidence, not a combined verdict |
| Send/target/order/owner/hash/fault mismatch or AUTO interference | Inconclusive; repair validity first |
