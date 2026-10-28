---
id: rug_startup_order_control
type: logic_node
inputs: [rug_input_evidence_control, rug_script_tests]
outputs: []
tags: [diagnostics, hooks, raw_input, startup]
---

# Hook installation / Raw startup order

## Reviewed evidence (2026-10-08)

The user's three fresh-process original/callback/original hmod runs all retained
the keyboard Hook gap. Every SendInput F8/B down/up returned 1 and each specified
key reached B's target 1/1. A's per-software-round keyboard callbackTotal was 0;
B's software-tagged key counts stayed 0, despite human callbacks before/after.
Both PostMessage keys passed and all sessions cleaned up. The repaired middle
run used the actual callback DLL hmod while retaining the main window instance.
Thus module association repair is insufficient, not a proven root cause.

Reported Core SHA-256 remained
`5E0C9B3F75A49DBA002436B2E438A440E493EEB01B4263E08B787D4FE475A405`;
Raw DLL `95F89393E3F599B9FC03782899B3067AE119B91C731730FA310B83D63D979DF5`;
Hook DLL `50A62A5DAC1BF6C68856CEAE12B7CB2208F41C1B6A27FE2386146F35185B7794`.
This work changes none of those native binaries, input construction or C-ABI.

## Matched initialization phases

`--startup-order hook-first|raw-first` is accepted ONLY for
keyboard-native-callback with corrected `--hook-module callback`. Validation
precedes Core loading or window creation. Without this option existing modes
retain their immediate Hook installation.

[HookRawStartup](../Rug.UI.Core.Script.Tests/HookRawStartup.cs#class:HookRawStartup)
sequences the phases; [RawInputDiagnosticWindow](../../Rug.UI.Core/Native/RawInputDiagnosticWindow.cs#class:RawInputDiagnosticWindow)
owns the posted installation, readiness, timestamps and cleanup.

Both ordered conditions first create the same target and message-only Hook
windows/threads. The Hook thread is pumping with no Hook installed; window
readiness alone is not Hook readiness. A single posted command installs the
Hook on that same thread and acknowledges success asynchronously. Concurrent
installation requests share one completion, never replace/reinstall a Hook.
Dispose or thread failure rejects pending/old requests. Existing window instance,
callback DLL/hmod, message loop and cleanup remain the same.

| Condition | Awaited phase 1 | Awaited phase 2 |
|---|---|---|
| hook-first | Hook install completion | Raw receiver start through registration readiness |
| raw-first | Raw receiver start through registration readiness | Hook install completion |

The unchanged native Raw DLL combines its thread start, receive-window creation,
registration and readiness in StartAsync. The Raw phase therefore includes these
operations, not merely RegisterRawInputDevices. This experiment isolates the
order of TWO initialization phases; it cannot isolate that Win32 call from its
receiver startup. Target/Hook windows are prepared before either phase, but the
Raw receive window is created inside the Raw phase. Both use the same new posted
Hook installation protocol, so hook-first must independently reproduce the gap;
it is not automatically interchangeable with the older constructor-install run.

`STARTUP PHASE` reports ordinal, phase, before/after Stopwatch/QPC ticks,
frequency, success and exception type. Hook actual installation ticks are also
reported. Raw phase completion records the HOST observation of successful native
registration readiness, not an exact native RegisterRawInputDevices timestamp.
No output occurs between phases, in Hook callbacks or inside message handling.
Phase failure prevents phase 2; resource owners still perform normal cleanup.

## Fresh hook-first → raw-first → hook-first

Build the managed diagnostics ONCE; retain the existing native binaries:

```powershell
dotnet build F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -warnaserror
```

For each round close both prior processes. A FIRST:

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_input_probe.ps1 -Mode ManagedTarget
```

At A's HWND/PID prompt, B SECOND:

```powershell
dotnet run --project F:/Cardinal/Rug/Rug/tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --hook-isolation-stage keyboard-native-callback --hook-module callback --startup-order hook-first
```

Only change the FINAL order option: hook-first, raw-first, hook-first.
Keep elevation, external applications, A-first process startup, sender and B
target role fixed. Confirm STARTUP phase order/success, corrected hmod, all native
hashes, three distinct B threads and one keyboard-only owned Raw registration.
Copy B HWND/PID into A. HUMAN BEFORE on B/another app; B baseline; A next; focus
B's white target within five seconds, release keys/buttons and remain hands off
through AUTO. B snapshot; HUMAN AFTER; A done; B snapshot/done. Return SIX full
logs plus normal-input/shortcut confirmation and any accidental AUTO interaction.

Retain all three lanes: actual SEND_CALL/SEND_EVIDENCE, per-tested-key
TARGET_EVIDENCE and A/B Hook counters. The receiver remains count-only: Raw
software/candidate evidence is NOT COLLECTED. A/B share one Hook mechanism.
Pump heartbeats and old Raw logs cannot fill this session's evidence gap.

| Predeclared outcome | Interpretation |
|---|---|
| hook-first gap → raw-first recovery → hook-first gap; send/target complete | Two-phase initialization order correlates with recovery; reverse-confirmed, still not an API/root-cause finding |
| All three gaps; send/target complete | Ordering insufficient; next separate B's observer into process C while preserving B target/Raw role |
| New hook-first already recovers | Posted-install baseline changed the old condition; investigate that difference before interpreting raw-first |
| Only one observer changes | Compare its lifecycle/phase/chain evidence; do not call both recovered |
| Partial delivery, failed phase, incorrect identities/registration, hash mismatch or interference | Inconclusive; resolve that defect before interpreting observation changes |

Moving the observer into C, then a pure C++ role comparison if necessary, are
structural controls, not strict single-variable experiments. Message-loop
replacement needs a supporting hypothesis. Automatic takeover remains deferred.

The user's three ordered pairs now all reproduce the gap with complete native
send/target evidence and clean registrations. Phase clocks confirm the requested
order; no recovery occurred. See the next [external observer control](ExternalObserverControl.md)
for the reviewed counts, process-role contract, three-console steps and result branches.

## Automated boundary

Six real success lifecycles verify both orders, one installation, correct module
and thread binding, actual registration, nonoverlapping phases, Hook timestamps,
detached snapshots and cleanup. Four failure lifecycles use real registration
conflicts or a controlled pre-install thread fault, verify no second phase after
failure and preserve foreign registrations. Existing Lua/Gateway/grants/input
arbitration/Raw tests and Debug/x64 WinUI build pass. CLI order/reverse checks
emit no input. Paired HUMAN/AUTO is **NOT RUN by the agent**; no listener pause,
input-ignore interval, synthetic human acceptance or automatic reinstall exists.
