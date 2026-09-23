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

> **Status:** WinUI 3 template scaffold (activation, services, VMs, views). Native
> interop is **not** done here — the app consumes `Rug.UI.Core` vision services
> (`IOcrService`, `ITemplateMatchService`).

## Internal Topology

| Path | Responsibility |
|---|---|
| `Views/` | XAML pages (Main, Settings, Shell) |
| `ViewModels/` | MVVM view-models (CommunityToolkit.Mvvm) |
| `Services/` | Activation, navigation, theming, local settings |
| (consumes) `Rug.UI.Core` | `IOcrService` / `ITemplateMatchService` — no direct P/Invoke here |

## Interop Restriction (CRITICAL)

* **No direct P/Invoke in `Rug.UI`.** The single native boundary lives in
  [Rug.UI.Core](../Rug.UI.Core/README.md) (`Native/Rug.Core.Native.cs`); the app
  consumes `IOcrService` / `ITemplateMatchService`. P/Invoke in Views, ViewModels or
  app Services is forbidden.
* Native memory safety is handled inside `Rug.UI.Core` (SafeHandle + `Rug_FreeBuffer`);
  UI code never touches native pointers.

## Coding Standards (C# / WinUI 3)

* Use `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`).
* Never block the UI thread; run native operations asynchronously.

## Constraints

* Depends downward only on `Rug.UI.Core` (which wraps `Rug.Core`); `Rug.UI` does not
  P/Invoke the native DLL directly (L1 §5.1).
