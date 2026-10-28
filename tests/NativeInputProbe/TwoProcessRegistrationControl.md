---
id: rug_native_two_process_registration_control
type: logic_node
inputs: [rug_native_two_process_control]
outputs: []
tags: [diagnostics, native, raw-input, reproduction]
---

# Native A+B: retained receiver, registration reversal

The three-session ON/OFF/ON review is complete; see the
[expert review package](RegistrationReversalReview.md) for all six artifact IDs,
hashes, specific targets, Hook observations and clocks. F8 insertion/target
delivery is complete in all three; Hook evidence is missing/recovered/missing.
OFF SendInput/B has complete Hooks but target down/up=0/1, retained as unresolved.
Do not label the entire OFF suite passed. Next freeze and review the reproduction,
not another identical run. The instructions below are the completed test protocol.

## Evidence package (2026-10-10)

First A+B attempt (A/B PID 57908/54892): F8 target delivery was complete with
Hook absence, but SendInput/B observed seven untagged-unflagged keyboard and 797
mouse events, specific B down/up=0/1. Preserve that round as interfered/NOT RUN;
generic key counts 1/1 cannot replace specific B. Do not merge sessions.

Clean user retry (A/B PID 55004/54592, target 0x2ca0dae) retained the
[fixed binaries and A+B commands](TwoProcessControl.md). Both diagnostic DLL
hashes matched. B Core/CLR absent, no C launched as instructed. A records Core
path/2649600 bytes, not loaded SHA256. Logs do not certify absence of unrelated
observers; session instructions require closing old probes.

| Separate evidence | Clean retry |
|---|---|
| Native send calls | Eight requested=1/returned=1, failureError=0, dropped=0 |
| Specific F8/B target | Four rounds down/up=1/1; B baseline 0/0 -> first snapshot 2/2 per key |
| A exact AUTO keyboard | Entry 0 in both SendInput rounds and complete AUTO interval |
| A mouse AUTO | Ten tagged injected events; untagged-unflagged activity zero |
| B keyboard | No tagged/injected events or tagged F8/B down/up through END |
| PostMessage | Both passed |
| HUMAN BEFORE/AFTER | A keyboard 481/45, mouse 14181/4185; same generation |
| Raw count/cleanup | Baseline 45 -> first snapshot 49 -> STOPPED 97/97 |
| Cleanup | Threads exited, callback detached, target destroyed, registrations zero; no reported faults |

B PREPARED 36296279765 < Hook install 36393175575..36393175689 < baseline
36947812282 < first send 37034587786 < final send end 37035220489 < first snapshot
37233365314 (QPC frequency 10000000). Successful flags-zero registration at
36296113247..36296113641 retained thread/HWND identity and never repeated.
B's first snapshot includes 28 unflagged keyboard events across terminal/focus
operations; it is not an exact AUTO boundary. Use A's per-round/exact AUTO
zero-unflagged evidence. Do not assert B had no entry callbacks in that broad
interval. Raw increase by four is not tagged-key evidence: packets are unparsed,
software/candidate attribution remains NOT COLLECTED.

Source artifacts are the user's clean retry attachments A
4ad2ae79-7b7f-4c79-82fd-5930a3e42ed2 and B
f6b6e746-3de4-417b-a251-c83c6da0f167. Original pasted logs stay outside the
repository; this package retains only aggregate diagnostic evidence.

The clean A+B reproduction check is complete; original integrated acceptance
is open. C and managed hosting are not necessary for this reduced gap. Normal
foreground/background/shortcut behavior requires user visual confirmation.
No production event-source defect or particular Windows cause is established.

## Thread/message audit

[RunRole](NativeRoles.cpp#function:RunRole) creates target, Hook window/thread,
then RawHost; selected Raw readiness is awaited before PREPARED. Hook installs
once on its thread after console install. Retry B identities:

| Component | OS thread / HWND | Source behavior |
|---|---|---|
| Target | 57192 / 0x2ca0dae | Visible ordinary window, atomic legacy/specific F8/B counters, protocol v2 |
| Hook | 48700 / 0x750f9e | Message-only window, callback-DLL hmod, same-thread attach/install |
| Raw | 55668 / 0x12e0cbc | Separate message-only window, keyboard usage 6, flags=0, explicit registration HWND |

[RoleWindow](NativeRoles.cpp#class:RoleWindow) and
[Receiver](RawReceiver.cpp#class:Receiver) use unfiltered GetMessageW with null
window; error returns are checked. Command waits run outside Hook callbacks.
[callback](KeyboardHook.cpp#function:callback) aggregates entry/classification/
thread/timing, always calls CallNextHookEx and returns its result. Downstream
chain behavior is not certified by this audit. Raw Handle counts, verifies
ownership and calls DefWindowProcW for WM_INPUT; no packet reads/consumer work.
Registration lock is local to the test DLL; scope review found no synchronous
Raw-to-Hook stop/input path. This is not proof against transient scheduling or
external Hook-chain issues; no source evidence justifies rewriting threads yet.

Microsoft's [Hook contract](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)
requires a message loop on the installing thread. The
[registration contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices)
has one receiver per process/device class. The
[device contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice)
defines flags zero as foreground registration, not removal, and hwndTarget as
the delivery window. The [WM_INPUT contract](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-input)
requires foreground DefWindowProc cleanup and zero return. These contracts do
not predict that adding this registration must suppress Hook observation.

## Completed test protocol: ONLY registration OFF, still A+B

The earlier registration reversal included C. Test this condition in the new
smallest host topology before changing target/receiver/thread arrangements.
Keep A first, no C, target/receiver B, B Core OFF, DEVNOTIFY/INPUTSINK OFF and
Hook install after B PREPARED. Preserve all five binaries, elevation and external
applications. Only --raw-registration changes. OFF retains DLL/window/thread/
timer/barrier/ownership/fault/cleanup; no registration call is made.
receiverReady=1 in OFF certifies infrastructure, NOT device readiness.

First run one fresh OFF session (two logs). If it recovers, restore ON in another
fresh session before assigning correlation. No rebuild or in-session switching.

```powershell
# Terminal A first; wait MANAGED TARGET prompt.
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeInputProbe.exe --managed-target
# Terminal B: ONLY raw-registration off differs from clean retry.
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner B --core-load off --raw-registration off --device-notify off --input-sink off
```

1. Close old probes. B PREPARED/COMMANDS: rawRegistrations=0, receiverReady=1,
   target=1, Core/CLR absent, Raw thread/window still nonzero. RAW_REGISTER_CALL
   fields all zero; succeeded=0 here means NOT CALLED, not a registration failure.
   B install -> wait INSTALLED; no C preparation needed despite old ABC hint.
2. Paste B NATIVE TARGET HWND/PID into A. HUMAN BEFORE in target/another app,
   keyboard/mouse/ordinary shortcuts. B baseline -> A next.
3. FIVE-second countdown: focus B target, release keys/buttons, hands off all
   four AUTO rounds. Individual PASS/INCONCLUSIVE lines are not completion.
4. Only at A HUMAN AFTER: B snapshot -> human checks -> A done -> B snapshot
   -> B done. Return full A/B logs and any accidental AUTO/normal-operation
   observations. Raw input/device counts must remain zero.
5. If asked to restore ON, use fresh processes, original B command with only
   --raw-registration on, same sequence and two more logs. Do not rebuild.

## Predeclared branches and validity tests

| Clean sends/targets/identities/hash/lifecycle | Branch |
|---|---|
| OFF restores A/B tagged injected F8/B; fresh ON misses again | Registration condition participates in A+B; package minimal reversal for expert review before further changes; not a Raw API cause claim |
| OFF still misses | Registration not necessary for this A+B gap; retain differing ABC results and audit reproducibility/context |
| Only one observer recovers | Preserve A/B entry/classification/per-key separately; shared mechanism is not independent sensor proof |
| Restored ON also recovers | Original condition unstable; no reversal claim |
| Interference, wrong target/focus/hash, send/registration/cleanup fault | Inconclusive; repair validity first |

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_two_process_tests.ps1 -RawRegistration on
& F:/Cardinal/Rug/Rug/tests/run_native_two_process_tests.ps1 -RawRegistration off
```

Both modes passed on 2026-10-10: three fresh A+B lifecycles and three incomplete
B cleanup cases each. Checks cover actual registration mode, stable receiver/
target identities, one-shot install, real A binding without AUTO and cleanup.
OFF requires zero registration/call/input/device counts with a live ready receiver.
Default remains ON; invalid mode rejected before startup. Five file hashes
unchanged. No native/product source or C-ABI changes, no rebuild. Real OFF AUTO/
HUMAN was not performed by the agent; later user logs are separately reviewed in
the expert package above. WinUI/product-wide regressions were not rerun for this
script/document-only change. Automatic takeover stays closed; no filters,
rehooks, sender changes, timing-ignore windows or deleted failing checks.
