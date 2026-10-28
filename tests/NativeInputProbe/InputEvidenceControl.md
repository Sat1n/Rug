---
id: rug_input_evidence_control
type: logic_node
inputs: [rug_core, rug_native_hook_callback_control]
outputs: []
tags: [diagnostics, input, evidence, isolation]
---

# Module association and native send evidence control

## Contract repair, not a root-cause conclusion

The d1a7643 native callback experiment supplied the main executable's module to
SetWindowsHookExW although its callback belonged to Rug.NativeKeyboardHook.dll.
The diagnostic now resolves the callback address with GetModuleHandleExW
FROM_ADDRESS | UNCHANGED_REFCOUNT, verifies that it belongs to the loaded DLL,
and uses that module by default. Window class/creation still use the original
main module. `--hook-module original` deliberately retains the old parameter
for a fresh-process reverse control; only keyboard-native-callback accepts it.
Output includes address, actual callback module/path, window instance, selected
Hook hmod, thread/generation, installation result and faults. No automatic
reinstallation or Hook check removal exists. See
[SetWindowsHookExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw).

A/B are distinct observer implementations sharing the Windows low-level Hook
mechanism, not two independent sensors. The original managed failure predates
the callback DLL; recovery here would not close that failure.

## Three separately reported evidence lanes

1. **SEND_EVIDENCE:** native Win32InputController::EmitKey records actual
   SendInput/PostMessage requested/returned counts, immediate failure error,
   before/after QPC ticks, sending thread, round and tested key/down/up.
   Successful insertion/posting is not target processing. GetLastError does not
   reliably identify UIPI; see
   [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).
2. **TARGET_EVIDENCE:** target protocol v2 reports F8 and B separately, each with
   down/up counters including WM_SYSKEYDOWN/UP. A requires exactly 1/1 for the
   current tested key; unrelated keys, missing releases and duplicates fail.
   These are aggregate target message counters, not authenticated input origin.
3. **HOOK_EVIDENCE:** retain callback/actionable/tag/injected intersections and
   per-tested-key software counts. Target delivery never substitutes for a
   missing Hook callback. Pump health also does not prove Hook health.

The native count-only Raw receiver remains unchanged and reads no packets:
Raw software/candidate evidence is **NOT COLLECTED**, not zero. Historical full
Raw results cannot be combined with this session to claim complete acceptance.

## Private recording transport

InputSendEvidence is disabled unless a native diagnostic host explicitly creates
a same-process named memory mapping before creating the input controller.
The producer checks magic/version, owning PID and frequency. It records at most
16 detached scalar records, publishes after copying, never overwrites and exposes
overflow. The reader snapshots after the compound action, printing nothing
between key down/up. Session destruction restores the environment and closes its
mapping; controller destruction releases its view/handle. No C-ABI export,
input method, scan code, delay, retry or ABI status mapping changes. This transport
only adds diagnostic observation, not input authority. Other emitters are not
instrumented by this keyboard experiment. Missing records cannot pass.

`-BuildCore` builds Debug/x64 Rug.Core and deploys that DLL from the standalone
project output to the probe's x64/Debug directory; existing native dependencies
must already be present. This new DLL differs from historical binaries. Freeze
all binaries across the following three rounds and compare reported paths/hashes.

## Original → callback DLL → original

First close all diagnostic processes and build once:

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode Test -BuildCore
dotnet build F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -warnaserror
```

For EACH round, fresh A FIRST:

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode ManagedTarget
```

When A requests B's HWND/PID, fresh B SECOND (rounds use original, callback,
original respectively; change ONLY the final option):

```powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --hook-isolation-stage keyboard-native-callback --hook-module original
```

Keep elevation, external applications and process/registration order fixed.
Copy B's MANAGED TARGET HWND/PID into A. HUMAN BEFORE: use B's white window and
another app for keys/mouse and ordinary shortcuts. B `baseline`, then A `next`;
focus B by its title bar within five seconds, release all input, then hands off
until all four AUTO rounds finish. B `snapshot`; repeat HUMAN AFTER in the same
session. A `done`, then B `snapshot` and `done`. Send all SIX full logs plus
normal-operation confirmation and any accidental AUTO interaction. Verify clean
thread exit, attached=0 and registrations=0 in every round.

| Predeclared result | Interpretation / next action |
|---|---|
| Original gap → callback recovery → original gap; sends/targets complete | Module parameter correlates with recovery in this native callback variant; separately retest original managed failure |
| All three retain Hook gaps with sends/targets complete | Module repair insufficient; next fix process startup order and separately control Hook install/Raw registration order |
| Only one observer changes | Compare installation/module identity and Hook-chain evidence before choosing another control |
| Send/target partial, fault, hash/registration mismatch or AUTO interference | Inconclusive; correct that evidence gap before interpreting Hook results |

If controlled installation order still reproduces, move B's observer into C
while retaining B's target/Raw registration; then consider minimal pure C++
roles using the same Rug.Core sender. These structural controls change multiple
conditions and are not strict single-variable tests. Message-loop changes need
a specific supporting hypothesis. No unmarked input is certified human input.

## Verification boundary

User paired original/callback/original results retained both Hook gaps with
complete sends/specific targets. Next: [controlled initialization phases](StartupOrderControl.md),
including the limits of the unchanged Raw receiver's combined startup/registration.

Strict x64 native/managed/WinUI builds and deterministic native tests pass.
Tests cover complete/partial/failed/wrong-key/backend/overlap evidence, bounded
overflow, detached rounds, foreign mapping identity, specific target normal/system
messages and real Rug.Core PostMessage success/failure on self-owned windows.
Both hmod selections have three registration/Hook/thread cleanup lifecycles.
Existing Lua/Gateway/grant/arbitration regressions pass. Physical SendInput and
the paired three-round HUMAN/AUTO control are **NOT RUN by the agent**.
Production Raw event-source acceptance and the Hook discrepancy remain separate;
automatic takeover stays off until the original failed scenario is closed.
