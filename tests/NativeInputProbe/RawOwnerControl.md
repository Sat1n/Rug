---
id: rug_raw_owner_control
type: logic_node
inputs: [rug_native_roles_control, rug_native_input_probe]
outputs: []
tags: [diagnostics, hooks, raw_input, ownership]
---

# Keyboard Raw registration owner comparison

## Reviewed pure-native evidence (2026-10-08)

The user returned nine native A/B/C logs, B-C / C-B / B-C install order, no CLR
in B/C and unchanged Core/Raw/callback DLL hashes:

| Round | B install QPC | C install QPC | A/B/C tagged software keyboard counts |
|---|---|---|---|
| B-C | 45341703973..45341704057 | 45377750413..45377750511 | 4/4/4, F8 and B each down/up 1/1 |
| C-B | 47053720738..47053720823 | 47017481740..47017481820 | 0/0/0; A per-key callbackTotal=0 |
| B-C | 48851635853..48851635941 | 48902884021..48902884262 | 4/4/4 again, both keys 1/1 |

All preparation preceded installation; installs preceded emission. Sends and
specific target processing were complete. Human callbacks before/after persisted,
including the missing middle round. Raw input/cleanup were 368/368, 305/305,
376/376; final registrations zero, no faults or thread mismatch. A flagged no
AUTO interference. This reproduces order correlation without managed hosts:
CLR/managed callbacks are not necessary for this topology, not a Windows root
cause or original integrated acceptance. Raw attribution remains NOT COLLECTED;
Hooks share one mechanism. Ordinary operation needs the user's confirmation.

## Ownership contract

Both native roles accept `--raw-owner B|C`, default B. Invalid values, misplaced
options, mixed roles and use with --test reject BEFORE module/thread/window
startup. [RunRole](NativeRoles.cpp#function:RunRole) retains target and Core
exclusively in B, one deferred keyboard Hook in each B/C, and creates
[RawHost](NativeRoles.cpp#class:RawHost) ONLY in the selected owner. The owner
loads the same receiver DLL and calls its unchanged Run on one native thread:
keyboard usage 6, INPUTSINK|DEVNOTIFY, 250ms timer, no packet reading, original
DefWindowProc cleanup. No receiver or callback DLL source changes/rebuild.

Every phase includes rawOwner, actual local rawRegistrations and readiness QPC.
Owner verifies live HWND/usage/flags before install/snapshot; nonowner verifies
zero registrations and no receiver module. C never loads Core or creates a target.
Target/Hook/Raw threads in an owner are distinct. Both roles must prepare before
either Hook. Duplicate install preserves timestamps; no registration transfer or
reinstallation within a session. Stop joins/clears owned resources. Incomplete
EOF/uninstalled done remains NOT RUN with cleanup.

Moving owner relocates DLL load, receiver host/thread and registration together.
Raw now belongs to a background process while target B is foreground; readiness
relative to preparing C's Hook window also changes. These are explicit parts of
the ownership control; differences cannot be attributed solely to one Raw API.
Production Raw/input/grants/takeover/C-ABI stay unchanged. B/C emit no input and
store no human sequence or trajectory. Timers/barriers do not prove Hook health.

Both B AND C must receive the SAME --raw-owner value. Local validation cannot
inspect foreign-process Raw ownership: check BOTH PREPARED logs against the table
below before AUTO. A wrong pair is inconclusive, not a valid negative result.

## Build and automated verification

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode Build
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --test
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1 -RawOwner B
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1 -RawOwner C
```

Strict x64 build and both test runs cover six real A/B/C order pairs, twelve
incomplete-session cleanups and eighteen pre-start CLI rejection cases. They
assert actual owner/nonowner module/registration counts, Core/target retained B,
awaited Raw readiness, preserved Raw HWND/thread, prepared-before-install QPC,
one-shot/duplicate install, unchanged A binding and final cleanup. No next/AUTO
or physical SendInput; software/HUMAN acceptance is not inferred. Existing native
lifecycle/fault/registration-conflict tests remain.

Core/Raw/Hook hashes must match [the baseline](StartupOrderControl.md). A EXE
remains `E323A5F1F2FD784D2BDEEE35E03CF5D50944A37DAC2775D907182AF1EAF27A06`.
Only the role EXE changes. Build once, no -BuildCore, before starting sessions.

## Manual owner B / owner C / owner B sessions

FIX Hook order B-C for ALL THREE sessions. Keep A-first/B-second/C-third startup,
elevation/external programs, emission and target B unchanged. Close old probes;
fresh processes per session. Use direct EXE commands below; runner NativeB/NativeC
modes select default owner B.

A first, wait at HWND/PID prompt:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeInputProbe.exe --managed-target
```

Sessions 1 and 3: B then C, each in its OWN terminal:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c --raw-owner B
```

Session 2 ONLY: B then C, each in its OWN terminal:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner C
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c --raw-owner C
```

Wait B PREPARED/COMMANDS before starting C; wait C PREPARED/COMMANDS before ANY
install. Required metadata:

| Session | B | C |
|---|---|---|
| 1 and 3 | target=1, rawOwner=B, rawRegistrations=1, receiver module present | target=0, rawOwner=B, rawRegistrations=0, no receiver |
| 2 | target=1, rawOwner=C, rawRegistrations=0, no receiver | target=0, rawOwner=C, rawRegistrations=1, receiver module present |

Both clrLoaded=0, hookInstalled=0, no fault; owner rawReadyObservedTicks nonzero.
B: install, WAIT B INSTALLED; C: install, WAIT C INSTALLED. B afterTicks must be
less than C beforeTicks, both later than both PREPARED observedTicks. Do NOT
reverse Hook order in session 2; only the requested Raw owner changes.

Copy B NATIVE TARGET HWND/PID to A. HUMAN BEFORE in B and another app, check normal
shortcuts. B baseline; C baseline; A next; focus B's white window in five seconds,
release keys/buttons and remain hands off through all four rounds. On A HUMAN
AFTER: B snapshot; C snapshot BEFORE human checks. Then HUMAN AFTER; A done;
B snapshot; C snapshot; B done; C done. Owner input must equal cleanup; final
registrations zero, all Hook/target/thread lifetimes clean in both roles.

Return NINE logs grouped owner B / owner C / owner B, plus normal-operation and
accidental-input confirmation. Raw classification remains NOT COLLECTED; broad
B/C console counts are not A AUTO interference evidence.

## Predeclared branches

| Complete sends/targets, correct ownership/clocks/lifecycle/hashes | Interpretation |
|---|---|
| B owner complete; C owner missing; B owner complete | Reverse-confirmed correlation with moving receiver to last Hook installer; target/Core co-location in that installer is not necessary here; not a root cause |
| All complete | Receiver ownership alone does not produce gap under fixed B-C order; investigate last-installer target/Core/process interaction |
| Controls fail or restoration fails | No stable ownership inference; audit changed preparation/environment |
| Some observers recover | Preserve per-observer results and compare bindings/chain counts |
| Ownership/clock/hash mismatch, partial delivery, fault or AUTO interference | Inconclusive; fix experiment validity first |

Manual owner comparison is **NOT RUN by development agent / pending user**.
Automatic takeover remains disabled; original failure still needs closure.
