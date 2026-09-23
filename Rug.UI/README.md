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

## Internal Topology

| Path | Responsibility |
|---|---|
| `Views/` | XAML pages (Main, DevTools, Settings, Shell) |
| `Views/DevToolsPage.xaml(.cs)` | Dev sandbox UI: crosshair window picker, WriteableBitmap live preview + OCR box overlay, input-test buttons, a client-space coordinate picker for scripting, colored console log |
| `ViewModels/` | MVVM view-models (CommunityToolkit.Mvvm) |
| `ViewModels/DevToolsViewModel.cs` | Orchestrates window binding, preview loop, input self-tests (client-space, confined to the bound window), coordinate picker, stress run; emits `Logged`/`FrameReady` events |
| `Helpers/LogLevelToBrushConverter.cs` | `LogLevel` → console foreground brush |
| `Models/DevTools.cs` | `LogLevel` / `LogEntry` / `PreviewFrame` UI-layer records |
| `Services/` | Activation, navigation, theming, local settings |
| (consumes) `Rug.UI.Core` | `IOcrService` / `ITemplateMatchService` / `IInputService` / `ICaptureService` / `IWindowSpyService` — no direct P/Invoke here |

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
