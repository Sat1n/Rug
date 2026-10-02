---
id: rug_hook_comparison
type: logic_node
inputs: [rug_native_input_probe, rug_script_tests]
outputs: []
tags: [diagnostics, evidence, hooks]
---

# Keyboard Hook comparison evidence (2026-10-02)

These are user-run sessions, not agent-simulated human acceptance. No human key
sequences, text or mouse trajectories are retained here.

| Session | Native keyboard injection | Managed keyboard injection | Raw software keyboard | Result |
|---|---|---|---|---|
| Native Human alone | F8/B each 2, down/up each 1/1 | Not running | Not registered by native probe | Native four rounds PASS |
| Native Observe first, managed second | 0 tagged/injected | F8/B each 0 callbacks | F8/B each 2, down/up 1/1 | SendInput INCONCLUSIVE; PostMessage PASS |
| Managed first, native Observe second | 4 tagged/injected, F8/B each 1/1 | F8/B each 2 tagged/injected | F8/B each 2, down/up 1/1 | Both SendInput and PostMessage rounds PASS |

Mouse injected/tagged totals were 10 in both paired sessions (two SendInput
rounds). Automatic candidate counts were zero. Both observers continued to see
human keyboard activity afterwards. In the passing reversed-order session,
managed HUMAN BEFORE/AFTER callback deltas were 149/147; native post-snapshot
human delta was 140. Both threads exited with WM_QUIT and no reported fault;
managed Raw Input registrations remaining were zero.

The observations establish that the managed callback can receive and classify
these keys, and that two independent probes can miss the same injection while
Raw Input and target delivery remain complete. They do not establish a CLR bug,
permanent Hook removal, F8-only interference or Windows root cause. Sessions
also restarted processes; third-party Hook state was not controlled. Install
order is a useful correlation, not a proven cause. Normal shortcuts and window
operation still require the user's explicit visual confirmation.

## Source audit

Subsequent user runs repeated the same split: native-first missed keyboard
injection; managed-first passed. Exiting G HUB and then PowerToys did not remove
the native-first symptom. Both failures retained complete raw software down/up,
zero candidates, later human callbacks and clean teardown. These are user-reported
application-exit conditions, not independent proof that every associated service
was unloaded; no component is blamed. DLL SHA-256 remained unchanged. See
[controlled stages](ControlledStages.md) for the next test without guessing at
production key emission changes.

- Both callbacks count entry before parsing, read only HC_ACTION structures,
  distinguish tag and injected flags independently, and always return
  CallNextHookEx's result with unchanged arguments. No suppression branch exists.
- Managed delegates are instance fields kept alive through thread teardown;
  native callbacks are static functions. Both install keyboard/mouse separately,
  capture installation errors immediately and run a dedicated message loop.
- Keyboard flags/extra-info offsets are 8/16 in both x64 implementations. Native
  assertions and managed layout tests agree. Neither probe reinstalls hooks or
  suspends observation. Install status/barriers certify neither ongoing Hook
  validity nor delivery of every event.
- Managed target and hooks share one message thread; native target and Hook
  receiver use separate threads. Managed InputService dispatches ABI calls with
  Task.Run; native CoreInput calls ABI synchronously from the console harness.
  These are differences to control if evidence requires it, not known defects.
- Managed diagnostics explicitly load repository x64/Debug/Rug.Core.dll and bind
  imports to that handle. Native diagnostics load the executable-adjacent DLL,
  also repository x64/Debug in this build. No different-copy path was found in
  source. Both ultimately call the existing Win32InputController EmitKey path.

Microsoft specifies [chain-head installation and forwarding](https://learn.microsoft.com/en-us/windows/win32/winmsg/about-hooks)
and [low-level callback message-loop/timeout requirements](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc).
Our duration counters measure only callbacks that entered; they cannot locate
an upstream component that never forwarded an event. Later human callbacks
without reinstallation do not support a permanently removed Hook explanation.

## Module provenance and next controlled comparison

Managed startup now prints actual loaded module path, process ID, file size and
SHA-256 by matching the loaded handle to Process.Modules. Native CoreInput prints
its actual GetModuleFileNameW path, process ID and size. Its PowerShell runner
prints a **pre-run file** SHA-256; this is not an in-memory binary hash and not
proof of what an earlier session loaded. Observe loads no core DLL and must not
print a CORE LOADED line. Do not replace binaries during a diagnostic session.

The previous user outputs contain no loaded-module provenance, so their hashes
cannot be reconstructed as historical runtime evidence. New instrumentation
does not retrospectively prove them equal.

Before changing emitters, repeat the two documented installation orders in fresh
processes, preserve CORE LOADED/file fingerprints, and keep elevation/environment
constant. If the order correlation persists, the user can disable G HUB or
PowerToys **one at a time**, restarting diagnostics each round; neither component
has been identified as the cause. Do not automatically alter user applications.
No extra ignore interval, hook reinstallation or removed Hook assertion is used.
Automatic takeover integration remains deferred. This audit fixes no proven
keyboard defect and leaves the inconsistent observation issue open.

## Agent verification of this audit

Managed Debug/x64 diagnostic build: zero warnings/errors. Native /W4 /WX build
and Debug/x64 WinUI build: PASS (zero warnings/errors). Native
and counter/layout/thread lifecycle tests: PASS. Existing full script harness
(Gateway, grants, input outcomes/arbitration, Raw Input, Lua/scheduler/state): PASS.
Both loaded-module paths resolved to repository x64/Debug/Rug.Core.dll, 2,645,504
bytes. Managed file SHA-256 matched the native runner's pre-run fingerprint:
89E6E1E0FDFCFF0399856D3DEE71F1815DA2A482D0ADFFC55410A9E5EA3EB643.
This verifies the current local setup, not previous user-session binaries.
Managed PostMessage/F8/B: PASS. Native PostMessage/F8: PASS; B: INTERFERED / NOT
RUN (unflagged mouse activity). Both SendInput rounds: NOT RUN (foreground
activation unavailable). Human acceptance and new paired sessions with module
fingerprints: NOT RUN by agent. No failed/inconclusive software round is promoted
to acceptance.
