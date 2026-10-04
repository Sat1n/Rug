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
a Hook, or alters production emitters. Both both-raw modes, target-raw and keyboard-raw enable Raw Input.

| Stage | Managed Rug.Core load | Managed target/message thread | Keyboard Hook | Mouse Hook | Raw Input |
|---|---|---|---|---|---|
| load | Yes | No | No | No | No |
| target | Yes | Yes | No | No | No |
| keyboard | Yes | Yes | Yes | No | No |
| both | Yes | Yes | Yes | Yes | No |
| both-raw | Yes | Yes | Yes | Yes | Yes |
| both-raw-split | Yes | Yes, plus separate Hook thread | Yes | Yes | Yes |
| target-raw | Yes | Yes, plus idle message-only receiver thread | No | No | Yes |
| keyboard-raw | Yes | Yes, plus same independent receiver thread | Yes | No | Yes |
| keyboard-split | Yes | Yes, plus same independent receiver thread | Yes | No | No |
| keyboard-pump | Yes | Yes, plus Hook thread and unregistered receiver pump | Yes | No | No |
| keyboard-direct | Yes | Yes, plus Hook thread and registered direct receiver | Yes | No | Yes, no source queue |

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
not resolve the original failing managed-sender scenario. The subsequent user's
both-raw/native-target comparison passed as recorded below; scripted startup/
snapshots/cleanup alone remains insufficient for acceptance.

Agent verification: real both-raw scripted baseline/snapshot/done reached Ready,
reported both Hooks plus Raw Input, then Stopped with zero subscribers and zero
process keyboard/mouse registrations. Software/candidate/down/up classification,
merged sample totals, detached snapshots and 1,000 concurrent observations passed
aggregate tests. Existing Gateway/grant/arbitration/Raw Input/Lua/scheduler suite
and Debug/x64 WinUI build passed (zero warnings/errors). No native sender or human
actions were used in the scripted both-raw check; that comparison remains NOT RUN.

## Native sender with managed target

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
The user's subsequent managed-target run reproduced missing automatic keyboard
Hook entry in both observers. Native F8/B per-round callback/injected/tagged
counts were zero, while each managed target key down/up was 1/1. Managed Hook
software keyboard counts were zero, mouse tagged/injected totals ten. Raw Input
retained software keyboard/mouse 4/10 and key down/up 2/2; both PostMessage rounds
passed. Human callbacks resumed without reinstallation; source fault/overflow/
consumer errors and stop subscriptions/registrations were zero. Thus managed
InputService/Task.Run is not necessary to reproduce the symptom. Target/foreground/
thread context and cross-process metric queries remain investigation variables;
this does not prove a CLR, Raw Input API or permanent-Hook-removal defect.

## Separate managed target and Hook threads

Retain the latest failing native ManagedTarget combination and change only B's
diagnostic topology to **both-raw-split**. The visible target keeps the same class,
title, WndProc and scalar metric queries. Both managed Hooks now install in a
dedicated message-only window/thread using the same counter/parser/forwarding
callbacks. Raw Input still uses its own unchanged receiver and consumer, starting
after the two Hook installations. No sender, test key, input tag, ignore interval,
Hook reinstallation, input permission or takeover behavior changes. Message-only
windows use HWND_MESSAGE and are invisible, as documented by
[Microsoft](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#message-only-windows).
The added thread/window is the controlled topology change, not a proven repair.

Close all old diagnostic processes. Keep both terminals at the same elevation
and external applications in the same state as the previous failing run.

1. In repository root, start A FIRST with the unchanged command:

   ```powershell
   ./tests/run_native_input_probe.ps1 -Mode ManagedTarget
   ```

   Wait for its HWND/PID prompt (its native Hooks are already installed).
2. Start B:

   ```powershell
   dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage both-raw-split
   ```

   Wait for ISOLATION READY. THREAD TOPOLOGY must print different targetThreadId
   and hookThreadId. READY TARGET has no Hooks; READY has both Hooks installed,
   errors zero. These are deliberately different probe generations, stable for
   each thread across the run. Raw Input must be Ready/fault None. Paste B's
   MANAGED TARGET hwnd and processId into A as `0xHANDLE PID`.
3. Follow A HUMAN BEFORE using B's visible white window and another app. Press/
   release B and F8, move/click/scroll and verify ordinary shortcuts work.
   Enter baseline in B, then next in A. During the unchanged five-second
   countdown activate B's white window, release all keys/buttons and leave
   input untouched throughout AUTO. Do not manually press B/F8 during AUTO.
4. Once A finishes all four rounds, enter snapshot in B. Perform HUMAN AFTER
   on the same live windows/source, including keyboard/mouse and shortcuts.
   Enter done in A; finally enter snapshot then done in B. Keep B alive until
   A finishes, and return both COMPLETE outputs, including cleanup lines.

Expected complete software evidence: native per-round SendInput key down/up 1/1,
tagged injected keyboard at least two and mouse at least five, all four rounds
PASS. B's first snapshot should retain software keyboard/mouse 4/10 and software
key down/up 2/2; its Hook tagged/injected keyboard/mouse totals should include
4/10. Candidate deltas include terminal/focus changes and cannot alone judge
AUTO interference. Hooks/target/source faults, parse errors or interference make
the comparison inconclusive. Thread topology status/barriers cannot prove Hooks
remain effective. Both threads must exit, with zero source subscribers/registrations.

| Result against previous shared-thread failure | Next branch |
|---|---|
| Both observers regain all marked keyboard events | Correlation with shared target/Hook thread; repeat before production repair |
| Both still miss keyboard events, target and raw complete | Separation is insufficient; retain target/foreground/query/chain hypotheses |
| Only one observer receives marked keyboard events | Compare Hook order, thread and forwarding-chain evidence |
| Target/raw delivery, foreground, fault or interference is incomplete | No root-cause inference; preserve evidence and rerun controlled conditions |

Agent checks: full script/Gateway/grant/input-arbitration/cache regressions and
Debug/x64 WinUI build passed, zero warnings/errors. Three real split lifecycles
verified distinct threads, target message protocol/delivery, observable post-ready
Hook fault with live target/source, and cleanup with zero registrations/subscribers.
Scripted baseline/snapshot/done reached Ready and clean exit for both threads;
it emitted no input and supplies no software/human acceptance. The subsequent
user's split-thread comparison used target thread 3440 and Hook thread 62092;
native automatic F8/B keyboard callbacks remained zero, managed injected/tagged
keyboard counts zero, target keys 1/1 per round, marked Raw Input key/mouse 4/10
with down/up 2/2, mouse Hook tags ten. Both PostMessage rounds passed, human
callbacks continued, and cleanup/fault/overflow counts were clean. SendInput
remained INCONCLUSIVE. Thread separation is insufficient; managed Hook presence
is the next controlled condition.
Original shared-thread failure and automatic takeover integration remain open.

## Managed Hooks omitted: target-raw

Compare against the latest both-raw-split failure. target-raw preserves its target
window, scalar metric queries, native sender, independent message-only receiver
thread/window and Raw Input receiver/consumer. It only omits both managed Hook
registrations; keeping the idle receiver avoids also changing thread existence.
The real Raw Input receiver remains a different thread/window. No automatic
reinstallation, emitter change, suppression, listener pause or ignore time is used.

Use fresh A/B processes, same elevation/external applications as the failed run.

1. Start A FIRST: `./tests/run_native_input_probe.ps1 -Mode ManagedTarget`.
   Wait at the HWND/PID prompt; the native Hooks are already installed.
2. Start B with the new stage:

   ```powershell
   dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage target-raw
   ```

   Wait for ISOLATION READY. Both managed keyboardInstalled/mouseInstalled must
   be False, requested flags False; THREAD TOPOLOGY still shows distinct IDs.
   MANAGED HOOKS OMITTED explains intentional absence. Raw Input must be Ready,
   with fault None. Copy B's MANAGED TARGET HWND/PID into A as `0xHANDLE PID`.
3. Complete A HUMAN BEFORE on B's visible window and another app, including keys,
   mouse/wheel and normal shortcuts. Enter baseline in B, then next in A.
   During the unchanged five-second countdown activate B's white target window;
   release all keys/buttons and leave input untouched throughout AUTO.
4. After A's four rounds finish, enter snapshot in B. Complete HUMAN AFTER and
   done in A; then snapshot and done in B. Keep B alive through A's completion.
   Return both complete outputs including cleanup, plus confirmation of ordinary
   input/shortcuts and any accidental AUTO interference.

Only A supplies Hook evidence in this mode. Successful SendInput rounds still
require marked injected keyboard down/up and mouse counts, actual target delivery
and no interference. Expected B raw software key/mouse is 4/10, key down/up 2/2.
Managed Hook counts must be zero even while human/software input is observed;
this does not mean capture failed and never substitutes for A's Hook checks.
Broad B candidate intervals include terminal/focus activity, not solely AUTO.
Both message threads must exit and subscriptions/registrations return to zero.

If A regains marked keys, investigate managed Hook registration/forwarding-chain
interaction; if A still misses keys despite complete raw/target evidence, managed
Hooks are not necessary to reproduce the failure. Neither outcome proves a CLR,
Raw Input API or third-party defect. Target/foreground/query/receiver hypotheses
remain if no cause is localized. Fault/incomplete delivery/interference means no
causal inference. Agent protocol/lifecycle/full regression checks and Debug/x64
WinUI build pass. The subsequent user's target-raw run passed all four native
rounds: SendInput/F8 and B each had two marked injected keyboard callbacks,
marked down/up 1/1, target down/up 1/1 and five marked mouse events. Raw Input
retained software key/mouse 4/10, down/up 2/2; human input continued, source
fault/overflow/consumer errors and final subscriptions/registrations were zero.
This narrows a registration/chain correlation but omitting both Hooks cannot
identify which Hook or their combination is responsible. It does not accept the
original managed sender/probe scenario. Next add only the managed keyboard Hook.

## Keyboard-only registration: keyboard-raw

The read-only audit found no proven declaration/layout/lifetime/forwarding defect:
HOOKPROC and CallNextHookEx use int nCode, pointer-sized WPARAM/LPARAM/LRESULT;
keyboard flags/extra-info offsets 8/16 agree with
[KBDLLHOOKSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-kbdllhookstruct)
and existing layout tests. Only HC_ACTION data is read. Negative codes forward,
and callbacks return CallNextHookEx's unchanged result; its hhk argument is ignored
as specified by [Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-callnexthookex).
Delegates remain rooted in instance fields through thread teardown. Default
platform calling convention is documented by
[Marshal](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.getfunctionpointerfordelegate?view=net-10.0).
Immediate install-error capture, message loops and fault/cleanup checks remain
unchanged. This audit does not prove runtime delivery; no speculative callback,
import or input-emission repair is made in this comparison.

keyboard-raw preserves target-raw's target, idle receiver window/thread, Raw Input,
sender and metric query protocol. It adds only the managed keyboard Hook on the
independent receiver thread; no managed mouse Hook is installed. A's native Hook
verification remains intact, including both keyboard and mouse. Startup order,
five-second countdown, software tag and four input rounds remain unchanged.

1. Close both old processes; keep elevation and external applications unchanged.
   Start A FIRST: `./tests/run_native_input_probe.ps1 -Mode ManagedTarget`.
2. At A's HWND/PID prompt start B:

   ```powershell
   dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage keyboard-raw
   ```

   Wait for ISOLATION READY. THREAD TOPOLOGY IDs must differ; READY TARGET has
   neither Hook. READY reports keyboardInstalled=True, mouseInstalled=False and
   zero install errors. Raw Input must be Ready/fault None. Paste B MANAGED TARGET
   hwnd/processId into A as `0xHANDLE PID`; A's older both-raw prompt is generic.
3. Complete HUMAN BEFORE with B's white target and another application. In B type
   baseline; in A type next. Activate B's white window within five seconds, release
   keys/buttons and keep hands off throughout AUTO. Do not manually press F8/B.
4. After A finishes four rounds, enter snapshot in B. Complete HUMAN AFTER and
   done in A; then snapshot/done in B. Return both COMPLETE outputs and confirm
   ordinary input/shortcuts and accidental interference. Keep B alive until A exits.

Expected complete evidence: all four A rounds PASS, native marked keyboard down/up
1/1 per SendInput key and mouse five each; B raw key/mouse 4/10, down/up 2/2 and
managed tagged/injected keyboard four. B mouse Hook counts are intentionally zero,
not a failed software round; A must still observe mouse input. Candidate deltas
include terminal/focus activity. Installation/barriers cannot certify Hook health.
All message threads/registrations/subscriptions must clean up.

Missing automatic keyboard evidence with complete raw/target data would reproduce
the failure with only the managed keyboard Hook present, without proving its
precise cause. A pass requires investigating mouse-only registration or combined
Hooks next; those further controls are not implemented/accepted here. Faults,
missing target/raw data or interference remain inconclusive. Real keyboard-only
startup/metric/fault/cleanup and full existing regressions plus Debug/x64 WinUI
build pass. The user's keyboard-raw run reproduced missing SendInput keyboard
callbacks in both observers (native AUTO 0, managed injected/tagged 0). Both keys
reached the target down/up 1/1; raw software key/mouse 4/10, down/up 2/2 were complete.
Human callbacks continued, PostMessage passed, faults/overflow/subscriptions and
remaining registrations were zero. Managed mouse registration is not necessary
to reproduce this condition; precise causality remains unresolved.

## Receiver omitted: keyboard-split

Change only receiver presence from keyboard-raw: same managed visible target,
independent keyboard-only Hook receiver/thread, native sender, startup order,
metric protocol and five-second countdown. No Raw Input source, message thread,
registration or consumer is created. The omitted receiver includes these lifecycle
differences; a changed result cannot by itself blame the Raw Input API.

Follow the keyboard-raw steps above, starting A FIRST with ManagedTarget, then B:

```powershell
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage keyboard-split
```

Check distinct target/Hook thread IDs, keyboard installed/mouse absent, zero errors,
rawInputRegistered=False and READY RAW INPUT OMITTED registrations=0. Paste HWND/PID
into A. HUMAN BEFORE, B baseline, A next, focus B within five seconds/hands off,
four AUTO rounds, B snapshot, HUMAN AFTER, A done, B snapshot/done remain unchanged.
Return both full logs and ordinary-input/shortcut/interference confirmation.

Both Hook observers should show four tagged/injected keyboard events in total;
A checks each key down/up 1/1, target 1/1 and mouse five per SendInput round.
B mouse Hook zero is expected. Raw counters are NOT COLLECTED, never inferred
zero or software-source acceptance. All threads and registrations must clean up.
The user's keyboard-split run passed all four rounds, both observers retained four
tagged/injected keyboard events, target delivery was complete and cleanup clean.
The keyboard-raw gap therefore correlates with receiver presence; this is not an
API diagnosis. Next retain the receiver pump and omit registration using
[keyboard-pump](ReceiverRegistrationControl.md), whose thread/window/timer evidence
is distinct from registered event-source readiness. Agent lifecycle checks cover
three no-source runs plus nine source runs; original acceptance remains open.
The user's keyboard-pump run also passed both observers/all rounds while its
unregistered receiver processed 245 timer messages. Next enable registration and
direct copied-observation counts, retaining no event-source queue/consumer:
[keyboard-direct steps](ReceiverRegistrationControl.md#registered-direct-counter-keyboard-direct).

## Remaining acceptance work (2026-10-04)

| 05B-01 requirement | Current evidence / outstanding work |
|---|---|
| Parsing: key down/up/repeat, mouse buttons/wheel, invalid data | Automated checks passed |
| Lifecycle: failures, generations, registration/subscription cleanup | Automated real/fake receiver checks passed |
| Consumers: slow/faulting, high frequency, critical overflow | Automated checks passed |
| Own software input | PostMessage passed; original SendInput Hook gap unresolved despite complete raw/target evidence |
| Human foreground/background input | User counters show observed input; explicit normal-operation confirmation remains needed |
| Normal operation/shortcuts/window interaction | Human visual confirmation needed; counters cannot certify this |

The next planned diagnostic is one keyboard-direct paired session. After a supported
repair, rerun the ORIGINAL raw-input-diagnostic (managed InputService sender),
HUMAN BEFORE -> AUTO -> HUMAN AFTER with complete Hook/raw/target evidence,
and relevant automated regressions. A simplified control passing alone is not
original-scenario acceptance. Repeat a repaired scenario if needed to address
the observed order dependence. Additional single-condition controls depend on
results; there is no evidence-based fixed number yet. No need to rerun every
historical matrix or vary unrelated external applications at the same time.
Automatic takeover integration is future development, outside this unit.
