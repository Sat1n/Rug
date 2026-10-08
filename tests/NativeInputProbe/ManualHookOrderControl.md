---
id: rug_manual_hook_order_control
type: logic_node
inputs: [rug_external_observer_control, rug_script_tests]
outputs: []
tags: [diagnostics, hooks, raw_input, order]
---

# B/C Hook installation order after shared preparation

## Reviewed evidence (2026-10-08)

All ordered two-process baselines retained the software keyboard Hook gap, with
complete Core send returns, exact F8/B target down/up and clean disposal. Moving
the native callback to C while B kept target/Raw restored A/C; returning to the
co-located two-process baseline restored the gap (Raw cleanup 317/317).

Adding C with B's original Hook STILL INSTALLED restored A/B/C: each observed
four tagged injected keyboard events, F8/B down/up=1/1, while target totals were
2/2 per key across both delivery modes. B Raw message/cleanup totals were 449/449.
Thus removal of B's Hook is not required for recovery.

In the next control C loaded the SAME DLL and prepared its window/thread but
never installed a Hook. C PREPARED 46136878190..46136895541 preceded A software
calls 47041721101..47042281865; C END 47385942402 followed them. Attached=0 and
Hook install ticks=0 throughout. A per-round keyboard callbackTotal remained 0;
B tagged F8/B remained 0/0, despite human callbacks. Sends returned 1 and target
delivery remained exact. B Raw cleanup was 313/313, registrations remaining 0.
C's NOT RUN is expected for an intentionally uninstalled observer, not evidence
of a thread failure or successful software observation.

These rounds correlate recovery with installing C's additional Hook, not merely
the C process, DLL or idle window existing. They do NOT identify a Windows/Hook
chain cause. Raw attribution remains NOT COLLECTED; original acceptance is open.

Core/Raw/Hook SHA-256 stay those in [the startup guide](StartupOrderControl.md).
No native rebuild, production behavior/C-ABI changes or automatic takeover here.

The user's nine-log B-C/C-B/B-C run recovered/missed/recovered software keyboard
observations in all A/B/C, with complete sends/targets and clean disposal. Both
observers were prepared before installation. The next [native B/C host control](NativeRolesControl.md)
records the exact clocks and tests whether that correlation requires managed hosts.

## Manual gate contract

`--manual-hook-install` is accepted ONLY with keyboard-native-callback and
corrected callback hmod, with NO --startup-order. Unsupported combinations fail
before loading Core or creating windows. Existing immediate/ordered controls
retain their behavior. The [phase validation](../Rug.UI.Core.Script.Tests/HookRawStartup.cs#class:HookRawStartup)
and [console harness](../Rug.UI.Core.Script.Tests/RawInputDiagnostics.cs#class:RawInputDiagnostics)
reuse Native/RawInputDiagnosticWindow's existing single posted installation.

B creates the target and deferred message-only Hook window/thread, starts the
UNCHANGED native Raw receiver and awaits actual registration readiness. PREPARED
reports targetReady=True, hookWindowReady=True, rawReady=True, hookInstalled=False,
rawReadyObservedTicks and zero Hook install ticks. Raw readiness is the host's
observed readiness time, not the exact RegisterRawInputDevices call time.

Only `install` requests the Hook. Before posting, the harness verifies live Raw
registration ownership/window/pump health; failures do not silently enter Hook
installation. INSTALLED reports actual Hook QPC before/after ticks, correct native
module/thread binding and the original Raw readiness time. Repeated install uses
the original task/timestamps, never unhooks, replaces or reattaches a Hook.
Baseline/snapshot before installation are refused, not zero-software acceptance.
Missing installation, EOF or resource failure is visible; done still disposes
Raw receiver, target, Hook window/thread/counter. Console waits/prints are off
the message threads. C's existing --hook-observer entry is unchanged.

Both B and C must be PREPARED BEFORE EITHER Hook install. A's existing Hooks are
installed first. The same B/Raw/C readiness protocol and process order apply to
both conditions. Only the two successful one-shot Hook install commands change
order; QPC bounds must not overlap. No reinstallation within a session, no input
ignore windows or source pause. A/B/C still share one Windows Hook mechanism.

## Fresh B→C / C→B / B→C sessions

Build managed diagnostics once:

```powershell
dotnet build F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -warnaserror
```

For EACH of three rounds close all old probes and use fresh A/B/C. Keep elevation,
external programs and native hashes fixed. Start A FIRST, wait at HWND/PID prompt:

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode ManagedTarget
```

B SECOND (manual option replaces --startup-order raw-first):

```powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --hook-isolation-stage keyboard-native-callback --hook-module callback --manual-hook-install
```

Wait for MANUAL HOOK COMMANDS and MANUAL HOOK PREPARED, actual keyboard-owned
registration count=1/mousePresent=False, no faults and hookInstalled=False.
Start C THIRD, wait for OBSERVER PREPARED/COMMANDS, hookInstalled=False:

```powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --hook-observer
```

Do NOT install B before C PREPARED, in any round. Installation sequence:

| Round | First command (await INSTALLED) | Second command (await INSTALLED) | Required clock relation |
|---|---|---|---|
| 1 | B: install | C: install | B Hook afterTicks < C Hook beforeTicks |
| 2 | C: install | B: install | C Hook afterTicks < B Hook beforeTicks |
| 3 | B: install | C: install | B Hook afterTicks < C Hook beforeTicks |

Both installations must be later than B PREPARED and C PREPARED. Every INSTALLED
must show Hook installed, correct callback DLL/module/thread, no mismatch/fault.
If a phase fails, end/clean up that round; do not start A AUTO with partial setup.

Then copy B's target HWND/PID into A. Perform HUMAN BEFORE in B and another app,
confirm ordinary keyboard/mouse/shortcuts; B baseline, C baseline. A next; activate
B's white window during its five-second countdown, release keys/buttons and remain
hands off for all four rounds. BEFORE HUMAN AFTER: B snapshot, C snapshot. Then
HUMAN AFTER; A done; B snapshot; C snapshot; B done; C done.

Return NINE complete logs, grouped by round/order, plus normal-operation and
accidental-input confirmation. A exact AUTO counters assess interference; B/C
broad intervals include terminal commands and need not have unflagged=0.
Native returns, specific target down/up, each Hook's tagged/actionable/intersection
counts, install timestamps, lifecycle and clean registration ownership are all
required. Raw software/candidate classification is NOT COLLECTED in these rounds.

## Predeclared interpretation

| Outcome, with complete sends/target and valid phase identities | Interpretation |
|---|---|
| B→C complete; C→B missing; B→C complete | Reverse-confirmed correlation with final Hook installation context; still not a concrete root cause |
| Both orders complete | Last-install order is insufficient; C's installed Hook presence remains the stronger observed distinction |
| Both orders missing | New all-prepared baseline failed to reproduce C recovery; audit this changed preparation protocol/timing before order claims |
| Only some observers recover | Compare observer-specific lifecycle/chain evidence; do not call all observers recovered |
| Invalid clocks/identity/hash, partial send/target, phase fault or AUTO interference | Inconclusive, fix experiment validity before interpreting order |

Do not infer production Raw failure from missing Hook callbacks or enable
automatic takeover from this reduced control. Preserve the original failure.

## Automated scope

[ManualHookInstallTests](../Rug.UI.Core.Script.Tests/ManualHookInstallTests.cs#class:ManualHookInstallTests)
starts actual B and C console subprocesses for B-C/C-B/B-C, verifies both are
prepared without Hooks before installation, real owned B Raw registration,
nonoverlapping cross-process QPC, correct one-shot timestamps, pre-install refusal,
unchanged Raw HWND/thread after install and native/window/thread cleanup. Three
incomplete B sessions (uninstalled done/EOF, installed EOF) remain NOT RUN and
clean up; four invalid CLI combinations reject before loading Core.

Run --manual-hook-tests or --raw-input-tests/full Lua suite. These checks emit
no physical input and do not replace HUMAN/AUTO or demonstrate software Hook
recovery. Manual-order input is **NOT RUN by the development agent**; the user's
nine-log comparison is recorded above and does not close original acceptance.
