# Rug.UI.Tests

Run `dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64` to validate the shipped `ui.json`, control-tree parsing, invalid schemas, and CommandBus state round trips. The suite targets `net10.0` under SDK `10.0.401`; the native WinUI renderer is compiled through `Rug.UI/Rug.UI.csproj` and can be viewed on the app's main page.

The hot-reload tests create a temporary plugin under the test output and use real `FileSystemWatcher` events. They check five rapid saves coalesce into one callback, `ui.json` and Lua edits reload, malformed JSON/Lua produce failures, and corrected files recover. The script integration harness additionally verifies that an active Lua task runs `on_stop`, releases capture, starts a fresh instance on the same HWND, and recovers that instance after a bad schema is fixed.

Run `pwsh -File Rug.UI.Tests/HotReloadUiSmoke.ps1` from the repository root after building the Debug/x64 WinUI app and Debug/x64 UI test project. The script resolves their net10.0 output paths. The script creates a unique disposable plugin under the test output and passes it through `RUG_PLUGINS_ROOT`, starts the app, uses Windows UI Automation to find the rendered `BeforeReload` title, rewrites `ui.json`, waits for `AfterReload`, then verifies both JSON and Lua syntax errors appear in the `InfoBar` and recover after corrected saves. It records each elapsed time. It closes only the process it started and verifies the resolved temporary path before removing the test directory. It does not edit installed plugins.

Run `dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64 -- --e2e` after building the x64 app to run the Phase 3 UI Automation integration. `E2ETests.cs` launches `Phase3E2E.ps1`, which creates an isolated copy of the demo plugin, verifies sidebar discovery and UI rendering, selects a task and invokes the Lua command, waits for the completed step, edits `ui.json`, and checks that the UI reloads within one second while selection and timeline state persist. The test closes its own app process and deletes only its verified test directory.

Run with `--instance-ui` to launch `GenericInstanceUiSmoke.ps1`. Its non-demo
`timeline_probe` plugin supplies `probe.timeline` as launch input for two
concurrent windowless runs. UI Automation verifies actual `StepFlowView` text
when selecting each run, fallback after stop, stale data removal during an
active-run schema hot reload, and binding a new run after that reload. The test
uses an isolated plugin directory and cleans it up.

Since Task 3.5-02, selected tasks are per-run launch inputs. The Lua demo writes
only its InstanceState, and the displayed completed timeline is a detached UI
copy associated with that run; PluginConfig remains plugin-scoped in memory.
