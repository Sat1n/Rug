---
id: rug_ui_core
type: logic_node
inputs: [rug_core]
outputs: [rug_ui]
tags: [host, logic, interop, vision, lua, plugins]
---

# Rug.UI.Core Index (L2)

Managed host logic between the native core and the UI. It is the **single managed
P/Invoke boundary** to `Rug.Core.dll` and exposes safe, async vision services
(OCR, template matching) plus model discovery; it will also host task scheduling,
plugin loading and the sandboxed Lua runtime. It contains no XAML.

> **Status:** native interop + vision services implemented (Phase 1, Task 1.4):
> `Native/`, `Models/Vision.cs`, `Services/OcrService.cs`,
> `Services/TemplateMatchService.cs`. `FileService`/`Json` pre-exist.
> `TaskScheduler` / `PluginLoader` / Lua engine are **Phase 2 targets**.

## Internal Topology

| Path | Responsibility |
|---|---|
| `Native/Rug.Core.Native.cs` | **Only** P/Invoke surface — `LibraryImport`(UTF-8) + blittable struct maps of `Rug.Core.dll` |
| `Native/OcrEngineHandle.cs` · `SafeOcrResult.cs` · `SafeModelList.cs` | `SafeHandle` wrappers releasing native engine/result/list deterministically |
| `Native/RugNativeException.cs` | Non-zero native status → exception |
| `Models/Vision.cs` | Managed records: `OcrTextBlock`, `TemplateMatchResult`, `Rect`, `OcrEngineType` |
| `Contracts/Services/` | `IOcrService`, `ITemplateMatchService`, `IFileService` |
| `Services/OcrService.cs` | Async OCR (Task.Run / MTA): load image → engine (WinRT, or Paddle by discovered id) → managed blocks; frees native memory |
| `Services/TemplateMatchService.cs` | Async template match over `Rug_MatchTemplate` |
| `Services/FileService.cs` · `Helpers/Json.cs` | File IO / JSON helpers |
| `TaskScheduler` / `PluginLoader` / Lua engine / permission interceptor | **planned (Phase 2)** |

## Native Interop & Memory Safety (CRITICAL)

* **All** P/Invoke to `Rug.Core.dll` lives in `Native/Rug.Core.Native.cs` — nowhere
  else (not in `Rug.UI`, not in services or view-models). Uses `LibraryImport` with
  `StringMarshalling.Utf8`; C fixed char buffers map to `fixed byte` and are decoded
  as UTF-8, so CJK text and paths never mojibake.
* Native memory is owned by native: engine/result/list handles are wrapped in
  `SafeHandle`s that call `Rug_DestroyOcrEngine` / `Rug_FreeOcrResult` /
  `Rug_FreeModelList`; raw frame buffers are freed with `Rug_FreeBuffer`. Managed
  code never `Marshal.FreeHGlobal`s a native pointer.
* Inference runs on a background **MTA** thread (`Task.Run`) — never block the UI
  thread; the native WinRT OCR path requires MTA.
* `Rug.UI` consumes `IOcrService` / `ITemplateMatchService`; it does **not** P/Invoke.

## Plugin & Permission Rules

* Enforce `manifest.json` permissions **before** executing any Lua API.
* Intercept unauthorized calls and throw `PermissionDeniedException`.

## Constraints

* Depends downward only on `Rug.Core` (through the `Native/` P/Invoke boundary);
  never references `Rug.UI` (L1 §5.1).
* Requires the native `Rug.Core.dll` (x64) plus its dependency DLLs at runtime.
