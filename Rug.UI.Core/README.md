---
id: rug_ui_core
type: logic_node
inputs: [rug_core]
outputs: [rug_ui]
tags: [host, logic, lua, plugins]
---

# Rug.UI.Core Index (L2)

Managed host logic between the native core and the UI: task scheduling, plugin
loading and the sandboxed Lua runtime, gated by a permission interceptor. It
contains no XAML and no native pipeline code.

> **Status:** minimal — `Services/FileService.cs`, `Helpers/Json.cs`,
> `Contracts/Services/IFileService.cs`. `TaskScheduler` / `PluginLoader` / Lua engine
> are **Phase 2 targets**, not yet implemented.

## Internal Topology

| Path | Responsibility |
|---|---|
| `Services/FileService.cs` | File IO abstraction |
| `Helpers/Json.cs` | JSON (de)serialization helpers |
| `Contracts/` | Service interfaces |
| `TaskScheduler` / `PluginLoader` / Lua engine / permission interceptor | **planned (Phase 2)** |

## Plugin & Permission Rules

* Enforce `manifest.json` permissions **before** executing any Lua API.
* Intercept unauthorized calls and throw `PermissionDeniedException`.

## Constraints

* Depends downward only on `Rug.Core`; never references `Rug.UI` (L1 §5.1).
