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
* `Views/MainPage` loads the copied `ui.json` example at runtime. A plugin can supply its own file to `DynamicUIHost.LoadFileAsync` without recompiling. The example exercises every card and both selector tabs. Button and preset actions require the host to subscribe to their named commands; a missing handler is reported in the error banner.
* `Rug.UI` targets Windows SDK 22621 and pins Windows App SDK 1.5 plus SettingsControls 8.1 because `SelectorBar` first appears in 1.5 and this toolkit build requires the 22621 target framework.
