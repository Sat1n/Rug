---
id: rug_core_load_control
type: logic_node
inputs: [rug_target_owner_control, rug_native_input_probe]
outputs: []
tags: [diagnostics, hooks, module, control]
---

# B diagnostic Core load on / off / on

## Reviewed target-owner evidence (2026-10-09)

The user returned nine target B/C/B logs, Raw/Core retained B, fixed C-B Hook
order. All preparation preceded installations; baseline preceded sends and first
snapshots followed sends. A bound the selected target HWND/PID each time, including
C hwnd=0xa05c4, pid=54620 in session 2. Eight key sends per session returned one;
target F8/B down/up=1/1 in each delivery mode, 2/2 cumulative per key. DLL hashes
unchanged, no CLR, binding/parse/thread/registration fault, or cleanup leak.

| Target owner | C install QPC | B install QPC | Tagged keyboard A/B/C |
|---|---|---|---|
| B | 34835595474..34835595570 | 34866851272..34866851361 | 0/0/0; A AUTO callbackTotal=0 |
| C | 37053454272..37053454404 | 37089513230..37089513320 | 4/4/4; F8/B each down/up=1/1 |
| B | 38566927198..38566927281 | 38609434207..38609434304 | 0/0/0; A AUTO callbackTotal=0 |

A retained ten tagged injected mouse events per AUTO; no unflagged AUTO events
observed. Human keyboard callbacks before/after were 123/64, 184/122, 157/72.
Raw received/cleaned 361/361, 361/361, 425/425; final registrations zero. Human
normal-operation confirmation still belongs to the user. Missing Hook evidence
is INCONCLUSIVE for acceptance, despite complete sending and target processing.

Moving target restored observation while Core/Raw and B-last Hook stayed B;
that B-only combination alone is insufficient here. Target movement changes
foreground process and startup/thread placement too; it is not proof of a Raw
API cause. Raw software/candidate classification was NOT COLLECTED. Hooks share
one observation mechanism. Original integrated failure and automatic acceptance
remain open; takeover stays disabled.

## Test-only load contract

[RunRole](NativeRoles.cpp#function:RunRole) accepts --core-load on|off, default on.
This controls ONLY diagnostic B's initial Module load, not A's sender. On mode
loads the same Core in B BEFORE target/Hook/Raw preparation, without creating an
input controller. Off omits that load entirely; no in-session unload/reload.
C NEVER loads Core in either mode. Snapshot checks actual module presence and
prints coreLoad (requested) and coreLoaded (observed) every phase; unexpected
pre-existing or later Core presence fails the session. On B retains path/bytes/
SHA256 evidence; off B has NO ROLE MODULE Core record. Both B/C select the same
mode for metadata consistency. No sender, Hook/Raw DLL, C-ABI or production changes.

Invalid/missing/duplicate Core options, unknown options and --test with options
reject before startup. Options are independent and order interchangeable; old
commands default on. --raw-owner/--target-owner still default B. Loading Core
also loads its dependencies/initialization; an on/off difference would concern
that whole load condition, not identify one DLL routine as root cause.

## Build and deterministic verification

Build ONCE before sessions, no -BuildCore:

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode Build
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --test
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1 -RawOwner B -TargetOwner B -CoreLoad on
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1 -RawOwner B -TargetOwner B -CoreLoad off
```

The contract runner supports all eight Raw/target/Core combinations: actual module
records and observed presence at PREPARED/INSTALLED/snapshot, preserved target/Raw
identities, separate threads, one-shot installation clocks, real unchanged A
binding then EOF BEFORE next, clean teardown, six incomplete sessions, twenty-two
invalid CLI cases and eight legacy/default/optional CLI checks per variant.
Automated checks emit no physical SendInput and cannot replace manual acceptance.
Native self-tests retain specific target metrics, background Core messages, fault,
layout and ownership checks. Only role EXE changes; [baseline DLL hashes](StartupOrderControl.md)
and A hash E323A5F1F2FD784D2BDEEE35E03CF5D50944A37DAC2775D907182AF1EAF27A06
stay unchanged. Manual on/off/on input is NOT RUN / pending user.

## Three fresh manual sessions

FIX process START order A-B-C, Hook INSTALL C-B, target B, Raw B, and A's same
Core sender in ALL sessions. Keep elevation/external apps unchanged. Close old
probes before each session; never toggle load within a process. A every session:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeInputProbe.exe --managed-target
```

Sessions 1/3, separate B/C terminals:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner B --core-load on
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c --raw-owner B --target-owner B --core-load on
```

Session 2, separate B/C terminals:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner B --core-load off
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c --raw-owner B --target-owner B --core-load off
```

Wait B PREPARED/COMMANDS before starting C; WAIT BOTH before ANY install. All
phases: B target=1, rawRegistrations=1, Raw module/readiness; C target=0,
rawRegistrations=0, no Raw/Core. Both targetOwner=B/rawOwner=B, clrLoaded=0.
Sessions 1/3: both coreLoad=on, B coreLoaded=1/Core hash, C coreLoaded=0.
Session 2: both coreLoad=off/coreLoaded=0, neither has Core module load record.

C install -> WAIT C INSTALLED -> B install -> WAIT B INSTALLED, every session.
C afterTicks < B beforeTicks, both later than both PREPARED observedTicks. Paste
B target HWND/PID into A. HUMAN BEFORE in B and another app; verify shortcuts.
B baseline -> C baseline -> A next, focus B white window in five seconds, release
all keys/buttons and remain hands off through all four AUTO rounds. At A HUMAN
AFTER: B/C snapshot BEFORE human checks. HUMAN AFTER -> A done -> B/C snapshot
again -> B done -> C done. A MUST still print CORE LOADED plus complete SEND_CALL
records in session 2: its sending implementation is unchanged.

Return nine complete logs grouped B Core on/off/on, plus ordinary-operation and
accidental-input confirmation. This experiment does not certify human origin or
production Raw classification; absence of events is not proof of filtering.

## Predeclared interpretation

| Complete sends/targets, correct load/owner/order/hash/lifecycle evidence | Meaning |
|---|---|
| Missing / missing / missing | B's diagnostic Core load not necessary for this native gap; simpler Raw/Hook/target reproduction remains, without proving a Windows root cause |
| Missing / complete / missing | B's load condition participates in this topology; investigate its dependencies/initialization, not A's emission algorithm |
| On controls do not both miss | Baseline not reproduced stably; audit experiment/environment before causal inference |
| Some observers recover | Keep A/B/C callbacks and tagged-key evidence separate |
| Load/order/target/send/hash/fault mismatch or AUTO interference | Inconclusive; repair validity first |
