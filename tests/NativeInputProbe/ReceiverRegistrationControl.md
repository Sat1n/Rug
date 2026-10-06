---
id: rug_receiver_registration_control
type: logic_node
inputs: [rug_hook_controlled_stages, rug_raw_input_events]
outputs: []
tags: [diagnostics, hooks, registration]
---

# Unregistered receiver control: keyboard-pump

The user's keyboard-raw run lost automatic keyboard callbacks in both observers
despite complete target/raw delivery. keyboard-split, with no Raw Input receiver,
passed all four native rounds: both observers had four tagged injected keyboard
events; target key down/up was complete, human callbacks continued and cleanup
was clean. This correlates the gap with receiver presence, not an API root cause.

## Boundary and implementation

keyboard-pump retains keyboard-split's visible managed target, independent
keyboard-only Hook receiver, native Rug.Core sender, startup order, target metric
protocol and five-second countdown. It additionally starts the actual
WindowsRawInputReceiver window/thread/timer/message loop used by keyboard-raw,
without registering devices or running registration ownership checks/removal.

The Core internal RunUnregisteredDiagnostic method and public Run share RunCore.
Public Run always registers devices; no public flag weakens IInputEventSource
readiness or enables plugin-controlled registration. The diagnostic host starts
the unregistered pump only after target/Hook readiness, on a dedicated background
thread with the same receiver thread name. It has no event-source delivery
consumer or buffering. These differences from keyboard-raw remain bundled with
omitted registration; passing cannot distinguish registration from subsequent
message processing or delivery. If failure reappears without registration,
receiver window/thread/timer activity alone is sufficient in this tested condition.

Windows requires registration to receive raw data
([RegisterRawInputDevices](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices)).
PumpReady therefore certifies only window/timer creation and a running message
loop, never input reception or valid Hook delivery. Snapshots report generation,
native thread ID, HWND, RegistrationEnabled, readiness/exit, timer/input/device
message totals, unexpected copied observations and failure text. No human keys,
text or trajectories are recorded. Registration count must remain zero.

The read-only audit found the expected 0x2100 registration flags, explicit
ownership checks and copied packet lifetime. The existing handler calls
DefWindowProc for WM_INPUT before returning zero, including faults
([WM_INPUT cleanup](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-input)).
No proven fix follows from that audit. Imports, Hook forwarding, emitters,
production capture readiness and takeover behavior remain unchanged.

## Manual paired test

Close old processes. Keep elevation/external applications unchanged. A FIRST:

~~~powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode ManagedTarget
~~~

At A's HWND/PID prompt, start B SECOND:

~~~powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage keyboard-pump
~~~

Wait for ISOLATION READY. Check target/Hook/pump native IDs are all distinct,
keyboardInstalled=True, mouseInstalled=False, install errors zero,
rawInputRegistered=False and UNREGISTERED PUMP RegistrationEnabled=False,
PumpReady=True, Failure empty. READY registrations must be zero. Paste B's
MANAGED TARGET HWND/PID into A as 0xHANDLE PID.

Perform HUMAN BEFORE in B's white target and another application, including
normal shortcuts and mouse/wheel operation. B baseline, A next. Within five
seconds activate B's white target, release all keys/buttons and keep hands off
AUTO. Do not manually press B or F8. After A completes four rounds, B snapshot.
Complete HUMAN AFTER, A done; keep B alive until A exits, then B snapshot/done.
Send both full logs, normal-input/shortcut confirmation and accidental interference.

Expected: A's four rounds PASS; each SendInput key has two tagged injected
callbacks and down/up 1/1, target down/up 1/1 and five tagged mouse events.
B has four tagged injected keyboard callbacks in total; B mouse Hook zero is
intentional. The pump has timer activity, no input/device/observation events,
zero registrations, no fault and clean exit/zero HWND after stop. Raw samples
are NOT COLLECTED; do not infer zero candidates or software-source acceptance.

| Evidence | Next interpretation |
|---|---|
| Hooks and target complete, unregistered pump healthy | Focus on registration/registered message processing/delivery interaction |
| Hook gap returns, target complete, zero registrations | Receiver window/thread/timer can reproduce without device registration |
| Fault, incomplete target, unexpected raw messages or interference | Inconclusive; diagnose that evidence before causal inference |

Agent checks pass: three real pump lifecycles, distinct threads, target protocol,
timer handling, idempotent starts/stops/disposal, increasing generations, visible
post-start fault and another receiver's registrations preserved during cleanup.
Existing regressions and Debug/x64 WinUI build pass. Scripted pump startup/snapshot/
cleanup is not AUTO acceptance. The user's subsequent keyboard-pump run passed both
Hook observers (four tagged injected keyboard events), all target/PostMessage
rounds, human before/after callbacks and clean teardown. Its receiver processed
245 timer messages with zero registrations/input/device/unexpected observations.
This weakens receiver thread/window/timer presence alone in the tested condition.
Original managed-sender diagnostics must pass after a supported repair;
automatic takeover remains deferred.

## Registered direct counter: keyboard-direct

Change from keyboard-pump: the same diagnostic pump now calls the production
WindowsRawInputReceiver.Run, restoring both device registrations, ownership
checks, ordinary packet reading/tracking and WM_INPUT cleanup. Copied observations
are classified/countable on the receiver thread. No RawInputEventSource, queue,
consumer, subscriptions, GUI update or user callback is created. The pump class
is renamed DiagnosticRawInputPump for its two explicit host diagnostic modes;
old unregistered behavior remains available unchanged.

Use fresh A/B processes, identical elevation/external applications, A FIRST:

~~~powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode ManagedTarget
~~~

At A's HWND/PID prompt, B SECOND:

~~~powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage keyboard-direct
~~~

Check separate target/Hook/receiver native IDs, keyboard Hook installed, mouse
Hook absent, zero install errors. DIRECT PUMP must report RegistrationEnabled=True,
PumpReady=True, ThreadExited=False, Failure empty; DIRECT REGISTRATIONS count=2
and keyboardMouseOwned=True. rawInputRegistered=True reflects verified native
registrations, not an event-source state claim. Paste B MANAGED TARGET HWND/PID
into A. HUMAN BEFORE, B baseline, A next, five seconds to focus B then hands off,
four AUTO rounds, B snapshot, HUMAN AFTER, A done, B snapshot/done remain unchanged.
Send complete logs plus normal-input/shortcut and interference confirmation.

Preserve all three evidence streams: A native Hook rounds, B managed keyboard
Hook deltas and direct raw software/candidate snapshots/deltas, and actual target
down/up. Expected software key/mouse 4/10 and software down/up 2/2. Each SendInput
key should have native tagged/injected callbacks=2 and tagged down/up 1/1; B should
have four tagged/injected key callbacks in total. Candidate deltas across baseline/
snapshot include terminal and focus input, so they are not an AUTO interference
verdict. Raw copies carry no native packet handles. Snapshot fields are independently
sampled aggregates; no globally atomic cut across receiver and Hook threads is claimed.

Receiver callbacks only accumulate scalars, never log, wait on consumers, execute
input or stop tasks. Receiver barriers prove the loop, not Hook or raw completeness.
Unexpected faults/ownership changes mark the session inconclusive. Stop removes
owned registrations and destroys the window; final HWND/registrations must be
zero and all threads exited. No registration ignore window or Hook reinstall.

| Evidence | Direction |
|---|---|
| Hook gap remains with complete target/raw delivery | Registered receiver path can reproduce without source queue/consumer |
| Both Hooks and target/raw complete | Investigate publisher/buffer/consumer and source lifecycle differences |
| Fault, incomplete raw/target data or interference | Inconclusive; fix the evidence gap before inferring cause |

Automated checks pass: copied classification/down/up/sample counts, concurrency,
detached snapshots, three real registered lifecycles with conflicts/readiness,
post-start malformed packet faults, generations, repeated cleanup and protection
of new registrations from old disposal. Full existing regressions and Debug/x64
WinUI build pass. Interactive keyboard-direct AUTO/HUMAN is NOT RUN by the agent.
The original managed-sender diagnostic and real-human normal-operation acceptance
remain open; no automatic takeover, emitter or production event-source changes.

## Registered message-count control: keyboard-messages

The user's keyboard-direct run reproduced missing automatic keyboard callbacks
in both observers, with target down/up complete and direct raw software key/mouse
4/10, down/up 2/2. Human callbacks continued and all threads/registrations cleaned
up. Source publisher/queue/consumer are therefore not necessary in this reproduction;
this does not identify the precise registered-receiver cause.

keyboard-messages changes only packet processing from keyboard-direct: retain
the actual receiver thread/window/timer, both 0x2100 registrations, ownership
checks and owned-registration removal. Omit GetRawInputData, packet parsing,
key/device tracking and observation callbacks (including device notifications).
WM_INPUT/device/timer messages remain counted; DefWindowProc is still called for
every WM_INPUT and the handler returns zero, preserving
[foreground system cleanup](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-input).
The Core seam is internal; public Run always reads packets. No production receiver,
emitter, Hook forwarding, C-ABI or takeover policy is weakened.

Use fresh processes and unchanged elevation/external applications. A FIRST:

~~~powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode ManagedTarget
~~~

At A's HWND/PID prompt, B SECOND:

~~~powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage keyboard-messages
~~~

Check MESSAGES PUMP: RegistrationEnabled=True, PacketReadingEnabled=False,
PumpReady=True, ThreadExited=False, Failure empty and Evidence empty/null.
DIRECT REGISTRATIONS count=2, keyboardMouseOwned=True must still hold (the
shared label denotes the same ownership check). Target/Hook/receiver thread IDs
must differ; B keyboard Hook installed, mouse absent, errors zero. Paste B's
MANAGED TARGET HWND/PID into A as 0xHANDLE PID.

HUMAN BEFORE in B target and another app; B baseline, A next. Focus B target
within five seconds, release keys/buttons, hands off AUTO; do not manually press
F8/B. After four A rounds, B snapshot. HUMAN AFTER, A done; keep B alive until A
exits, then B snapshot/done. Return both complete logs, ordinary-input/shortcut
confirmation and any accidental AUTO interference.

Preserve A's per-round Hook/target evidence, B's Hook deltas, and MESSAGES PUMP
snapshots/MESSAGE DELTA. Input/device/cleanup totals are independently sampled;
at final stopped snapshot InputCleanupCalls must equal InputMessages. Every raw
software/candidate field is NOT COLLECTED, not zero; message totals cannot prove
keyboard classification, so this control alone cannot pass software-source acceptance.
Pump barriers prove only message-loop health. Final HWND/registrations zero,
threads exited and no failures remain required.

| Evidence | Direction |
|---|---|
| Both Hooks complete, target complete | Difference lies in omitted reading/parsing/tracking/callback path; split that path next |
| Keyboard gap remains with target delivery and healthy pump | Registration/ownership/basic message-processing condition can reproduce without reading/classification |
| Fault, incomplete target or interference | Inconclusive; repair evidence before causal inference |

Automated coverage includes three count-only lifecycles, conflict rejection,
generations, invalid foreground/sink handles without parsing, device counts,
DefWindowProc call counts, ownership-loss faults and replacement-owner protection.
Synthetic message tests are not software/HUMAN acceptance. Interactive paired
AUTO is NOT RUN by the agent; original managed-sender closure remains required.

## Mouse-only registration: keyboard-messages-mouse

The user's keyboard-messages run still lost automatic keyboard callbacks in both
Hooks. Each target round delivered down/up 1/1; mouse Hooks captured all ten
tagged injected events. HUMAN callbacks continued; receiver input/cleanup totals
were both 14,586, with no faults and zero final registrations. Packet reading,
parsing, tracking and callbacks are not necessary for this reproduction.

keyboard-messages-mouse changes only device selection from keyboard-messages:
register page 1 usage 2 (mouse), omit usage 6 (keyboard). Keep 0x2100 flags,
receiver window/thread/timer, count-only handling and DefWindowProc cleanup;
retain native sender, managed target, independent managed keyboard Hook and
native keyboard/mouse Hooks. Ownership checks now require the selected mouse
class and reject unexpected keyboard/page-wide registrations. Startup still
rejects conflicting keyboard/mouse entries; cleanup removes only selected owned
classes, preserving other owners. This internal diagnostic seam cannot weaken
public production Run, which registers and reads both classes.

Fresh processes, identical elevation/external applications. A FIRST:

~~~powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode ManagedTarget
~~~

At A's HWND/PID prompt, B SECOND:

~~~powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage keyboard-messages-mouse
~~~

Wait for ISOLATION READY. MOUSE MESSAGES PUMP must show RegistrationEnabled=True,
KeyboardRegistrationEnabled=False, MouseRegistrationEnabled=True,
PacketReadingEnabled=False, PumpReady=True, ThreadExited=False and no Failure.
MOUSE REGISTRATIONS must show count=1, mouseOwned=True, keyboardPresent=False.
Check three distinct thread IDs, managed keyboard Hook installed/mouse absent
and zero errors. Paste B's MANAGED TARGET HWND/PID into A as 0xHANDLE PID.
Snapshot selection fields describe configured mode; registration queries verify
actual ownership, and the final query must show zero entries after cleanup.

HUMAN BEFORE in B target/another app; B baseline, A next, five seconds to focus
B target then release everything/hands off. Do not manually press F8/B during
AUTO. After four rounds B snapshot; HUMAN AFTER, A done; wait for A exit, then B
snapshot/done. Return both complete logs and normal-input/interference confirmation.

Preserve both Hook observers, per-round target delivery, message/cleanup phase
deltas and registration selection. Raw keyboard/mouse software and candidate
counts are NOT COLLECTED; reduced registration is not production acceptance.
Final input/cleanup totals must match, HWND/registrations zero and threads exited.

| Evidence | Direction |
|---|---|
| Both keyboard Hooks complete, target complete | Difference correlates with keyboard-class registration in this setup; further confirm before repairing production |
| Keyboard gap remains, mouse-only ownership/pump healthy | Keyboard-class registration is not necessary; investigate retained mouse registration/ownership/message path |
| Fault, unexpected registrations, missing target or interference | Inconclusive; resolve that evidence first |

Three automated mouse-only lifecycles cover selected flags/identity, idempotence,
generations, malformed count-only messages, conflicts, unexpected keyboard faults,
replacement-owner preservation and cleanup. Original both-class tests remain.
Interactive paired AUTO/HUMAN is NOT RUN by the agent. No sender, C-ABI, Hook
forwarding, source filtering or takeover behavior changes.

## Keyboard-only registration: keyboard-messages-keyboard

The user's mouse-only run passed both Hook observers: tagged injected keys 4,
F8/B down/up 1/1 each, mouse 10 and complete target delivery. Actual mouse-only
ownership and 11,132 matching input/cleanup calls were verified. Restoring both
registrations restored missing software keyboard callbacks in both observers,
while target keys remained 1/1 per round and mouse Hooks captured 10. HUMAN
callbacks continued; 15,894 input/cleanup calls, no faults and zero registrations
confirmed teardown. Neither count-only run collected raw attribution evidence.

keyboard-messages-keyboard changes only selection from the failing
keyboard-messages: keep page 1 usage 6, omit usage 2. Keep 0x2100 flags,
native Rug.Core sender, managed target, both native Hooks, independent managed
keyboard Hook, receiver window/thread/timer and count-only DefWindowProc cleanup.
This does not change production registration, packet handling or input emission.

Fresh processes, unchanged elevation/external applications. A FIRST:

~~~powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode ManagedTarget
~~~

At A's HWND/PID prompt, B SECOND:

~~~powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage keyboard-messages-keyboard
~~~

Wait for ISOLATION READY. KEYBOARD MESSAGES PUMP must show RegistrationEnabled=True,
KeyboardRegistrationEnabled=True, MouseRegistrationEnabled=False,
PacketReadingEnabled=False, PumpReady=True, ThreadExited=False and no Failure.
KEYBOARD REGISTRATIONS must show count=1, keyboardOwned=True, mousePresent=False.
Check distinct target/Hook/Raw thread IDs and managed keyboard installed/mouse
absent. Paste B's MANAGED TARGET HWND/PID into A as 0xHANDLE PID.

Perform HUMAN BEFORE in B target/another app, then B baseline and A next. During
the five-second countdown focus B's isolated target, release everything and keep
hands off throughout AUTO; do not manually press F8/B. After four rounds B
snapshot; perform HUMAN AFTER. A done, keep B alive until A exits, then B
snapshot/done. Return both complete logs plus normal-operation/interference
confirmation. Preserve both Hook observers and per-round target counts.

Raw software/candidate evidence is NOT COLLECTED. Message/cleanup totals must
match; final HWND/registrations zero, threads exited, no fault. Configured flags
remain descriptive after stop; query actual registrations to prove cleanup.

| Evidence | Direction |
|---|---|
| Keyboard gap, healthy keyboard-only ownership and complete target | Keyboard registration alone suffices in this controlled setup; investigate its retained registration/message/ownership behavior |
| Both Hook paths complete | Both-class combination or associated timing remains relevant; do not attribute directly to the API |
| Fault, unexpected ownership, target loss or interference | Inconclusive; resolve that condition first |

Three additional keyboard-only lifecycles check flags, conflicts, unexpected
mouse ownership, malformed count-only messages, replacement protection and
cleanup. Both/mouse-only tests remain. Paired AUTO/HUMAN is NOT RUN by the agent;
repair and ORIGINAL diagnostics must pass before automatic takeover integration.

The user's keyboard-only round reproduced missing software keyboard callbacks
in both Hooks, with targets 1/1 per F8/B round, tagged mouse 10, continuing human
callbacks, 293 matching input/cleanup calls and clean teardown. Next compare the
[same-process native receiver](NativeReceiverControl.md), retaining keyboard-only
registration and all sender/target/Hook conditions. Raw attribution remains absent.
