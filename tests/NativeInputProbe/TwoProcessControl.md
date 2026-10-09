---
id: rug_native_two_process_control
type: logic_node
inputs: [rug_input_sink_control, rug_native_input_probe]
outputs: []
tags: [diagnostics, hooks, raw-input, two-process]
---

# Pure native A+B reduction

## Reviewed INPUTSINK reversal (2026-10-09)

Nine complete fresh A/B/C logs kept B target/receiver, B Core OFF, keyboard
registration ON, DEVNOTIFY OFF, startup A-B-C and Hook order C-B. Actual
registration flags were 256/0/256, all succeeded=1/error=0. Both PREPARED preceded
C installation, C completed before B began, baselines preceded emission and
first snapshots followed all eight successful native send calls.

| INPUTSINK | A / B / C PID | A AUTO keyboard entries; A/B/C tagged keyboard | Raw input/cleanup | Human keyboard before/after |
|---|---|---|---|---|
| On | 14364 / 46696 / 55128 | 0; 0/0/0 | 352/352 | 115/64 |
| Off | 48396 / 46596 / 61336 | 0; 0/0/0 | 94/94 | 137/58 |
| On | 46668 / 62744 / 22148 | 0; 0/0/0 | 358/358 | 126/81 |

All four specific F8/B target checks per round had down/up=1/1. B per-key totals
rose from 0/0 to 2/2. Mouse AUTO observed ten tagged injected events per round;
no unflagged AUTO activity was observed. Device changes were zero. Hook/receiver
hashes matched, no reported parse/ownership/thread faults, all threads exited,
final B/C registrations zero. A reports Core path/size, not a loaded SHA256.

INPUTSINK is not necessary in this topology: keyboard registration with neither
INPUTSINK nor DEVNOTIFY still reproduces. This is not proof of a Raw API defect.
Raw software/candidate classification was NOT COLLECTED. Human callback counts
do not certify ordinary shortcuts/window behavior. Original integrated failure
and automatic takeover acceptance remain open.

## Fixed experiment contract

Remove C entirely, retaining A sender/observers and B target/count-only receiver/
keyboard Hook. B Core OFF, keyboard registration ON, DEVNOTIFY OFF, INPUTSINK OFF
(actual flags=0). Start A before B; install B Hook only after B PREPARED/COMMANDS.
No CLR, receiver parsing, software filters, retries, ignore windows or takeover.

This reduces process and Hook-chain topology; it is a structural comparison,
not a strict single-variable causal proof. A and B Hooks still share Windows
low-level Hook infrastructure. The retained B console hint says "AFTER both B/C
PREPARED" for historical ABC runs; here wait only B PREPARED and do NOT start C.
[RunRole](NativeRoles.cpp#function:RunRole) has no C dependency in its install gate.

No native source/build or product change is needed. Retain these five binaries
without rebuilding between sessions:

| x64/Debug file | SHA256 |
|---|---|
| Rug.Core.dll | 5E0C9B3F75A49DBA002436B2E438A440E493EEB01B4263E08B787D4FE475A405 |
| Rug.NativeInputProbe.exe | E323A5F1F2FD784D2BDEEE35E03CF5D50944A37DAC2775D907182AF1EAF27A06 |
| Rug.NativeKeyboardHook.dll | 50A62A5DAC1BF6C68856CEAE12B7CB2208F41C1B6A27FE2386146F35185B7794 |
| Rug.NativeRawReceiver.dll | 40D9739DC6E50816CB68D4EEEFEED90F2E121793192EB32646238825CAE084EB |
| Rug.NativeRoleProbe.exe | 428D56C1F1C7488FCA6818CF6E352646641ED20AF1BF03BAB9D056F1724DB75E |

## Automated validity check

```powershell
& F:/Cardinal/Rug/Rug/tests/run_native_two_process_tests.ps1
```

The runner starts only A and B, never sends next/AUTO, verifies three fresh
sessions: flags-zero successful registration, loaded diagnostic module hashes,
Core/CLR absence in B, pre-install refusals, one-shot install clocks/module
association, stable target/receiver identity and registration, real A binding,
EOF-before-AUTO rejection and full cleanup. Three incomplete B cases also verify
done/EOF cleanup without C. It bounds output/waits and disposes only its own
children. It verifies five file hashes unchanged before/after; this is not a
certificate of loaded Core during software emission (which is NOT RUN).

On 2026-10-09 those six lifecycle cases passed. The existing fixed-flags ABC
contract also passed. No native rebuild: all five binaries unchanged. Physical
AUTO/HUMAN and Hook observation are NOT RUN by the agent; Raw classification
NOT COLLECTED. WinUI/product-wide regressions were not rerun for this script/doc
change; it does not change any production input or event-source behavior.

## Manual session: return TWO full logs

Close all old A/B/C probes, keep external applications/elevation unchanged and
do not run other Rug input tasks. First run one fresh A+B session, no C.

Terminal A (wait for MANAGED TARGET prompt):

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeInputProbe.exe --managed-target
```

Terminal B:

```powershell
& F:/Cardinal/Rug/Rug/x64/Debug/Rug.NativeRoleProbe.exe --role-b --raw-owner B --target-owner B --core-load off --raw-registration on --device-notify off --input-sink off
```

1. B PREPARED/COMMANDS must show target=1, receiverReady=1, rawRegistrations=1,
   coreLoaded=0/clrLoaded=0, and RAW_REGISTER_CALL requestedFlags=0,
   succeeded=1/error=0 with nonzero thread/HWND/timestamps. In B type `install`,
   wait INSTALLED. No C preparation or install command is required.
2. Paste B's NATIVE TARGET `0xHWND PID` into A. Perform HUMAN BEFORE in B's
   white target and another application: keyboard, mouse and normal shortcuts.
3. B `baseline`, then A `next`. During FIVE-second countdown focus B's white
   target by its title bar, release keys/buttons and keep hands off throughout
   AUTO. Do not manually press B/F8 during AUTO.
4. At A's HUMAN AFTER prompt, B `snapshot` BEFORE further human checks. Then
   perform HUMAN AFTER, return to A and type `done`. B `snapshot`, then `done`.
5. Return both complete logs (A then B), including modules, registration,
   install clocks, native SEND_CALL, each F8/B target/Hook delta and cleanup.
   Report accidental AUTO input and whether ordinary operation remained normal.

## Predeclared branches

| Valid sends, specific targets, identities/hashes/focus/lifecycle | Interpretation / next step |
|---|---|
| Gap persists in A+B | C is not necessary; package the native two-process reproduction and audit target/receiver/window-thread relationship before further changes |
| Both observers regain complete tagged F8/B | C/process/Hook-chain topology participates in the ABC condition; restore fixed-flags ABC as a fresh control before drawing conclusions |
| Only one observer regains evidence | Preserve A/B separately; no claim of independent sensor proof or original failure closure |
| Send/target/focus/hash/registration/install fault or AUTO interference | Inconclusive: repair experiment validity before interpretation |

Keep insertion, specific target processing, Hook entry/classification and Raw
counts separate. Count-only Raw supplies no software/candidate attribution.
Never combine different sessions into a complete observation. A reduced pass
does not accept the original integrated source or enable automatic takeover.
