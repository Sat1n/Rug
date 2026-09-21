---
id: rug_ui
type: logic_node
inputs: [rug_core, rug_ui_core]
outputs: []
tags: [host, winui, ui]
---

# Rug.UI Index (L2)

C# WinUI 3 desktop shell: views, view-models, navigation/activation, theming and the
single native interop boundary. It renders the dynamic UI and forwards user intent to
host logic; it owns no automation pipeline itself.

> **Status:** WinUI 3 template scaffold (activation, services, VMs, views). The
> `Native/NativeMethods.cs` P/Invoke layer is **not yet created**.

## Internal Topology

| Path | Responsibility |
|---|---|
| `Views/` | XAML pages (Main, Settings, Shell) |
| `ViewModels/` | MVVM view-models (CommunityToolkit.Mvvm) |
| `Services/` | Activation, navigation, theming, local settings |
| `Native/NativeMethods.cs` | **Only** place allowed to declare P/Invoke imports (planned) |

## Interop Restriction (CRITICAL)

* All native imports must reside strictly inside `Native/NativeMethods.cs`.
  Direct P/Invoke in ViewModels or Services is forbidden.
* Managed code must never free native pointers — release native buffers only via the
  core's `Rug_FreeBuffer` (see [Rug.Core L2](../Rug.Core/README.md)).

## Coding Standards (C# / WinUI 3)

* Use `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`).
* Never block the UI thread; run native operations asynchronously.

## Constraints

* Depends downward only on `Rug.Core` (via P/Invoke) and `Rug.UI.Core` (L1 §5.1).
