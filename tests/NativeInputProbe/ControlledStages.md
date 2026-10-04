---
id: rug_hook_controlled_stages
type: logic_node
inputs: [rug_hook_comparison, rug_script_tests]
outputs: []
tags: [diagnostics, hooks, isolation]
---

# Controlled managed-component stages

This is diagnosis, not Raw Input or takeover acceptance. The native Human probe
remains the fixed sender/target/observer; the second managed process adds exactly
one selected cumulative stage. It never sends input, automatically reinstalls
a Hook, or alters production emitters. Only both-raw enables Raw Input.

| Stage | Managed Rug.Core load | Managed target/message thread | Keyboard Hook | Mouse Hook | Raw Input |
|---|---|---|---|---|---|
| load | Yes | No | No | No | No |
| target | Yes | Yes | No | No | No |
| keyboard | Yes | Yes | Yes | No | No |
| both | Yes | Yes | Yes | Yes | No |
| both-raw | Yes | Yes | Yes | Yes | Yes |

Use **fresh A and B processes for every stage**, in this order:

1. Terminal A: `./tests/run_native_input_probe.ps1 -Mode Human`. Leave it waiting
   at HUMAN BEFORE; its native hooks are already installed.
2. Terminal B (initial stage load):

   ```powershell
   dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage load
   ```

   Wait for ISOLATION READY and CORE LOADED. Replace load with target, keyboard,
   then both in subsequent fresh sessions. Do not switch stages in a live process.
   The next comparison against the user's passing both session is **both-raw**;
   change only the B stage argument, keeping native sender and target unchanged.
3. Perform A's HUMAN BEFORE using the **native** white window and another app.
   In B enter baseline; in A enter next. During the five-second countdown activate
   the **native** window, then release keys/mouse and keep hands off AUTO.
   The managed white window in target/keyboard/both is not the input target.
4. After A finishes all four software rounds, enter snapshot in B. Complete A's
   HUMAN AFTER and done; finally enter snapshot and done in B. Keep both outputs.
5. Keep elevation and external applications unchanged across stages. Preserve
   module fingerprints, native tagged F8/B down/up, target delivery, per-round
   interference verdict, and both thread cleanup statuses. Verify normal input
   and shortcuts yourself; counters alone do not certify noninterference.

Managed READY means the selected components have started, not that Hook delivery
has passed. Absent Hook counters in load/target are expected. Keyboard stage has
no managed mouse Hook, but native mouse evidence remains available. Installation
failures or post-start thread faults fail visibly. EOF without done is incomplete.
Snapshots print aggregate counts only; baseline/terminal input is not an AUTO
interference verdict. No key sequence or mouse trajectory is persisted.

both-raw starts the existing RawInputEventSource after both Hook installations,
with its existing INPUTSINK/DEVNOTIFY registration, dedicated receiver thread,
bounded consumer and software-tag classification unchanged. READY is emitted
only after receiver readiness. Each snapshot drains delivered keyboard/mouse
sample totals against source totals, then prints source state/fault/device changes/
overflow/consumer errors and detached raw SNAPSHOT/DELTA: candidate key/mouse,
software key/mouse and software key down/up. Hooks retain their separate deltas;
native output retains actual target delivery. Neither receiver nor callbacks
emit input or persist key contents. Source failure, overflow and consumer errors
make the diagnostic inconclusive; stopping reports subscriptions and remaining
process keyboard/mouse registrations, and disposes the receiver and Hook window.

Raw candidate deltas between B baseline and snapshot also include human terminal
commands/focus changes. Do not label them self-input failures or hide them using
an ignore interval. Tagged software counts isolate cooperating Rug emissions;
native per-round evidence provides its own narrower interference verdict.
Any difference after both-raw is a correlation with receiver startup/registration/
message/consumer/thread behavior, not proof that Raw Input API is defective.
If complete, compare managed target separately only after reviewing this result.

If load already loses native keyboard evidence, focus on process/DLL/environment
differences; if target is the first change, examine target/thread interactions;
if keyboard is the first change, examine Hook registration/chain interaction;
if only both changes it, examine interaction between registrations. These are
investigation branches, not causal proof. A both-stage pass does not reproduce
the original failing **managed sender** case: next control would keep target and
observers fixed and compare caller thread/emitter context, only when authorized.
Do not label absent native Hook evidence as a Raw Input parser failure.

Automated tests validate stage mapping, invalid stage rejection before native
loading, real selective Hook startup, message barriers and cleanup. Native Human
comparison in these stages remains NOT RUN by the agent; user operation required.

User load/target/keyboard/both sessions all passed native F8/B and PostMessage
rounds. In keyboard/both, managed Hook evidence included four tagged injected
keyboard events; both also counted ten tagged injected mouse events. This does
not resolve the original failing managed-sender scenario. both-raw human/native
comparison is pending; its scripted startup/snapshots/cleanup is not acceptance.

Agent verification: real both-raw scripted baseline/snapshot/done reached Ready,
reported both Hooks plus Raw Input, then Stopped with zero subscribers and zero
process keyboard/mouse registrations. Software/candidate/down/up classification,
merged sample totals, detached snapshots and 1,000 concurrent observations passed
aggregate tests. Existing Gateway/grant/arbitration/Raw Input/Lua/scheduler suite
and Debug/x64 WinUI build passed (zero warnings/errors). No native sender or human
actions were used in the scripted both-raw check; that comparison remains NOT RUN.

## Native sender with managed target (only target changes)

The user's both-raw/native-target session passed all four native rounds, both
Hooks counted four tagged keys/ten tagged mouse events, and Raw Input counted
software key/mouse 4/10 with down/up 2/2. Subsequent human input remained visible;
source overflow/fault/consumer errors and stop registration/subscription counts
were zero. This is evidence for that combination, not the original managed sender.

Next comparison uses **the same B both-raw stage**. Only A's target endpoint changes;
the native sender still calls the same Rug.Core DLL/C-ABI, with the same four rounds.
A still creates its original spare target thread/window (hidden in this mode),
so that thread's existence is not removed as an additional variable. B's existing
target becomes the endpoint. A queries detached aggregate target counters with
bounded SendMessageTimeout; no pointer or input content crosses this protocol.

1. Close both old processes. Start A FIRST:

   ```powershell
   ./tests/run_native_input_probe.ps1 -Mode ManagedTarget
   ```

   Wait at the HWND/PID prompt: A's native Hooks are already installed.
2. Start B using the unchanged both-raw command. Copy the **MANAGED TARGET** line's
   hwnd and processId into A as `0xHANDLE PID` (only those two values).
   A verifies PID/thread, exact captured RugRawDiagnostic class, expected title and
   counter-protocol magic; arbitrary windows are rejected. Keep both processes at
   the same elevation; failed/blocked metric queries do not bypass UIPI.
3. Complete A HUMAN BEFORE with the **B managed white window**, and another app.
   Enter baseline in B, then next in A. During the five-second countdown activate
   **B's window**, release keys/mouse, and keep hands off until AUTO finishes.
   A never forces foreground. A logs per-round Hook deltas and actual managed
   target move/mouse down/up/key down/up counters; B also prints target deltas.
4. In B enter snapshot. Complete A HUMAN AFTER and done; then enter snapshot/done
   in B. B must remain alive through A AUTO/HUMAN AFTER; A does not destroy B.
5. Send both complete outputs and confirm ordinary input/shortcuts and accidental
   interference. Terminal/focus activity still contaminates the broad candidate
   interval; do not equate it to AUTO self-input errors.

Identity is checked before controller creation/each round and during queries.
Destroyed/replaced targets, wrong PID/class/title/protocol, blocked queries or
foreground refusal are not acceptance. This opt-in window protocol is a diagnostic
restriction, not a production authorization system or a general arbitrary-HWND
input adapter. Necessary key/button releases retain the existing CoreInput path.
The target experiment does not prove Raw Input or caller-thread root cause; if it
passes, managed sender/caller context remains to be isolated in a later unit.

Agent checks: native /W4 /WX build, parser rejection and thread lifecycle tests,
managed real-window metric protocol tests, existing full script regressions and
Debug/x64 WinUI build passed (zero warnings/errors). A scripted cross-process
run bound the actual managed target and read key delivery 2/2, movement 4 and
mouse down/up 2/4 after the two native ABI PostMessage rounds; cleanup had zero
subscriptions/registrations. The rounds were INTERFERED / NOT RUN because of
untagged mouse activity; SendInput was NOT RUN for lack of foreground and HUMAN
was NOT RUN. This verifies transport/delivery only, not software/human acceptance.
The managed-target four-round manual comparison remains pending.
