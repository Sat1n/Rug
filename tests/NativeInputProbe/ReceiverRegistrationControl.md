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
& F:CardinalRugRug	estsun_native_input_probe.ps1 -Mode ManagedTarget
~~~

At A's HWND/PID prompt, start B SECOND:

~~~powershell
dotnet run --project F:CardinalRugRug	estsRug.UI.Core.Script.TestsRug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage keyboard-pump
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
cleanup is not AUTO acceptance. Interactive AUTO/HUMAN is NOT RUN by the agent.
Original managed-sender diagnostics must pass after a supported repair;
automatic takeover remains deferred.
