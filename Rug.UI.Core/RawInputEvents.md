---
id: rug_raw_input_events
type: logic_node
inputs: [rug_ui_core]
outputs: [rug_ui_core]
tags: [input, observation, diagnostics]
---

# Raw Input event source (Task 3.5-05B-01)

The opt-in diagnostic window supports selective keyboard/mouse Hook installation
for [controlled component stages](../tests/NativeInputProbe/ControlledStages.md).
Defaults remain both Hooks; this is separate from the event source and takeover.
The both-raw stage adds the existing receiver after both Hooks and reports raw
software/candidate sample deltas alongside Hook evidence without sending input.
Its diagnostic window also exposes bounded scalar message counters for the native
ManagedTarget comparison; no key contents or native-memory pointers are returned.

[IInputEventSource](Abstractions/IInputEventSource.cs#interface:IInputEventSource) is replaceable by a trusted host/test implementation.
[RawInputEventSource](Services/RawInputEventSource.cs#class:RawInputEventSource) is explicitly created/started by the diagnostic host. It is
not started by the normal WinUI application, registered as a plugin command, or
connected to TakeOverAsync. No scheduler, grant, control-return or C-ABI behavior
changes. The follow-up tags software emissions without changing the C-ABI. Production OS interop remains in Native; UI only consumes services.

## Contract and ownership

StartAsync completes after the dedicated message-only window exists, keyboard
(usage page 1 / usage 6) and mouse (page 1 / usage 2) registration succeeds, and
ownership verification succeeds. States are Uninitialized, Starting, Ready,
Faulted and Stopped. Creating a thread alone is not readiness. A startup token
cancels startup and awaits cleanup; after successful start it is not a session
lifetime token. Concurrent lifecycle operations serialize; repeated Start while
Ready creates no new receiver. Stop also cancels an in-progress startup.

Each new startup attempts a new monotonic generation, with sequence reset to one.
InputSourceEvent carries generation, sequence, Stopwatch.GetTimestamp() ticks
(frequency is Stopwatch.Frequency), immutable InputObservation and sample count.
Keyboard records retain VK, scan code, break/E0/E1 flags, message, repeat and
extra information. Mouse records retain relative/absolute/virtual-desktop flags,
all button transitions, signed wheel data, X/Y and extra information. Relative
X/Y are device deltas; absolute X/Y are Win32 normalized 0–65,535 coordinates,
not client or physical screen pixels. A single
mouse packet may contain multiple transitions. Device arrival/removal records
retain an opaque scalar DeviceId. Removed devices clear repeat tracking.

DeviceId is a host-issued session-local number, not the native hDevice value
or an owned handle. The receiver maps device handles internally with a 4,096-ID
capacity, drops mappings on device removal, and clears them on session exit.
Zero means an unattributed device and is deliberately retained. No HRAWINPUT, unmanaged buffer, callback pointer or mutable byte
array reaches subscribers. Device/key tracking is capped at 4,096 held identities;
keyboard overrun or malformed packets faults instead of silently claiming
continuity. Unmapped VK 255 is ignored as documented by Windows.

Snapshot is an independent immutable record with state, generation, revision,
fault reason, per-session raw keyboard/mouse/device/coalescing/overflow counts,
consumer error count, queued count and subscriber count. Counts measure received
packets before motion coalescing, including Rug software, not authenticated human actions.
InputObservation.IsRugSoftwareInput recognizes exact ExtraInformation 0x52554749
(the native emitter tag). These events remain delivered with sequence/counters
intact but IsCandidateInput is false. Unknown tags and untagged zero-device input
remain candidates. The tag is a spoofable cooperation hint, not authenticated
origin or authorization. No activity-window or DeviceId-only filtering is used. No input by
itself is not a fault. StateChanged notifications may coalesce; reread Snapshot
and its revision rather than applying a captured old state.

Stop rejects old generation callbacks, cancels native capture, destroys the
window/unregisters its class, waits for the native thread and consumers, discards
the queue and clears input/state subscriptions. Subscribe again after Stop before
restart. Already-entered callbacks finish before Stop completes; callbacks must
not synchronously wait on lifecycle methods. A blocked consumer can delay Stop's
completion but cannot block the message thread. Async Stop does not block a UI
caller. Dispose is idempotent; later Start/subscription addition throws. An OS
cleanup failure stays Faulted/CleanupFailure, not a successful Stopped state.
Other historical fault details remain available after an explicit clean stop.

## Registration, messages and concurrency boundary

Registration uses only RIDEV_INPUTSINK | RIDEV_DEVNOTIFY (0x2100), with a valid
receiver HWND. NOLEGACY, mouse capture and hotkey suppression are not enabled.
[Microsoft registration rules](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices)
allow one receiver per class per process. A process-local Rug registration lock
serializes query/register/remove across Rug sources. An existing keyboard/mouse
or overlapping page registration rejects startup rather than replacing it.
Ownership is checked after registration, before processing input/device messages
and by a 250 ms timer. Timer checks ownership, not input inactivity. On this
Windows host GetRegisteredRawInputDevices returns INPUTSINK without DEVNOTIFY;
verification tolerates only that omitted notification bit, retaining target and
other effective-flag checks. The registration call still supplies both flags.

Removal queries current registrations and removes only entries still targeting
our HWND, with REMOVE and null target. A replacement owner's entries survive
Stop. Windows has no conditional registration/remove API: third-party direct
RegisterRawInputDevices calls cannot participate in Rug's lock. There remains a
query/update race with such uncoordinated calls; the host must be the unified
registration authority for all cooperating components. Unexpected ownership
changes fault capture and are never silently re-registered/overwritten.

WM_INPUT reads its size, bounds it to 4 KiB, copies the full packet while still
inside WndProc, parses scalars and frees temporary memory. DefWindowProc cleanup
runs even if parsing fails; [WM_INPUT](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-input)
requires it for foreground packets. Device notifications use copied scalars.
Callbacks do not wait for subscribers, execute input or stop automation tasks.

A separate consumer worker invokes subscribers, isolating exceptions and
counting them. Queue capacity defaults to 1,024 (host selectable 2–65,536).
Only adjacent pure movement from the same device/foreground/flags/extra-info
may merge: relative deltas sum with overflow checks; absolute movement keeps the
latest point. The merged item has latest sequence/time and total samples.
Transitions, wheels, device changes, attributes changes and intervening devices
form barriers. Sequence gaps caused by merging are intentional. A full queue
that cannot merge does not silently drop transitions: it records Overflow,
faults capture, stops the receiver, and discards discontinuous pending data.
Overflow/ownership/native/malformed-data/cleanup faults require explicit host
inspection/restart. No fault invokes takeover or changes input ownership.

## Diagnostic commands and human acceptance

From repository root, build first:
```powershell
dotnet build tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -warnaserror
dotnet run --no-build --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --raw-input-tests
dotnet run --no-build --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --raw-input-self-test
dotnet run --no-build --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --raw-input-diagnostic
```

Software diagnostics require Windows interactive desktop and x64/Debug/Rug.Core.dll
plus its dependencies. They create/clean their own isolated window, bind a real
InputService and send two distinct moves, click, key down/up and necessary button release
(two points prevent an unchanged cursor from suppressing legacy move evidence).
AUTO now runs SendInput/F8, SendInput/B, PostMessage/F8 and PostMessage/B
sequentially in the same live probe/source session. Each round takes fresh hook,
raw and target baselines and releases its key/button; the user does not manually
press F8/B during AUTO. No production emitter or key delivery is changed.
Actual target messages must arrive; a no-op native input path cannot pass.
Keyboard emission now supplies MapVirtualKey scan metadata alongside the existing
virtual-key delivery. Hook flags and pointer-sized extra-info offsets have explicit
layout regression tests. A SendInput PASS requires target delivery, zero candidates,
at least seven tagged raw samples and tagged injected hook evidence for two keys
and five mouse packets. Consumer counters drain after native barriers before
evaluation; incomplete evidence never prints PASS.
Independent low-level hook probes exist only in the opt-in Native diagnostic
window; they forward every event and count injected/unflagged activity, never
feed the Raw Input source or filter its events. If unflagged user-like activity
occurs in a software round, output INTERFERED/NOT RUN and rerun hands off.
An incomplete probe sequence is INCONCLUSIVE. Raw candidates during the round
are printed as failure evidence, never hidden by a pause or fixed ignore time.
Exit 0 means successful software checks; exit 2 means missing prerequisites,
interference, incomplete verification or non-passing software diagnostics;
unexpected exceptions exit 1. Counts/logs contain no input text, key sequences,
device names or full trajectories and are not automatically persisted.

Human acceptance (NOT RUN by the development agent):

1. Run --raw-input-diagnostic from a visible terminal. Confirm Ready, zero faults
   and overflow, and retain this same source generation throughout the test.
2. Focus the diagnostic Rug window: press/release/hold a key, move, click and
   scroll. Keyboard down/up/repeat and mouse counters must rise. Drag/resize the
   window and verify ordinary input and shortcuts remain usable.
3. Alt+Tab to another application and repeat keyboard/mouse actions. Background
   counters must rise; normal target interaction and shortcuts must work.
   Optionally unplug/replug a device and confirm device-change counts.
4. Return to the terminal and type next. During the 5-second countdown, click
   the isolated window title bar to activate it, then release all input before AUTO.
   Both real software modes should report no candidate input; any failure or
   incomplete probe evidence requires investigation, not acceptance.
5. Repeat steps 2–3 during HUMAN AFTER, then type done. Keyboard and mouse counts
   must rise again, the same generation must still be Ready, and no overflow
   may have occurred. Only actual human operation validates this final step.
6. Confirm STOPPED and cleaned subscriptions/registrations after exit. If focus
   is denied by Windows, run on a local interactive desktop; do not classify the
   denied attempt as software-filtering success.

## Independent probe evidence (diagnostic only)

The keyboard and mouse hook counters independently expose callbackTotal,
actionableTotal (HC_ACTION only), injected, unflagged, taggedTotal,
taggedInjected, taggedUnflagged and parseErrors. Callback entry is counted before
classification; negative nCode forwards without reading its structure. Tag and
injection flag tests are independent. Tagged-unflagged observations are not called
human interference, and still cannot satisfy the existing tagged-injected PASS.
No hook callback prints, waits for a consumer or stores a key/trajectory sequence.
Own and CallNextHookEx durations have separate total/max Stopwatch ticks. The
maxima in delta output are lifetime maxima, not interval maxima; interval totals
are subtracted. Timing cannot establish why a hook disappeared.

Each probe instance has a new generation, installation OS thread ID and process
architecture. Keyboard and mouse installations are separately checked with
SetLastError=true and the error captured immediately on failure, before the next
installation. Installed flags record initial successful installation, not ongoing
hook health. Failure snapshots and Completion retain exceptions after Ready has
already completed. Clean exit records WM_QUIT; controlled/post-start failure
records Fault. Hook removal failures retain their immediate Win32 code and
CleanupFault, and prevent a successful diagnostic exit. Synchronize checks thread failures before/after the barrier, but a
successful message barrier never proves that either hook remains effective.

STARTED, HUMAN BEFORE END, AUTO BEFORE, AUTO AFTER and HUMAN AFTER END print
snapshots and deltas from the preceding phase. Each software-mode round also
prints hook deltas and Raw Input CandidateKeyboard/Mouse, SoftwareKeyboard/Mouse
and SoftwareKeyDown/Up. SendInput requires at least two software keyboard samples,
one down and one up, plus five software mouse samples; the combined seven alone
is insufficient. Snapshots are detached atomic counter reads, not a transaction
across the independent message/consumer threads. Software evaluation drains
received samples; human phase boundaries can include transition/terminal input.

| New evidence | Next investigation |
|---|---|
| Human and software keyboard callbackTotal both zero | Installation, thread, probe lifetime or hook chain |
| BEFORE callbacks existed, later raw keys but no new hook callbacks | Interval blocking/fault/exit; timeout-removal remains a hypothesis |
| taggedUnflagged greater than zero | Flags/classification/structure; callbacks did enter |
| Human hooks work but only automatic F8 missing | Compare F8 against B, only in isolated window |
| Tagged Raw Input down/up complete but hook absent | Separate source functionality from independent probe failure |

The terminal F8/B COMPARISON uses actionable callback deltas only when both
SendInput rounds have target delivery, complete marked raw key down/up, no
parse/thread fault and no detected unmarked human-like interference. Missing
rounds remain INCONCLUSIVE; F8=0/B>0 selects key-specific investigation, both
zero selects native comparison, and both nonzero still require each round to
pass all existing tagged-injected checks. It is a diagnostic branch, not a PASS.

The independent [x64 native probe](../tests/NativeInputProbe/README.md) now provides
native C++ callbacks on a dedicated message thread plus a separate isolated target,
using the unchanged real Rug.Core path and same F8/B/tag. Run it alone first,
then use its passive Observe mode to compare the same managed sender in two
processes. It does not register Raw Input, so ownership is unaffected. Native
counter/lifecycle tests and PostMessage rounds pass; native SendInput and human /
two-process comparison were NOT RUN by the agent, with subsequent user evidence
recorded below and in the controlled comparisons. No automatic hook
reinstallation, changed emitter, suppressed input or relaxed Hook PASS is used.
G HUB/PowerToys comparisons are performed by the user, one condition at a time,
with a fresh diagnostic process. Microsoft describes possible silent timeout
removal in [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc);
this is not established as the cause on this host.

## Current evidence and support boundary

The opt-in both-raw-split controlled stage moves the managed diagnostic Hooks to
their own message-only window/thread; the visible managed target, metric protocol,
Raw Input source and native sender remain unchanged. This receiver is not the Raw
Input receiver. Both selected Hooks must become ready before source startup.
Target and Hook generations/thread IDs, failures and exits are printed separately;
snapshots combine target message counts with independent Hook counters. The old
both-raw mode remains available. Neither mode changes normal application startup.
See [exact two-terminal steps and evidence](../tests/NativeInputProbe/ControlledStages.md).
Actual distinct threads, target metric delivery, post-ready Hook fault isolation
and repeated source/registration/subscription cleanup pass automated checks;
interactive split-thread SendInput was not performed by the agent; the user's
subsequent result is recorded below.
This is a diagnostic topology comparison, not a confirmed keyboard-probe repair.
The user's split comparison still missed automatic keyboard Hook entry in both
observers despite complete target and marked raw key delivery. The target-raw
control therefore keeps the same target, idle message-only receiver thread and
Raw Input, but installs no managed Hooks. Native observer verification remains
required; zero managed counters are intentional, never software acceptance.
Actual startup/protocol/fault/cleanup tests pass; the user's subsequent target-raw
session passed all four native rounds with complete marked Hook/raw/target evidence.
Original SendInput acceptance remains blocked, while
normal foreground/background/shortcut behavior needs explicit human confirmation.
The keyboard-raw comparison keeps those windows/threads/source/sender,
installing only the managed keyboard Hook in the independent receiver thread.
The mouse Hook stays absent; A retains native keyboard/mouse checks, B retains
keyboard and raw/target evidence. Read-only audit found no proven ABI/layout/
forwarding/lifetime defect, so callbacks and imports remain unchanged. Keyboard-only
lifecycle tests pass. The user's paired run reproduced missing automatic keyboard
entry in both observers with only the managed keyboard Hook: native AUTO callbacks
0, managed injected/tagged 0; target F8/B down/up 1/1 per round and raw software
key/mouse 4/10, down/up 2/2 remained complete. Human callbacks continued and cleanup
was clean. This does not locate a Hook-chain or receiver defect. The next
keyboard-split control retains the target and independent keyboard Hook receiver,
but creates no Raw Input source/registration/consumer. Omitted raw collection is
not zero-candidate evidence. Real lifecycle/protocol/fault/cleanup checks cover
both configurations. The user's no-source run passed both keys in both Hook
observers (four marked injected keyboard events), target delivery and PostMessage,
with clean thread/registration teardown. This correlates the gap with receiver
presence, not a proven API defect. The next
[registration control](../tests/NativeInputProbe/ReceiverRegistrationControl.md)
retains WindowsRawInputReceiver's actual window/thread/timer/message loop while
skipping registration and ownership checks. Its internal diagnostic method never
changes public Run, which always registers devices, or IInputEventSource Ready.
Only pump readiness/identity/message aggregates are reported; no raw attribution
is claimed. Lifecycle tests include faults, generations and foreign registration
preservation. No input emitter, C-ABI, Hook forwarding or takeover changes.

Automated parsing/lifecycle/buffering and real native registration tests pass.
The user's pre-repair same-session reproduction observed SendInput candidates=7
(keyboard 2, mouse 5), zeroDevice=7, extra-info=0, unflagged probe=0 and injected
keyboard/mouse=0/5, despite actual target delivery. PostMessage passed with zero
candidates. This failure evidence is not dismissed as human interference.
HUMAN BEFORE/AFTER counters covered keys, repeat, buttons, wheel and foreground/
background reception; ordinary shortcuts still require user confirmation.

The repair tags all native software move/button/key/text emissions, including
releases, without dropping raw observations. Parser tests cover x86/x64 tag
propagation, unknown tags and preservation of zero-device untagged candidates.
The user's normal/admin runs after tagging both showed candidates=0,
rugSoftware=7, target key down/up=1/1, injected keyboard/mouse=0/5 and
PostMessage PASS. This establishes aggregate source behavior but not separate
keyboard-tag evidence or callback entry; the earlier tagged count was inside
the injected branch. The new counters correct that diagnostic limitation only.
A subsequent user phase run showed HUMAN BEFORE keyboard actionable=246,
AUTO=0, HUMAN AFTER=9 (no reinstallation). SendInput had SoftwareKeyboard=2,
SoftwareMouse=5, SoftwareKeyDown/Up=1/1 and zero candidates. Thus raw keyboard
tag evidence is complete and the hook still receives later human input; neither
classification loss nor permanent hook removal is established. The current AUTO
version compares F8/B per round, without changing production key delivery.
The later user F8/B comparison showed both automatic keys had complete marked
raw down/up but zero keyboard Hook entry. Human callback deltas before/after
were 43/23, parse/thread faults zero, and both PostMessage rounds passed. This
rules out a diagnosis based solely on F8-specific behavior or permanent Hook
loss. It selects independent native comparison rather than speculative changes.
Agent F8/B validation passed both real PostMessage rounds; both SendInput rounds
were NOT RUN due to foreground activation policy, and the comparison correctly
remained INCONCLUSIVE. Subsequent user native-alone and reversed-order paired
sessions passed all four software rounds; the opposite paired order still missed
both injected keyboard keys in both Hook observers. See the
[source/module audit and comparison matrix](../tests/NativeInputProbe/HookComparison.md).
This is an unresolved order correlation, not a proven emitter or CLR defect.

Agent verification with the new phase instrumentation saw both hooks install
successfully (X64, errors=0), no parse/thread failures and clean WM_QUIT cleanup.
SendInput was NOT RUN because foreground activation was blocked; the software
PostMessage round was INTERFERED/NOT RUN due to unflagged mouse activity. No
software acceptance or keyboard root cause is inferred from that run. New manual
phase evidence was supplied as documented in the comparison matrix; it does not
yet identify a keyboard-probe repair.

RAWINPUTHEADER has no injected-origin flag.
[Microsoft documents zero device handles for precision touchpads](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputheader),
so zero device alone cannot exclude candidates. If the input path loses the tag,
diagnostics fail or remain inconclusive instead of silencing input. Other
injectors can forge the tag: this is attribution of cooperating Rug software,
not proof of human origin.

Raw observations are candidates only, never authenticated human identities.
Virtual HID, future hardware backends and all remote-session origins are outside
this unit's promised classification. Foreground/background human reception and
normal-input noninterference are pending human acceptance. Do not integrate
automatic takeover until the SendInput observation issue and human acceptance
are resolved. No C-ABI, grant policy or takeover behavior changed. Native software emitters
now attach the observation tag; the listener is never paused.
