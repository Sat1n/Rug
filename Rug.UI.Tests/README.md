---
id: rug_ui_tests
type: logic_node
inputs: [rug_ui_core, rug_ui]
outputs: []
tags: [ui, tests]
---

# Rug.UI.Tests

07B-02 uses script harness `--runtime-switch-ui`: fixed embedded v1/v2 sample,
explicit switch dialog, actual fresh WGC before/after Stop, new-generation AFTER-only,
inherited Pause and visible red-scene rejection. `CheckpointRestoreUiSmoke.ps1`
adds Switch/SwitchPause/SwitchReject phases. `--runtime-switch-manual` provides
isolated user steps; automated UI results are not human acceptance. See
[switching and commands](../Rug.UI.Core/RuntimeSwitch.md).

07B-01 actual UI verification runs through the script harness `--runtime-update-ui`
after Debug/x64 app/test builds. `CheckpointRestoreUiSmoke.ps1 -Phase Update`
uses its fixture's green isolated target and temporary `update_probe`; it confirms
explicit consent, new-checkpoint real WGC freeze, same-generation cancellation,
and Stop while frozen. Actual Lua logs assert one INIT/STOP and no held AFTER
after Stop. See [preparation contract](../Rug.UI.Core/RuntimeUpdates.md).

Run `dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64` to validate the shipped `ui.json`, control-tree parsing, invalid schemas, and CommandBus state round trips. The suite targets `net10.0` under SDK `10.0.402`; the native WinUI renderer is compiled through `Rug.UI/Rug.UI.csproj` and can be viewed on the app's main page.

The hot-reload tests use real FileSystemWatcher events and validated snapshots: five rapid saves produce one application, inactive code changes publish without starting Lua, malformed JSON/Lua retain old UI/catalog, and corrections recover. The script harness --plugin-update-tests adds actual Lua and deterministic barriers for old-run preservation, UI-only pause/state retention, pending runtime/config/permissions, revocation, immutable startup source, rollback, ordering and capacity. See [07A contract](../Rug.UI.Core/HotReload.md).

Run `pwsh -File Rug.UI.Tests/HotReloadUiSmoke.ps1` from the repository root after building the Debug/x64 WinUI app and Debug/x64 UI test project. The script resolves their net10.0 output paths. The script creates a unique disposable plugin under the test output and passes it through `RUG_PLUGINS_ROOT`, starts the app, uses Windows UI Automation to find the rendered `BeforeReload` title, rewrites `ui.json`, waits for `AfterReload`, then verifies both JSON and Lua syntax errors appear in the `InfoBar` and recover after corrected saves. It records each elapsed time. It closes only the process it started and verifies the resolved temporary path before removing the test directory. It does not edit installed plugins.

Run `dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64 -- --e2e` after building the x64 app to run the Phase 3 UI Automation integration. `E2ETests.cs` launches `Phase3E2E.ps1`, which creates an isolated copy of the demo plugin, verifies sidebar discovery and UI rendering, selects a task and invokes the Lua command, waits for the completed step, edits `ui.json`, and checks that the UI reloads within one second while selection and timeline state persist. The test closes its own app process and deletes only its verified test directory.

Run with `--instance-ui` to launch `GenericInstanceUiSmoke.ps1`. Its non-demo
`timeline_probe` plugin supplies `probe.timeline` as launch input for two
concurrent windowless runs. UI Automation verifies actual `StepFlowView` text
when selecting each run, fallback after stop, timeline/pause preservation during
UI-only hot reload, safe actual UIFactory build failure, and subsequent new runs.
TextCard/ToggleCard defaults sharing a previously missing key must reject both
the initial update and its title-only retry, retaining the same UI tree and paused
run generation. A subsequent valid bool binding to that key proves failed staging
did not publish the string default into PluginConfig. The test
uses an isolated plugin directory and cleans it up.

Run with `--lifecycle-ui` for `LifecycleUiSmoke.ps1`: actual timer Pause/Resume
labels/buttons, two identities, a paused timer retained across takeover/return,
fresh snapshots after hot reload and disabled terminal controls. Consent is
explicit; isolated files/app are cleaned. It does not perform human input or
prove hardware native pause timing. See [06A contract/manual steps](../Rug.UI.Core/Lifecycle.md).

Since Task 3.5-02, selected tasks are per-run launch inputs. The Lua demo writes
only its InstanceState, and the displayed completed timeline is a detached UI
copy associated with that run; PluginConfig remains plugin-scoped in memory.

Task 3.5-04 UI automation explicitly invokes the real host permission dialog.
`--instance-ui` verifies denied Start produces no timeline, Allow starts, revoke
stops all runs, and regrant starts a new run; it waits for enabled navigation
before switching asynchronously created instances. `--e2e` explicitly approves
the demo before running and checks its completed timeline survives UI reload.
No environment variable auto-approves grants. Background reload never prompts.

Task 3.5-05A: run `dotnet run --no-build --project Rug.UI.Tests/Rug.UI.Tests.csproj
-c Debug -p:Platform=x64 -- --takeover-ui` after the x64 app/test build.
`ManualTakeoverUiSmoke.ps1` uses an isolated timer-only plugin, explicitly grants
its timer permission in the real host dialog, and invokes Manual takeover /
Return control three times. It verifies actual state labels, idempotent clicks
and timer-run preservation, then stops its run and cleans up its own app/files.
Task 3.5-05B-02 additionally checks actual production detector Ready/no-session
status and the global takeover rule before/after timer startup and each return.
It does not simulate human acceptance. Real human-triggered WinUI verification
is user-confirmed passed via the script harness `--intervention-manual`; see
[isolated target steps](../Rug.UI.Core/InputIntervention.md). The host UI owns
permission confirmation and target binding; no automatic approval/test bypass
is added. Existing generic-instance/demo/hot-reload regressions remain enabled.

06B-01 recovery UI automation is hosted by the script harness `--recovery-ui`
with an isolated target and temporary explicitly-authorized plugin.
`RecoveryUiSmoke.ps1` receives only the fixture-owned UI PID/HWND: it binds via
the actual DevTools crosshair before input session startup, invokes consent,
checks AwaitingHuman/ManualControl, selection re-query, no ordinary Resume,
Return without continuation, fresh WGC Verify and terminal controls. UIA plus a
trusted test panel change do not prove human recovery; use `--recovery-manual`.
Green verification must finish after one Verify invocation; the smoke test has no
retry-until-success loop. See [recovery acceptance](../Rug.UI.Core/Recovery.md).
No production approval switch.
06C-02 `--checkpoint-restore-ui` in the script harness runs
`CheckpointRestoreUiSmoke.ps1` against two separately started WinUI processes.
The fixture-owned target and store survive the restart. Actual consent/crosshair
binding, source stop, checkpoint selection, fresh WGC red rejection and green
on_restore are checked; B/C counters must each grow once. Human input remains
separate: the `--checkpoint-restore-manual` flow was subsequently user-confirmed
with save/restart/AFTER-only continuation and red rejection (see startup contract).
The automated fixture also creates a second host-owned checkpoint solely for
selection coverage. A controlled real consent await must disable the selector,
block choosing another record, and re-enable selection after Deny/cancellation.
No timing delay or production test/auto-approval switch is added.
The noninteractive suite compiles the actual LifecyclePresentation helper and
asserts that retained Validating snapshots cannot hide Stopping/Faulted/Stopped.
