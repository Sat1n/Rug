---
id: rug_exit_evidence
type: logic_node
inputs: [rug_ui, rug_ui_core]
outputs: []
tags: [shutdown, verification]
---

# EXIT-01 verification — 2026-10-22

Starting revision: `95149f35caa858a52e4754b91f611d1b37e7b11b`.
Exact SDK 10.0.402; loaded coreclr 10.0.13; x64 Windows desktop. Final files were
rebuilt before the accepted runs. No Hook binaries, Native dependencies or SDK
pin were changed. No process memory, dump, PDB or copied binary is committed.
Local full dump remains under ignored artifacts/host-exit/resolved-stack-debug.

## Hypothesis-driven diagnosis (Debug empty host)

Each row is a fresh-process contrast against the failing empty-host baseline,
not a cumulative set of component disablements. Production contains no diagnostic
notification/theme disable switches.

| Hypothesis | Only changed condition | Prediction if sufficient | Actual / conclusion |
| --- | --- | --- | --- |
| Notification registration causes assertion | omit registration/show | exit 0 | still C0000602; not sufficient |
| UISettings event causes assertion | omit ColorValuesChanged subscription | exit 0 | still C0000602; not sufficient |
| MainWindow.Close path causes assertion | use Application.Exit | exit 0 | still C0000602; not sufficient |
| Live root XAML references cause assertion | clear root/resources before Close | exit 0 | still C0000602; not sufficient |
| Old native WinUI static-event teardown enters CLR too late | only WindowsAppSDK 1.5 -> first stable 1.6 (same runtime) | exit 0 | exit 0; stack/source evidence supports dependency defect |

[Baseline](baseline-debug.json) and same-named contrast JSON/stderr retain failures.
[sdk16-clean-debug.json](sdk16-clean-debug.json) is the dependency-only successful
contrast, before the subsequent host-owned lifecycle cleanup improvements.

[native-stack.txt](native-stack.txt) records the failing native thread. Relevant
Microsoft.UI.Xaml.Controls.dll is 3.1.5.2403; matching public PDB GUID
34978404-8cb2-aa79-d1ef-bd73a07e51ef, age 2. Weak-ref get is resolved +0x44;
following event_revoker symbol has +0x115A1A displacement, so this is not an exact
source-line stack. CRT execute_onexit_table / DLL detach precedes CLR fail-fast.
Public source before/after and limitations are in [Shutdown.md](../../../Rug.UI/Shutdown.md).

Historical Release failures remain in [08C-01](../../../samples/controlled-visual/evidence/release-report.json)
and [08C-02](../../../samples/controlled-visual/evidence/recovery-release-report.json).
No historical failure report was rewritten.

## Final acceptance matrix

All following commands used final rebuilt products. The CLI aggregate exit codes
were 0; timeout kills were not used. Stderr for actual UI children is empty; no CLR
assertion. Reports keep actual IDs, module versions and actual cleanup state.

| Scenario | Debug | Release | Evidence |
| --- | --- | --- | --- |
| Empty plugins, no tasks/viewer | 5/5 natural exit 0 | 5/5 natural exit 0 | Debug-empty.json / Release-empty.json; per-round stdout/stderr |
| Viewer open/close + repeated main close | exit 0 | exit 0 | Debug-viewer.json / Release-viewer.json |
| Real DevTools preview while closing | exit 0; no late input, 0/0 | same | Debug-matrix.json / Release-matrix.json |
| Active observed PostMessage task close | exit 0; 1/1 retained, no later action | same | matrix; cleanup true, actual work 0, offline Valid |
| AwaitingHuman close | exit 0; 1/1, no resume/AFTER | same | matrix; cleanup true, actual work 0, offline Valid |
| Complete 08C-01 | 8 scenes + viewer, exit 0 | same | Debug-08c01.json / Release-08c01.json |
| Complete 08C-02 | 8 scenes + real UI, exit 0 | same | Debug-08c02.json / Release-08c02.json |
| Duplicate close / cleanup failure | deterministic PASS | same | UI tests: one shared completion, later/sibling cleanup still runs, failure visible |
| Held preview start/capture | deterministic PASS | same | UI tests: actual VM source, no early stop/dispose or late FrameReady |
| Entered/queued notification callback | deterministic PASS | same | UI tests: actual service with WinRT doubles, paired registration/removal, active dialog drain |
| Host admission before cancellation propagation | deterministic PASS | same | full: two actual Lua instances, blocked cancellation callback, queued/new Gateway input 0, repeat dispose pending |
| Full script regression | command 0 | command 0 | Debug-full.txt / Release-full.txt |
| UI core Schema/state/hot reload and shutdown | command 0 | command 0 | Debug-ui-tests.txt / Release-ui-tests.txt |
| Related managed/WinUI builds | 0 warnings/errors | same | six *-build.txt files; Core also rebuilt as project dependency |

08C-01 stable target receives 1/1 and fresh image confirms completed. Rejection
cases have 0/0; scene/geometry/age/Stop/revoke reasons remain specific in reports.
08C-02 success receives BEFORE 1/1 and AFTER 1/1, same Lua local/state retained;
Stop/revoke/late cases have BEFORE 1/1 and AFTER 0/0. Actual WinUI flow ends at
2/2. PNG/JSON incident IDs and trace links survive recovery update. Traces read
Valid with actual work 0 and cleanup recorded. Viewer read-only interaction is
separate from those functional/native facts.

The deterministic callback tests use doubles; real hosts still register/show
notifications and keep theme updates enabled. They do not claim a new physical
toast activation test. UIA/bind setup is test setup, not business SendInput
acceptance. New human intervention / real business SendInput: NOT RUN (not needed
for this scoped shutdown change). Native core was not changed/rebuilt. No Hook
root-cause closure or Cancelled-to-ValidationTimedOut classification fix is claimed.

## Commands

See [repeatable commands and ownership contract](../../../Rug.UI/Shutdown.md).
The 08C-01/02 accepted runs used the equivalent `dotnet run ... --no-build --
--controlled-visual-sample` / `--controlled-recovery-sample` after explicit matching
builds, with output under artifacts/host-exit/final2-<configuration>-08c0x.
The harness preserves aggregate failure; successful assertions alone cannot hide
a future child nonzero exit. All logs here are bounded test output, not a second
production trace or a dump of plugin state/images.
