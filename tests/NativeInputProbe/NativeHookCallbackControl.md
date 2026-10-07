---
id: rug_native_hook_callback_control
type: logic_node
inputs: [rug_native_receiver_control, rug_script_tests]
outputs: []
tags: [diagnostics, hooks, native, isolation]
---

# Native keyboard callback on the original managed Hook thread

## Evidence and boundary (2026-10-07)

With the same keyboard-only native receiver DLL, B's managed keyboard Hook
installed: both A/B software keyboard Hook counters missing. Omit that Hook
installation, keeping its idle thread: A observed four tagged/injected key events,
F8/B down/up 1/1; B target delivered 1/1, F8 target 0/1 remained inconclusive.
Restore B's Hook: both Hook gaps returned, while BOTH targets delivered 1/1.
Mouse software counts and human keyboard callbacks remained present. This
missing/recovered/missing correlation does not establish a callback or CLR defect.

`keyboard-native-callback` compares against `keyboard-native-messages`.
The historical experiment changed B's callback pointer to a separate count/forward
DLL but retained GetModuleHandleW(null), which was not the callback's owning module.
The [module/send contract repair](InputEvidenceControl.md) now defaults to the
actual callback DLL hmod and offers `--hook-module original` for the reverse
control. It keeps WH_KEYBOARD_LL=13, thread parameter 0, the original managed
Hook thread/message loop/window and original window instance. Target thread/window, A sender/two native Hooks,
and B native Raw receiver remain unchanged. No managed delegate runs inside this
keyboard Hook callback. No reinstallation, filtering or input-ignore interval.

The existing Rug.NativeRawReceiver.dll is not modified/rebuilt by source changes:
verified bytes=132096, SHA-256
`95F89393E3F599B9FC03782899B3067AE119B91C731730FA310B83D63D979DF5`.
The runner builds a separate NativeKeyboardHook.vcxproj into
x64/Debug/Rug.NativeKeyboardHook.dll. Neither test DLL modifies the Rug.Core ABI,
production receiver, emitter, grants or takeover behavior.

[NativeKeyboardHookDiagnostic](../../Rug.UI.Core/Native/NativeKeyboardHookDiagnostic.cs#class:NativeKeyboardHookDiagnostic)
loads the callback DLL only for this explicit control. Scalar snapshots expose
entry/actionable/injected/unflagged/tagged intersections, null parse errors,
own/chain QPC totals/lifetime maxima, tagged synthetic F8/B down/up, bound thread,
attachment and thread mismatch. Reads are independent copies of atomic aggregates,
not transactional event snapshots. Negative/non-action nCode never reads lParam.
Every actual callback forwards unchanged; no human key contents/trajectories are
saved. Native callback count storage supports one active owner per process and
rejects replacement. Unhook must succeed, then detach on the installation thread,
join the actual thread, and only then destroy storage/unload. If unhook/detach
fails, retain the DLL/storage and report a cleanup fault; do not claim success.

The receiver STILL does not read Raw packets: software/candidate attribution is
NOT COLLECTED. A/B share the low-level Hook mechanism; target processing is a
separate evidence lane. No observation is inferred from pump readiness.
Timers/barriers do not certify Hook health. Aggregate software key counts are
cumulative snapshots; keyboard DELTA retains per-phase callback classification.

## Paired run

Use the [three-round original/callback/original procedure](InputEvidenceControl.md)
next. The commands below select the repaired default, not the old experiment.
Use fresh A/B processes, same elevation/external applications and no other Rug
input tasks. A FIRST (build/deploy Core once as instructed above):

~~~powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode ManagedTarget
~~~

At A's diagnostic HWND/PID prompt, B SECOND:

~~~powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --hook-isolation-stage keyboard-native-callback --hook-module callback
~~~

1. B READY: callback/receiver actual loaded paths, bytes/hash and process IDs;
   keyboard Hook installed True/error 0, mouse False; native callback attached=1,
   boundThreadId equals hookThreadId, threadMismatch=0, Hook hmod equals the callback
   module while window instance remains the main module. Target/Hook/Raw thread IDs
   distinct; Raw keyboard-only owned registration count=1, reading=False, no fault.
   Receiver hash must match above; otherwise keep the run inconclusive.
2. Copy B MANAGED TARGET HWND/PID to A. HUMAN BEFORE: use B's white target and
   another app for ordinary keyboard/mouse/shortcuts; verify they work normally.
3. B `baseline`, then A `next`. During the unchanged **5-second** countdown, focus
   B's white window by its title bar, release all keys/buttons, then hands off
   until all four AUTO rounds finish. Only A emits via the same Rug.Core DLL.
4. B `snapshot`. HUMAN AFTER: repeat foreground/background human input and normal
   operation checks in the SAME session. A `done`, wait for its complete cleanup;
   B `snapshot`, then `done` and verify receiver cleanup, native attached=0,
   all thread exits and registrations=0.
5. Send BOTH full logs plus normal-operation confirmation and any accidental
   interaction during AUTO. Terminal baseline/snapshot activity is not isolated
   AUTO interference evidence. F8/B delivery and observation are judged separately.

| Evidence | Next direction |
|---|---|
| Both Hooks and both targets complete | Callback implementation/managed transition condition correlates with recovery; reverse-confirm before repairing |
| Both Hooks still miss, targets/human phases complete | Native callback is insufficient; investigate retained install/pump/registration conditions without naming a root cause |
| Only A or B recovers | Compare Hook-chain/observer-specific evidence and installation identity |
| Partial target, fault, registration/hash mismatch or interference | Keep that round inconclusive; do not hide it with another key's success |

Automated parser/intersection/owner/unload-guard tests emit no input. Three real
callback-installation lifecycles PER hmod selection cover thread binding, target protocol, receiver
flags/conflicts/ownership loss/WM_INPUT cleanup and Hook thread fault cleanup.
Paired HUMAN/AUTO is **NOT RUN by the agent**. Reduced controls do not close the
original integrated SendInput failure. After a supported repair, rerun the original
raw-input-diagnostic HUMAN BEFORE -> AUTO -> HUMAN AFTER, including full raw,
Hook and target evidence. Automatic takeover stays deferred.
