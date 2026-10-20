---
id: rug_ui
type: logic_node
inputs: [rug_core, rug_ui_core]
outputs: []
tags: [host, winui, ui]
---

# Rug.UI Index (L2)

C# WinUI 3 desktop shell: views, view-models, navigation/activation and theming. It
renders the dynamic UI and forwards user intent to `Rug.UI.Core` host logic; it owns
no automation pipeline and no native interop itself.

> **Status:** WinUI 3 template scaffold (activation, services, VMs, views) plus the
> **Dev Tools sandbox** (Task 1.6): `Views/DevToolsPage.xaml(.cs)` +
> `ViewModels/DevToolsViewModel.cs` visualize WGC capture, OCR overlay, the Spy++-style
> crosshair window picker, input self-tests and a 100x stress run. Native interop is
> **not** done here — the app consumes `Rug.UI.Core` services.
> Task 3.1.1 registers a singleton `StateStore`/`ICommandBus`; Task 3.5-02
> structurally partitions its data by application, plugin and run. The WinUI
> `DispatcherQueue` delivers scoped background notifications on the UI thread.

## Internal Topology

| Path | Responsibility |
|---|---|
| `Views/` | XAML pages (Main, DevTools, Settings, Shell) |
| `Views/DevToolsPage.xaml(.cs)` | Dev sandbox UI: crosshair window picker (HWND/title/process/size/DPI/**monitor**), WriteableBitmap live preview + OCR box overlay, input-test buttons, a client-space coordinate picker for scripting, colored console log |
| `ViewModels/` | MVVM view-models (CommunityToolkit.Mvvm) |
| `ViewModels/DevToolsViewModel.cs` | Orchestrates window binding, preview loop, input self-tests (client-space, confined to the bound window), coordinate picker (physical + logical DIP), stress run; emits `Logged`/`FrameReady` events |
| `Helpers/LogLevelToBrushConverter.cs` | `LogLevel` → console foreground brush |
| `Models/DevTools.cs` | `LogLevel` / `LogEntry` / `PreviewFrame` UI-layer records |
| `Services/` | Activation, navigation, theming, local settings |
| (consumes) `Rug.UI.Core` | `IOcrService` / `ITemplateMatchService` / `IInputService` / `ICaptureService` / `IWindowSpyService` plus shared `StateStore`/`ICommandBus` — no direct P/Invoke here |

## Interop Restriction (CRITICAL)

* **No direct P/Invoke in `Rug.UI`.** The single native boundary lives in
  [Rug.UI.Core](../Rug.UI.Core/README.md) (`Native/Rug.Core.Native.cs` for Rug.Core,
  `Native/WindowNative.cs` for user32 window inspection); the app consumes the
  `I*Service` contracts. P/Invoke in Views, ViewModels or app Services is forbidden.
* Native memory safety is handled inside `Rug.UI.Core` (SafeHandle + `Rug_FreeBuffer`);
  UI code never touches native pointers. The crosshair drag only handles XAML pointer
  events and calls `IWindowSpyService`.
* Native operations run on `Rug.UI.Core` background threads; the DevTools view marshals
  frame/log events onto the UI thread via `DispatcherQueue`.

## Coding Standards (C# / WinUI 3)

* Use `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`).
* Never block the UI thread; run native operations asynchronously.

## Constraints

* Depends downward only on `Rug.UI.Core` (which wraps `Rug.Core`); `Rug.UI` does not
  P/Invoke the native DLL directly (L1 §5.1).
* **Multi-monitor / DPI:** `app.manifest` declares `PerMonitorV2`, so the whole
  pipeline (user32, WGC, native input) is consistently in PHYSICAL pixels — no
  logical↔physical conversion is applied on the capture/input path. The live-preview
  OCR overlay stays aligned at any DPI because the `Image` and the overlay `Canvas`
  share one frame-pixel coordinate space inside a `Viewbox` (scaled together), so no
  manual ratio matrix is needed. `DpiHelper` (in Rug.UI.Core) converts only for
  script-facing DIP output.

## Dynamic UI renderer (Task 3.2)

* `Services/UIFactory.cs` maps validated `UISchemaNode` trees to WinUI 3 controls. Settings cards use Windows Community Toolkit `SettingsCard`; navigation uses the native `SelectorBar`. `Controls/TaskSelectorCard` provides selection, ordering, select-all, clear and command dispatch; `Controls/StepFlowView` renders status and optional app-owned thumbnail URIs. `InfoBanner` maps to `InfoBar`.
* `Views/DynamicUIHost` accepts JSON text or a file plus `ICommandBus`. The host calls `SetActiveInstance(RunIdentity?, displayFallbacks)` for any plugin; the binding survives card rebuilds, and null releases the live instance subscription. Settings controls read/write the selected plugin's `PluginConfig` scope; each `StepFlowView` reads and subscribes to its own schema `stateKey` in the active run's `InstanceState`. Instance changes and card rebuilds reread that scope, with schema steps as defaults. The factory applies exact-scope/key UI notifications on the dispatcher and detaches subscriptions when replaced or unloaded. Command failures surface in the host `InfoBar`.
* `MainWindow` hosts plugin navigation, `DynamicUIHost` and the active-instance picker. It follows scheduler changes, clears stopped runs and rebinds after hot reload. Schema commands `host.start_windowless`/`host.stop_selected` keep their existing Gateway route. The host's **运行绑定窗口任务** button starts the selected plugin against the trusted DevTools crosshair binding, with explicit consent; schema parameters cannot supply HWNDs. The older `MainPage` remains a legacy page.
* [LifecyclePresentation](Helpers/LifecyclePresentation.cs#class:LifecyclePresentation) gives Stopping/Faulted/Stopped precedence over
  retained recovery phases. Only an active Recovering snapshot shows “正在验证”.
  Recovery registry/coordinator are each registered once in the composition root.
* `Services/PluginAutomationCommands` binds verified plugin UI callers and routes
  start/stop/pause/resume through the scheduler's Automation Gateway. Schema
  parameters remain launch data, never identity/permission grants or HWNDs.
  Foreground starts use host-owned `PluginPermissionConsent` to display and
  explicitly approve the requested permissions first. Deny/close never starts.
  The main-window Plugin permissions button displays grants and revokes them.
  Demo start and automatic completion-stop use that same path. The host also
  registers `host.pause_selected`/`host.resume_selected` for schema commands.
  See the [Gateway contract](../Rug.UI.Core/AutomationGateway.md) for deadlines,
  cancellation, bounded deduplication and sanitized outcomes, and the
  [authorization contract](../Rug.UI.Core/Authorization.md) for process-local
  installation-bound grants, startup/revocation ordering and background reload.
* `Rug.UI` targets Windows SDK 22621 and pins Windows App SDK 1.5 plus SettingsControls 8.1 because `SelectorBar` first appears in 1.5 and this toolkit build requires the 22621 target framework.

## .NET 10 x64 build and verification (Task 3.5-01)

* The app targets `net10.0-windows10.0.22621.0` with the existing
  `TargetPlatformMinVersion` of `10.0.17763.0`; root `global.json` pins SDK
  `10.0.402`. Existing NuGet versions need no migration-specific upgrades.
* Run `dotnet build Rug.UI/Rug.UI.csproj -c Debug -p:Platform=x64 -warnaserror`
  and repeat with `-c Release`. The x64 native DLLs from
  `x64/$(Configuration)/` are copied next to `Rug.UI.exe` under
  `Rug.UI/bin/x64/$(Configuration)/net10.0-windows10.0.22621.0/win-x64/`.
* Run the non-interactive UI schema suite with
  `dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64`.
  See [UI tests](../Rug.UI.Tests/README.md) for desktop smoke and Phase 3 E2E runs.

## Plugin hot reload (Task 3.3)

* The app watches `%LOCALAPPDATA%/Rug/plugins/` by default; RUG_PLUGINS_ROOT isolates tests. MainWindow reads the validated effective snapshot catalog. [07A updates](../Rug.UI.Core/HotReload.md) stage UI on the dispatcher and retain old trees/subscriptions on build failure. UI-only replacement rebinds the same InstanceState and preserves Lua identity/pause without restarting tasks.
* Applied updates show a two-second success notice. Runtime/config/permission changes with active runs show 更新待应用; invalid changes show a persistent error while old UI/tasks remain. Unchanged content clears obsolete errors without reconstruction. UI defaults are staged until successful replacement; later controls sharing a missing key read and type-check its first staged value, so incompatible bindings fail before replacing the old UI or publishing defaults. Old subscriptions are released only after success. Ready precedes shell activation. Active-run code switching remains deferred.

## Shared input and host takeover (Task 3.5-05A)

The [07B-01 footer](../Rug.UI.Core/RuntimeUpdates.md) offers explicit runtime-update
preparation and cancellation through trusted Gateway callers, per-instance node
progress and **已冻结，尚未切换**. Cancelling retains the original Lua/run and
any user Pause. Preparation never grants permissions or publishes runtime code.

* `App` registers one `IInputArbiter` for scheduler/Lua and singleton DevTools.
  It also owns the checkpoint store/coordinator and exact embedded-code sample
  node registry. Explicit permission consent describes `checkpoint`; the footer adds
  [checkpoint restore UI](../Rug.UI.Core/CheckpointStartup.md). Selection, explicit
  confirmation and preparation cancellation use the Gateway; no automatic grants.
  The checkpoint selector is disabled throughout confirmation/preparation and is
  re-enabled after completion/cancellation; late query results cannot replace it.
  DevTools mouse, drag, key and stress tests hold a compound-action lease and
  validate it before native input; target rebinding takes place inside the lease.
  It uses a trusted host owner independent of plugin identities.
* `MainWindow` displays Manual takeover / Return control and current status.
  Takeover closes input immediately, cancels input runs/queues, and shows manual
  control only after actual native input and required releases settle. Timer and
  vision-only runs continue. Cleanup failure stays blocked; return cannot bypass
  cleanup, change grants or restart old instances. UI notifications reread current
  revision/state on the dispatcher, and handlers detach on window close.
* See [input arbitration](../Rug.UI.Core/InputArbitration.md) for window lifetime,
  mutual exclusion, conflict FIFO, noncooperative native calls and fault behavior.
  Verify real UI state transitions with the UI suite `--takeover-ui`.
* Task 3.5-05B-02 starts one production `IInputEventSource` and
  `IInputInterventionCoordinator` before app activation. Input runs and DevTools
  tests register whole input sessions; timer/vision/trajectory-only work does not.
  Candidate external downs, wheel or effective movement request existing global
  takeover, including between input actions and in other windows. Software tags,
  repeat/held keys, trailing releases and old observations do not trigger it.
* The footer shows detector readiness, monitoring count, trigger reason and
  separate detector/cleanup faults, plus the global rule. Return cannot bypass an
  unhealthy detector or revive stopped runs. Window close awaits watcher shutdown,
  input/source/scheduler cleanup before final close. 05B-02 human-triggered UI checks
  are user-confirmed passed; reproduce with `--intervention-manual` using the
  [isolated acceptance steps](../Rug.UI.Core/InputIntervention.md). No diagnostic
  Hooks, raw human contents, new grant behavior or automatic restart is introduced.

## Phase 3 MVP integration (Task 3.4)

Task 3.5-06A adds host footer Pause/Resume/Stop and selected-run lifecycle labels:
“正在暂停”, “已暂停”, “正在恢复”, “正在停止”. Pause success means request accepted;
only the snapshot confirms pause. Buttons follow live status; selection/reload and
coalesced dispatcher notifications re-query current identity, not stale event
values. Human observation stays active, including physical Pause clicks on input
runs. See [lifecycle contract](../Rug.UI.Core/Lifecycle.md) and `--lifecycle-ui`.
New real-human 06A checks remain NOT RUN by the agent.

* `MainWindow` owns the plugin sidebar, selected dynamic host, hot-reload status and a footer entry for DevTools. The bundled `plugins/demo_arknights` is copied into the app output and seeded into the default local plugin directory only when absent; an explicit `RUG_PLUGINS_ROOT` is never modified.
* `DemoWorkflowCoordinator` subscribes to `demo.start_selected` and passes the selected task IDs as that run's launch input. Its per-instance state subscription converts `rug.set_state("demo.step_event", json)` into `SchemaStep[]` at the same instance's `demo.steps`; the subscription is released on stop. `MainWindow` supplies a key-specific detached display copy after completion so a card rebuild can still show the timeline, while the stopped InstanceState is evicted. The generic `DynamicUIHost` and `UIFactory` have no demo-specific state key. The demo uses only `timer` permission and simulates wake/OCR steps without controlling an external app.

Task 3.5-06B-01 injects recovery coordinator and exact installation/scenario
validator registry. Production has no executor; arbitrary plugins have no default
validator. Only the trusted `recovery_probe` controlled green-window validator is
registered at the composition root. Footer shows recovery/waiting/verification,
reason, Verify recovery and Stop; Pause/Resume reject recovery. Verify sends a
Gateway control with the selected full identity/recoveryId; it never calls Runtime
Resume. Selection/queued updates re-query current revision. The DevTools crosshair
has an accessibility name for isolated UI automation. See [recovery/manual contract](../Rug.UI.Core/Recovery.md).
