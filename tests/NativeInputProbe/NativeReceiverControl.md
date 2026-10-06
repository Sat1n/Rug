---
id: rug_native_receiver_control
type: logic_node
inputs: [rug_hook_controlled_stages, rug_script_tests]
outputs: []
tags: [diagnostics, raw-input, native, isolation]
---

# Same-process native keyboard receiver control

## Evidence and audit (2026-10-06)

Mouse-only count registration passed both software keyboard Hook observers.
Restoring both registrations restored missing callbacks. The user's subsequent
keyboard-only count run again missed F8/B software callbacks in both Hooks,
although each target delivered down/up 1/1. Native mouse Hooks observed all 10
tagged injected events; human keyboard callbacks before/after were 75/52.
Actual registration was one owned keyboard, no mouse. Input/cleanup totals 293,
timer 587, no faults, all threads exited and final registrations zero. Software
Raw Input/candidate attribution was NOT COLLECTED; phase snapshots include human
terminal activity and are not isolated AUTO interference counts.

Source audit found no proven fix: managed Device has ushort page/usage, uint flags,
pointer target; keyboard usage 6 and 0x2100 means INPUTSINK|DEVNOTIFY, no NOLEGACY.
MSG contains pointer-sized HWND/WPARAM/LPARAM and correct trailing fields.
Count control does no packet reading, preserves DefWindowProc cleanup/zero return,
and forwards both Hooks. Public production behavior is unchanged. These checks
exclude some simple mistakes, not every runtime issue or a Windows root cause.
See Microsoft's [registration ownership](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices),
[flags](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice)
and [WM_INPUT cleanup](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-input).

## Replacement boundary

keyboard-native-messages compares against keyboard-messages-keyboard. Only B's
receiver implementation changes: [Receiver](RawReceiver.cpp#class:Receiver)
uses native registration, WNDPROC, GetMessage/TranslateMessage/DispatchMessage,
ownership checks and cleanup, loaded in the SAME B process. An independent
process receiver would change registration ownership and is not this control.
The host still creates the dedicated managed Thread and calls blocking Probe_Run
there. Target and managed keyboard Hook retain their separate existing threads;
A retains native keyboard/mouse Hooks and the same Rug.Core sender.

Keep keyboard usage 6 only, flags 0x2100, HWND_MESSAGE, 250ms timer, registration
conflict rejection, timer/input/device ownership checks and count-only handling.
Both foreground and sink WM_INPUT call DefWindowProc and increment cleanup;
no GetRawInputData, raw parser/tracker, source queue or candidate counters exist.
No Hook filtering/reinstallation, delay-ignore window, takeover or input changes.

The test DLL has no DllMain registration or Rug.Core/solution reference. It loads
only through [NativeRawInputDiagnosticReceiver](../../Rug.UI.Core/Native/NativeRawInputDiagnosticReceiver.cs#class:NativeRawInputDiagnosticReceiver)
in Native/, with a separate TEST ABI (not Rug.Core C-ABI). Six exports manage an
opaque receiver, blocking Run, copied fixed 96-byte snapshots, stop, sequence
barriers and destruction. No managed callback executes on the message path.
Native locks protect scalar snapshots; barriers/readiness poll asynchronously
outside the receiver thread. Implementation replacement includes native callback
and readiness handoff timing: a difference narrows investigation, not to one API.

Faults survive readiness (registration=1, ownership=2, native=3, cleanup=4,
callback=5, unexpected quit=6; Win32/cleanup errors retained). Stop posts quit,
cleans only still-owned keyboard registration/window/class, then the host joins
the actual thread BEFORE destruction or DLL unload. Replacement owners survive.
Repeated disposal shares one completion; disposal before start releases the DLL.
Successful barriers/timers still do not certify keyboard Hook delivery.

## Paired user run

Fresh A/B processes; keep external applications and elevation unchanged.

1. Start A FIRST (runner builds both probe and receiver DLL; not production core):

   ~~~powershell
   & F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode ManagedTarget
   ~~~

2. At A's HWND/PID prompt start B SECOND:

   ~~~powershell
   dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage keyboard-native-messages
   ~~~

3. Wait for ISOLATION READY. NATIVE RECEIVER prints actual loaded-module path,
   SHA-256 and sameProcessId equal to B's MANAGED TARGET PID. PUMP shows Ready,
   KeyboardRegistrationEnabled=True, MouseRegistrationEnabled=False, reading
   False, no Failure, and three distinct target/Hook/Raw thread IDs. KEYBOARD
   REGISTRATIONS must show count=1, keyboardOwned=True, mousePresent=False.
   Managed keyboard Hook installed, mouse absent; A still has both Hooks.
   Paste B's HWND/PID into A as 0xHANDLE PID.
4. HUMAN BEFORE in B's white target and another app; check normal shortcuts.
   B baseline, A next. During the five-second countdown activate B target,
   release keys/buttons and keep hands off. Do not manually press F8/B in AUTO.
5. After all four rounds B snapshot; HUMAN AFTER. A done; keep B alive until A
   exits, then B snapshot/done. Return both complete logs and normal-operation/
   accidental-interference confirmation. Actual final registrations/HWND zero,
   threads exited, input/cleanup totals equal and no faults are required.

## Branches and automated coverage

| Evidence | Next direction |
|---|---|
| Native receiver restores both keyboard observers with complete target | Compare managed/native receiver implementation and readiness/callback behavior; reverse-confirm before any production repair |
| Both still miss software keys, humans continue, targets complete | Managed receiver implementation is not necessary; examine retained process/target/Hook/keyboard registration interaction |
| Fault, wrong ownership/process, missing targets or interference | Inconclusive; resolve that evidence first |

Strict native x64 build and managed lifecycle tests are separate from paired
software/human acceptance. Tests reuse the same assertions for real registrations,
three separated threads, timers, malformed foreground/sink messages, device
messages, conflicting startup, unexpected mouse registration, replacement-owner
preservation, generations, idempotent/unstarted disposal and full cleanup.
Build with tests/run_native_input_probe.ps1 -Mode Build before normal script tests;
missing DLL explicitly reports native lifecycles NOT RUN, never as passed.
Interactive paired AUTO/HUMAN is NOT RUN by the agent. Original integrated failure
still requires supported repair and original diagnostics; automatic takeover waits.

## No managed Hook: target-native-messages

The user's native-receiver run retained missing tagged software keys in both
Hooks. SendInput/B delivered target down/up 1/1; SendInput/F8 delivered 0/1,
so F8 does NOT supply complete-target evidence. Both PostMessage rounds passed.
Native mouse Hooks observed 10 tagged injected events; human keyboard BEFORE/
AFTER callbacks were 85/81. Same-process keyboard-only ownership was verified,
input/cleanup totals 307, timer 426, no faults and zero final registrations.
Thus managed Raw Input callback/loop implementation is not necessary for the
B-key reproduction. No Windows or Hook-chain root cause is established.

target-native-messages changes only B's managed keyboard Hook installation from
keyboard-native-messages. Keep its idle message-only window/thread, the SAME
native receiver DLL/registration/timer/count handling, managed visible target and
A's native sender/two Hooks. This does not weaken original Hook verification:
B's omitted counters are EXPECTED ZERO; A is the sole Hook observer in this mode.

Use the same A-first command and paired HUMAN BEFORE/AUTO/HUMAN AFTER sequence
above. At A's HWND/PID prompt, B SECOND:

~~~powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage target-native-messages
~~~

Check MANAGED HOOKS OMITTED, ISOLATION READY keyboardRequested=False/mouseRequested=False,
both managed Hook flags False/error 0, and separate target/idle/Raw thread IDs.
Native PUMP remains keyboard registration True/mouse False/reading False/no
Failure; actual registration count=1, keyboardOwned=True, mousePresent=False.
Native receiver sameProcessId matches B; its DLL hash should match the preceding
native-receiver control when not rebuilt. A's two Hooks must remain installed.
Raw software/candidate evidence remains NOT COLLECTED. B zero Hook counters and
healthy barriers are NOT software acceptance. F8 and B target counts are judged
separately; any missing down/up remains INCONCLUSIVE, even if Hook counts recover.

| Evidence | Direction |
|---|---|
| A's tagged keyboard down/up and targets complete | Missing managed Hook installation correlates with recovery; reverse-confirm before repairing |
| A still misses software keys, human callbacks/targets complete | Managed Hook is not necessary under this native receiver; investigate retained process/target/registration conditions |
| Partial target delivery, fault, interference or wrong registration | Keep that round inconclusive; do not use another key's success to hide it |

Three additional no-managed-Hook native lifecycles verify the unchanged receiver,
idle thread flags/zero counters, target query/delivery, barriers/timers, conflicts,
ownership loss, foreign-owner preservation, generations and cleanup. Prior native
Hook-enabled tests remain. Paired AUTO/HUMAN is NOT RUN by the agent. No production
listener, input emitter, C-ABI, Hook forwarding or automatic takeover change.
