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
> Task 3.1.1 registers a singleton `StateStore`/`ICommandBus`; the WinUI
> `DispatcherQueue` delivers background state notifications on the UI thread.

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
* `Views/DynamicUIHost` accepts JSON text or a file plus the shared `ICommandBus`. The factory initializes missing keys from schema defaults, forwards user edits through the bus, applies bus notifications to controls on the UI dispatcher and detaches event subscriptions when the page unloads. Command failures surface in the host `InfoBar`.
* `MainWindow` now hosts the plugin navigation and `DynamicUIHost`; the older `MainPage` remains a legacy page. A plugin supplies `ui.json` to `DynamicUIHost.LoadFileAsync` without recompiling. Command failures appear in the error banner.
* `Rug.UI` targets Windows SDK 22621 and pins Windows App SDK 1.5 plus SettingsControls 8.1 because `SelectorBar` first appears in 1.5 and this toolkit build requires the 22621 target framework.

## Plugin hot reload (Task 3.3)

* The app watches `%LOCALAPPDATA%/Rug/plugins/` by default; set `RUG_PLUGINS_ROOT` to use an isolated directory. `MainWindow` lists scanned plugins and attaches the selected `DynamicUIHost` to its plugin ID. Changing JSON or Lua rebuilds the card tree on the WinUI dispatcher while the app stays open. The old factory unsubscribes from `CommandBus` before its controls are removed; shared state values persist across reloads.
* The host displays a success `InfoBar` for two seconds, or a persistent error with the JSON/Lua exception stack. A failed parse keeps the last good UI in place; correcting the file triggers another reload. UI and script changes are validated before new instances start.

## Phase 3 MVP integration (Task 3.4)

* `MainWindow` owns the plugin sidebar, selected dynamic host, hot-reload status and a footer entry for DevTools. The bundled `plugins/demo_arknights` is copied into the app output and seeded into the default local plugin directory only when absent; an explicit `RUG_PLUGINS_ROOT` is never modified.
* `DemoWorkflowCoordinator` subscribes to `demo.start_selected`, starts an isolated Lua task, and turns `rug.set_state("demo.step_event", json)` updates into `SchemaStep[]` at `demo.steps`. `StepFlowView` reads the current state when rebuilt, preserving the timeline on hot reload. The demo uses only `timer` permission and simulates wake/OCR steps without controlling an external app.
