---
id: rug_native_input_probe
type: logic_node
inputs: [rug_core]
outputs: []
tags: [diagnostics, input, hooks, native]
---

# Independent x64 native input probe

[Controlled managed-component stages](ControlledStages.md) keep native emission
fixed while adding DLL load, target window and individual Hook registrations.
The next [module/send evidence control](InputEvidenceControl.md) separates native
send returns, specific F8/B target delivery and the unresolved Hook discrepancy.
The next [startup-order comparison](StartupOrderControl.md) retains those binaries
and compares two B initialization phases in fresh paired sessions.
All three ordered pairs retained the gap. The next [external observer control](ExternalObserverControl.md)
moves the same callback to process C while B retains its target/native Raw receiver;
it adds no software input, native binary changes or automatic takeover.
The next [manual B/C installation order](ManualHookOrderControl.md) prepares both
observers and B Raw registration before either Hook, changing only install order.
That B-C/C-B/B-C user run recovered/missed/recovered all three observers. The next
[native B/C host comparison](NativeRolesControl.md) retains A and both diagnostic
DLLs while replacing B/C hosting; it is a structural comparison, not a CLR diagnosis.
The pure-native reversal also recovered/missed/recovered. The next
[Raw owner comparison](RawOwnerControl.md) keeps Hook order B-C and target/Core in
B, relocates the unchanged receiver to C, then restores owner B in fresh sessions.
The user's nine B-C logs were complete in all three owners. The same guide now
records fixed C-B/owner B-C-B missing/complete/missing. The next
[target owner control](TargetOwnerControl.md) fixes Core/Raw B and C-B Hook order,
moving target B/C/B with --target-owner B|C; the user reproduced
missing/complete/missing. The next [B diagnostic Core load control](CoreLoadControl.md)
keeps target/Raw B, Hook C-B, and A's emission unchanged while comparing B's
--core-load on/off/on. The user's load reversal missed in all three sessions;
B's diagnostic Core load is not necessary. The next
[registration on/off/on control](RawRegistrationControl.md) keeps B Core off,
target/receiver B and C-B Hook order, retaining the receiver infrastructure.
The user's registration reversal missed/recovered/missed across all three
observers with complete specific targets. The next
[DEVNOTIFY control](DeviceNotifyControl.md) keeps keyboard registration and
INPUTSINK, comparing only device-notify on/off/on; the user's run missed in all
three sessions. The next [INPUTSINK control](InputSinkControl.md) keeps keyboard
registration, DEVNOTIFY off and tests sink on/off/on; all three user sessions
retained the gap, including flags zero. The next [A+B reduction](TwoProcessControl.md)
removes C without rebuilding any binary. `tests/run_native_two_process_tests.ps1`
checks actual registration, one-shot install, target binding and cleanup without
AUTO. The clean A+B user retry reproduced both SendInput keyboard gaps with
complete sends/targets and no observed AUTO interference. See
[evidence, thread audit and registration OFF control](TwoProcessRegistrationControl.md).
The existing two-process runner now accepts -RawRegistration on|off; no native
binary changed. Next manual check needs two OFF A/B logs, followed by fresh ON
restoration if OFF recovers. Original integrated acceptance remains open.
`-Mode ManagedTarget` binds the selected owner's validated diagnostic HWND/PID and keeps the
native Rug.Core sender; see the controlled guide for startup/focus/counter rules.

See [paired-session evidence and source audit](HookComparison.md) for the passing
reversed-order session and the unresolved install-order correlation. CoreInput
prints the actual loaded DLL path; the runner labels its pre-run SHA-256 separately.

[Pump](main.cpp#class:Pump) uses C++ Win32 callbacks, a dedicated hook message
thread and a separate isolated target-window thread. It does not use CLR,
managed diagnostic callbacks or register Raw Input devices. Hook installation
uses WH_KEYBOARD_LL/WH_MOUSE_LL with immediate per-call error capture. Negative
nCode is counted and forwarded without dereferencing lParam. Every event is
forwarded; no listener pause, ignore window or automatic reinstallation exists.

[Counter](main.cpp#class:Counter) reports entry/actionable counts, independent
injected/unflagged and tag/intersection counts, parse errors and separate own /
CallNextHookEx total/max QPC ticks. Maxima are lifetime values even in delta lines.
Native offsetof static assertions verify x64 keyboard/mouse flags and extra-info
layout. Snapshot reads are detached atomic aggregates, not cross-thread atomic
transactions. Only known **tagged synthetic** F8/B down/up counts are separated;
target message F8/B aggregates are also separated without asserting their origin;
no human key sequences, text or full mouse trajectories are printed or saved.

Each receiver has a generation, installation OS thread ID, architecture, install
results and thread fault/exit reason. Successful installation or a message barrier
is not proof of continuing Hook health. Post-start failures survive readiness,
cleanup failures prevent success, and ordinary exit removes hooks/windows/classes
and joins threads. Input and waits run in the console harness, never a host UI.

[CoreInput](main.cpp#class:CoreInput) loads x64/Debug/Rug.Core.dll next to the probe
using the current **unchanged** C-ABI. Moves, click, F8/B down/up and button release
use real Rug.Core Win32InputController emitters with the existing 0x52554749 tag.
Opt-in bounded native send recording preserves the input algorithm and ABI.
The probe never injects into a game/existing app. The
interactive mode refuses foreground input unless its own isolated target is
already foreground; it does not bypass Windows activation policy. Background
PostMessage remains confined to that window. No host grants/takeover are changed.

## Build and modes

Requires Visual Studio v145 x64 C++ tools and Windows SDK. From repository root:

```powershell
.\tests\run_native_input_probe.ps1 -Mode Test -BuildCore
.\tests\run_native_input_probe.ps1 -Mode Human
```

The runner locates MSBuild via vswhere and builds NativeInputProbe.vcxproj plus
the [diagnostic receiver DLL](NativeReceiverControl.md) and separate
[keyboard callback DLL](NativeHookCallbackControl.md). Outputs are ignored
x64/Debug/Rug.NativeInputProbe.exe, Rug.NativeRawReceiver.dll and
Rug.NativeKeyboardHook.dll. None of these projects
links to Rug.Core; `-BuildCore` explicitly builds/deploys the instrumented Core DLL.
The test DLLs load only in B's opt-in
native controls; A's sender and Hook implementation remain unchanged.
Test/Human/SelfTest need x64/Debug/Rug.Core.dll and dependencies. See the evidence
guide for deployment and fixed-binary comparisons.

| Mode | Behavior |
|---|---|
| Build | Build only |
| Test | Deterministic classification/lifecycle/send-buffer/target-protocol tests and real Rug.Core PostMessage success/failure on an isolated hidden window; no physical SendInput |
| Human (default) | HUMAN BEFORE -> AUTO F8/B in both modes -> HUMAN AFTER |
| SelfTest | Same native software rounds without human acceptance; denied foreground is NOT RUN |
| Observe | Passive independent native Hook observer, no synthetic input or DLL needed |
| NativeB / NativeC | New Rug.NativeRoleProbe.exe hosts target/Raw/deferred Hook (B), or only deferred Hook (C); explicit install command, no emission |
| RoleTest | Native role layout/target/registration/conflict/one-shot install/fault/cleanup tests; no physical input |

`-Mode Build` also builds NativeRoleProbe.vcxproj, a separate x64 native executable.
Run `tests/run_native_role_tests.ps1` after building for real A/B/C subprocess gate,
protocol binding, order/duplicate/EOF/cleanup checks without AUTO. Core, sender EXE,
Callback DLL, Core and A sender remain unchanged; the test Raw DLL gains
optional registration/notification constructors and actual registration-call
evidence. Legacy exports/snapshots remain compatible. No production or solution reference is added.
Role EXE's `--raw-owner B|C` selects the receiver host, default B. Run that script
with `-RawOwner B` and `-RawOwner C` for both contract variants; direct EXE
commands in the owner guide keep both roles' selected owner consistent.
Independent --target-owner B|C defaults B. Subprocess tests also accept
-TargetOwner B|C; all four owner combinations verify the selected Core mode, real A
binding to the selected target, preserved target identity and complete cleanup.
Old NativeB/NativeC runner modes retain both default owners B.
--core-load on|off defaults on and affects only B's diagnostic module load; C
always excludes Core and A's sender is unchanged. Contract tests accept
-CoreLoad on|off and verify observed module presence for all eight combinations.
--raw-registration on|off defaults on; only the selected receiver's keyboard
registration changes. Off retains its DLL/window/thread/timer, verifies zero
registrations and reports infrastructure readiness, not device readiness.
--device-notify on|off defaults on and changes only DEVNOTIFY. Registered mode
retains INPUTSINK; RAW_REGISTER_CALL reports actual requested flags, return/error,
QPC timestamps and receiver identity separately from queriedFlags (which may omit
DEVNOTIFY). Contract tests accept -RawRegistration and -DeviceNotify on|off.
--input-sink on|off defaults on. Off keeps the keyboard registration and selected
receiver HWND, using default foreground reception; it is distinct from
--raw-registration off. Actual flags zero must still have succeeded=1, valid
call identity/clocks and rawRegistrations=1. Contract tests also accept
-InputSink on|off: all 64 combinations, 37 pre-start rejections and 14
legacy/optional CLI checks. Previous test exports/layouts remain compatible;
Probe_CreateWithFlags is a tenth optional test export. Core/emitter unchanged.

### First run alone

1. Close other probe processes and do not run other Rug input tasks.
2. Run `-Mode Human`. Use the **Rug independent native probe** white window for
   normal keyboard/mouse input, then another app; verify ordinary shortcuts work.
3. Return to its terminal and type `next`. In the 5-second countdown, activate
   the native white target by its title bar; release keys/mouse before AUTO.
4. Keep hands off throughout all four native software rounds. Each prints native
   keyboard/mouse deltas, tagged tested-key down/up and actual target down/up.
5. Repeat human keyboard/mouse input during HUMAN AFTER, type `done` and retain
   the output. Thread generations must stay unchanged and cleanup must be clean.
   Missing BEFORE human callback evidence keeps the session INCONCLUSIVE, even
   if software rounds continue for diagnosis; it cannot become accepted.

Native SendInput verification also requires complete native send records and
exact specific tested-key target down/up=1/1. Hook verification requires tagged
injected key >=2, tagged injected mouse >=5, tested-key down/up >=1 each, no
parse/thread fault and no untagged-unflagged interference. PostMessage requires
target delivery and no tagged hook input; ordinary user interference is NOT RUN.
This is **independent Hook verification**, not Raw Input candidate acceptance.
Exit 0 means checks passed (Observe means clean passive observation only), 2 means
missing/inconclusive/interfered evidence; failures exit 1. Human actions are NOT
RUN by the development agent and cannot be replaced with fake events.

### Then compare against managed input in two terminals

1. Start fresh terminal A with `-Mode Observe`. Do not run native Human at the
   same time. The observer's spare target stays hidden and sends no input.
2. Start terminal B with the existing managed command:

   ```powershell
   dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --raw-input-diagnostic
   ```

3. Perform managed HUMAN BEFORE. In A type `baseline`, then return to B, type
   `next` and activate the **managed** white window within its 5-second countdown.
4. Wait hands off for managed AUTO to complete. In A type `snapshot`. Compare
   its tagged F8/B down/up and taggedInjected counters against B's per-key deltas.
   Observer phase unflagged totals naturally include terminal/focus human input;
   they are not an interference verdict for the managed AUTO round.
5. Complete managed HUMAN AFTER and `done`; verify observer still sees human
   callbacks, then type `snapshot` and `done` in A. Both processes must clean up.

A native observer that sees the *same managed sender's* tagged keyboard events
while the managed Hook does not narrows investigation to managed probe/thread or
hook-chain differences. If neither sees them while both see humans, investigate
shared input path/environment/Hook chain; do not simply blame Windows. If only
standalone native sending works, sender/process context is also a variable.
Initial comparison installs native before managed; any reversed-order comparison
must use fresh processes and be explicitly labeled. User-operated G HUB/PowerToys
comparisons change one condition at a time. No automatic reinstall masks failure.

## Current verification

Native strict x64 build and Test mode pass. Real PostMessage/F8 and PostMessage/B
pass with actual target down/up=1/1 and zero native Hook input. Agent SendInput
rounds are NOT RUN because the native target is not foreground. Human standalone
and two-process comparison remain NOT RUN / pending user operation. These results
do not locate the managed keyboard-probe cause or accept automatic takeover.
