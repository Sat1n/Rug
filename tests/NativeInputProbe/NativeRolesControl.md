---
id: rug_native_roles_control
type: logic_node
inputs: [rug_manual_hook_order_control, rug_native_input_probe]
outputs: []
tags: [diagnostics, hooks, native, topology]
---

# Native B/C host comparison

## Reviewed manual-order evidence (2026-10-08)

The user returned all nine logs for B-C / C-B / B-C with fresh A/B/C processes.
Both B/C preparation preceded either Hook install. Actual install intervals were:

| Round | B Hook QPC | C Hook QPC | A/B/C tagged SendInput keyboard observations |
|---|---|---|---|
| B-C | 15145322887..15145325995 | 15233192847..15233195941 | Complete: four events each, F8/B down/up=1/1 |
| C-B | 17794656449..17794659819 | 17695562586..17695565880 | Missing: A per-round callbackTotal=0; B/C tagged=0 |
| B-C | 19592652462..19592655616 | 19635125819..19635129482 | Complete again: four events each, F8/B down/up=1/1 |

Frequency=10000000 in all processes. Every send returned the requested quantity;
specific target keys were 1/1 per delivery mode and 2/2 cumulative per key.
Human before/after callbacks remained present even in the missing middle round.
Raw input/cleanup were 340/340, 424/424, 355/355, with zero final registrations,
clean threads and unchanged Core/Raw/Hook hashes. No AUTO interference was reported
by A. Normal-operation confirmation still belongs to the user, not aggregate counts.

This is reverse-confirmed correlation with the final Hook installation context,
not a proven root cause. Raw software/candidate data were NOT COLLECTED. Low-level
Hooks share one Windows mechanism. Original integrated acceptance remains open.

The user's subsequent nine pure-native logs reproduced complete/missing/complete
in A/B/C without CLR; sends/targets and cleanup remained complete. Exact clocks
and the next [Raw owner B/C/B comparison](RawOwnerControl.md) are recorded there.
Managed hosting is not necessary for that correlation in this topology.

## Replacement boundary

`Rug.NativeRoleProbe.exe` is a NEW diagnostic executable, separate from unchanged
A's `Rug.NativeInputProbe.exe`. It never emits input or calls takeover. It replaces
both B/C managed hosts with native hosts; window classes/procedures, host thread
startup and readiness handoff change together. This is a STRUCTURAL comparison,
not a strict one-variable experiment and not a production event-source replacement.

B loads the SAME Rug.Core.dll without creating an input controller or sending.
The topology below describes the original/default roles. Independent Raw and
[target ownership options](TargetOwnerControl.md) now allow relocation while Core
remains B; defaults preserve this comparison. The subsequent [Core load control](CoreLoadControl.md)
can omit B's diagnostic module load only; C still never loads Core.
The later [registration control](RawRegistrationControl.md) retains receiver
infrastructure while omitting only keyboard registration; default remains on.
Its native target exposes the same title, diagnostic class prefix and v2 metric
protocol consumed by A's existing HWND/PID checks. Three distinct threads own
target, deferred keyboard Hook window, and Raw receiver. The
[RawHost](NativeRoles.cpp#class:RawHost) calls Rug.NativeRawReceiver.dll
on its own std::thread, keyboard-only flags INPUTSINK|DEVNOTIFY, 250ms timer and
WM_INPUT count/DefWindowProc cleanup. No GetRawInputData or candidate attribution.
The receiver's legacy registered constructor and 96-byte layout remain; its new
optional unregistered constructor is used only by the later native-role control.

C has only one deferred message-only keyboard Hook thread, no target, no Core,
no receiver and no Raw registration. Both use the SAME Rug.NativeKeyboardHook.dll
with [HookApi](NativeRoles.cpp#class:HookApi); no callback reimplementation.
[RoleWindow](NativeRoles.cpp#class:RoleWindow) installs WH_KEYBOARD_LL/13, thread=0,
callback-owning DLL hmod on the prepared thread. Main-module hmod is used only
for host windows. Module metadata includes loaded path/bytes/SHA-256; callback
address/module, bound thread, attachment, mismatch, QPC install before/after,
phase snapshots/deltas, tested synthetic keys and target keys are separate evidence.

Hook windows prepare without installing. Raw readiness is actual DLL/window/device
readiness. Install admission checks live target/Hook/Raw ownership, never waits
inside a callback for the console. Duplicate install retains timestamps and Hook.
Baseline/snapshot before install are refused. Faults and incomplete EOF/done
remain observable; unhook then detach on the owning thread precedes DLL unload.
Failed Hook detach/unhook retains its counter/module until process exit and fails
cleanup. Raw stops/joins before destroy/unload. Registration conflicts never
silently replace an owner. CLR absence is checked in every role snapshot.

All native B/C message loops use GetMessageW with no HWND filter. This does not
make loop liveness proof of Hook health. No human text/sequence/trajectory storage,
ignore interval, automatic reinstallation, input algorithm or C-ABI change.

## Build and deterministic validation

Requires Visual Studio v145 x64 tools and Windows SDK. Build once; do NOT pass
-BuildCore. Current binaries must retain the [recorded Core/Raw/Hook hashes](StartupOrderControl.md).
A's EXE hash for this comparison is
`E323A5F1F2FD784D2BDEEE35E03CF5D50944A37DAC2775D907182AF1EAF27A06`.

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode Build
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --test
& F:/Cardinal/Rug/Rug/tests/run_native_role_tests.ps1
```

The strict native build and tests cover DLL layouts, target protocol/specific keys,
one-shot installation, thread binding, Raw ownership/conflict, post-ready fault and
repeated cleanup. Subprocess tests run actual A/B/C in B-C/C-B/B-C order, verify
both preparation timestamps precede installations, unchanged Raw HWND/thread,
duplicate install, pre-install refusal, six incomplete-session cleanups, 27
invalid CLI rejections before module loading and ten optional/default CLI checks
per owner combination. A binds the real selected native target
using its unchanged validation, then EOF BEFORE next; no AUTO is emitted.
These tests do not replace physical input/HUMAN acceptance.

## Three fresh manual rounds

Use three fresh terminal processes per round, same elevation/external programs.
Build once before starting A. Run existing A FIRST (the label ManagedTarget refers
to the existing diagnostic protocol, not a requirement that B load CLR):

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeInputProbe.exe --managed-target
```

Wait at HWND/PID prompt. Native B SECOND:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b
```

Wait for NATIVE B PREPARED/COMMANDS, clrLoaded=0, target=1, rawRegistrations=1,
hookInstalled=0 and no fault. Native C THIRD:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-c
```

Wait for NATIVE C PREPARED/COMMANDS, clrLoaded=0, target=0, rawRegistrations=0,
hookInstalled=0. BOTH must prepare before ANY install. Install once in B-C,
C-B, B-C order, each command awaited until NATIVE role INSTALLED before the next.
Both actual installations must follow both PREPARED observedTicks and not overlap.

Copy B NATIVE TARGET HWND/PID into A. HUMAN BEFORE in B and another app; confirm
ordinary shortcuts work. B baseline; C baseline; A next; focus B's white window
within unchanged five-second countdown, release keys/buttons and remain hands off
through all four AUTO rounds. When A reaches HUMAN AFTER, B snapshot; C snapshot
BEFORE human checks. Then HUMAN AFTER; A done; B snapshot; C snapshot; B done;
C done. Each log must include final clean lifecycle/registration evidence.

Return NINE logs grouped B-C/C-B/B-C, plus normal-operation and accidental-input
confirmation. A's per-AUTO counters judge interference; B/C intervals include
console/focus activity. Broad unflagged counts alone do not label interference.
Raw classification stays NOT COLLECTED and cannot be spliced from an older round.

## Predeclared branches

| Valid sends/targets/lifecycle and fixed hashes | Conclusion / next investigation |
|---|---|
| B-C complete, C-B missing, B-C complete | Reproduced without CLR; managed hosting is not necessary in this topology; investigate shared native registration/Hook/process conditions |
| Both orders complete | Native hosting did not reproduce order sensitivity; narrow to changed host/window/thread conditions, without claiming CLR itself is faulty |
| Both orders missing | Native replacement lost even the recovered condition; compare changed topology/readiness before interpreting order |
| Only some observers recover | Compare each observer's counts, module/installation identity and chain timings separately |
| Hash/identity/clock/fault/target/send mismatch or AUTO interference | Inconclusive; repair experiment validity first |

Manual input is **NOT RUN by the development agent**; the user's native comparison
is recorded above. Both Raw owner comparisons are recorded in the owner guide;
the target owner comparison also reproduced missing/complete/missing. The new
Core on/off/on also missed in all three sessions. The next
[registration comparison](RawRegistrationControl.md) remains pending user execution.
Automatic takeover remains disabled; no reduced pass closes the original failure.
