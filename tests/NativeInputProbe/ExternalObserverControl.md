---
id: rug_external_observer_control
type: logic_node
inputs: [rug_startup_order_control, rug_script_tests]
outputs: []
tags: [diagnostics, hooks, raw_input, processes]
---

# Separate observer process C

## Reviewed startup-order evidence (2026-10-08)

The user's fresh hook-first / raw-first / hook-first pairs all retained the gap.
Phase timestamps prove successful nonoverlapping initialization in the selected
order. All three use the same Core/Raw/Hook DLL hashes and corrected callback-DLL
hmod. Every SendInput F8/B down/up returned 1 and each tested key reached B 1/1;
A's per-software-round keyboard callbackTotal remained 0. B's tagged F8/B counts
were 0/0 despite other keyboard callbacks. Human phase callbacks before/after,
PostMessage delivery and full cleanup remained intact. Raw message/cleanup totals
were 284/284, 254/254 and 402/402; remaining registrations were 0.
The ordering of these two bundled phases is insufficient to explain recovery.
Raw attribution was NOT COLLECTED, not zero-candidate or production-source failure.

## Roles and boundaries

| Process | Role | Hooks | Raw Input | Input |
|---|---|---|---|---|
| A | Existing native ManagedTarget sender | Existing native keyboard/mouse | None | Same Rug.Core C-ABI, F8/B SendInput and PostMessage |
| B | Existing target-native-messages stage | None; original idle message-only window/thread retained | Same native keyboard count-only receiver | None |
| C | New --hook-observer | Same corrected native keyboard callback DLL, on its dedicated managed message thread | None | None |

[ExternalHookObserver](../Rug.UI.Core.Script.Tests/ExternalHookObserver.cs#class:ExternalHookObserver)
reuses the existing Native/RawInputDiagnosticWindow deferred-install gate and
native callback wrapper. C's message-only window readiness precedes explicit
`install`; PREPARED does not mean Hook ready. Duplicate install commands reuse
the existing completion and cannot replace/reinstall the Hook. Baseline/snapshot
before installation are refused. Missing installation or EOF reports NOT RUN.
Install/binding/thread faults fail visibly; done removes the Hook, detaches the
counter, destroys the window and joins the thread before unloading its DLL.

C validates no Core/Raw DLL is loaded at readiness and no keyboard/mouse/page-wide
registration exists. It prints PID, generation/thread, callback module/path/hash,
install QPC bounds, snapshots/deltas, tagged F8/B down/up and aggregate processing/
chain timing. No physical key sequence or text is recorded. C has no visible target;
all automatic input must still go to B's validated isolated target. Printing and
console waits occur outside the Hook thread. Synchronization proves pump health
only. A and C share Windows' low-level Hook mechanism, not independent sensors.

This is a STRUCTURAL role control, not a strict single-variable experiment:
observer process/module ownership, process startup and Hook-chain installation
context change. B no longer loads/attaches its native callback counter; its
existing no-Hook stage retains target, idle thread, native Raw receiver and Core
load. Production event source, input implementation and native binaries are
unchanged. C installs AFTER B Raw readiness, matching the raw-first relation
of the latest failed baseline, but not claiming identical cross-process timing.

## Commands and manual session

Build managed diagnostics once (root-pinned SDK); do not rebuild native binaries
or change elevation, G HUB/PowerToys state between comparison sessions:

```powershell
dotnet build F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -warnaserror
```

Close all previous probe processes. Start A FIRST, wait at its HWND/PID prompt:

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode ManagedTarget
```

Start B SECOND, wait for its READY snapshot and owned keyboard registration:

```powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --hook-isolation-stage target-native-messages
```

Start C THIRD:

```powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --hook-observer
```

1. In C type `install`; require INSTALLED hookInstalled=True, attached=1,
   threadMismatch=0, callback-DLL hmod and rawRegistrations=0. C has no white window.
2. Copy B's MANAGED TARGET HWND/PID into A's prompt. Do not use C's message window.
3. HUMAN BEFORE: use keyboard/mouse in B's white window and another application;
   verify ordinary input/shortcuts. Capture B `baseline`, then C `baseline`.
4. In A type `next`; use its five-second countdown to activate B's white window
   by the title bar. Release keys/buttons, hands off through all four AUTO rounds.
5. After A prints HUMAN AFTER, capture B `snapshot`, then C `snapshot` BEFORE
   deliberately performing HUMAN AFTER input. Terminal typing contributes ordinary
   unflagged callbacks to these broad deltas; they are not A's exact AUTO windows.
6. Repeat HUMAN AFTER keyboard/mouse/foreground/background/shortcut checks in the
   same live session. A `done`; B `snapshot`; C `snapshot`; B `done`; C `done`.
7. Return THREE COMPLETE logs (A/B/C), normal-operation confirmation and any
   accidental AUTO input. Keep stage/PID/thread/hash/timestamp headers and cleanup.

Across AUTO C should observe tagged injected F8 down/up=1/1 and B down/up=1/1
(four keyboard events), with no parse/binding/thread fault; PostMessage adds no
Hook-tagged events. Do not demand zero broad unflagged counts caused by terminal
commands. A retains its per-round acceptance/interference checks and native send
records. B's final tested-key totals normally include both modes (2/2 per key).
The Raw receiver remains count-only: software/candidate evidence NOT COLLECTED.

## Predeclared result branches

| Observation (send/target complete, stable identities) | Next interpretation/action |
|---|---|
| A and C complete tagged keyboard evidence | Supports a process-role/co-location correlation; repeat original co-located baseline in fresh processes before claiming reverse confirmation |
| A missing, C complete; or A complete, C missing | Observer-specific gap; compare install timing/lifecycle/chain evidence, do not call both recovered |
| A and C both missing | Separating the observer is insufficient; next minimal pure C++ target/receiver role control with the SAME Core sender |
| C taggedUnflagged > 0 | Classification/structure evidence; cannot describe it as missing callback |
| Failed install, incomplete send/target, changed hashes, ownership loss or AUTO interference | Inconclusive; resolve that defect before interpreting topology |

Even recovery here does not close the original integrated diagnostic or prove
production Raw Input classification. No automatic takeover, listener pause,
ignore window, Hook removal/reinstallation or software-send changes are introduced.

## Automated boundary

[ExternalHookObserverTests](../Rug.UI.Core.Script.Tests/ExternalHookObserverTests.cs#class:ExternalHookObserverTests)
runs five real child-process lifecycles while parent B owns its actual target,
idle thread and native Raw registration. It checks role exclusivity, no Core/Raw
load or registration in C, correct callback module, single installation, pre-install
refusal, EOF/uninstalled NOT RUN, preserved B registration/target protocol and
clean child/parent disposal. Posted protocol messages do not validate physical
SendInput or human observation. Run `--observer-tests`, `--raw-input-tests` or
the ordinary Lua/Gateway suite. Human/AUTO/Human three-process acceptance is
**NOT RUN by the development agent; pending manual execution**.
