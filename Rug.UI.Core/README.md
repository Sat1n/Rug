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
(OCR, template matching, WGC capture) plus model discovery, humanized input
synthesis and OS window inspection; it will also host task scheduling, plugin
loading and the sandboxed Lua runtime. It contains no XAML.

> **Status:** native interop + vision services implemented (Phase 1, Task 1.4):
> `Native/`, `Models/Vision.cs`, `Services/OcrService.cs`,
> `Services/TemplateMatchService.cs`; input service added (Task 1.5):
> `Models/Input.cs`, `Native/InputControllerHandle.cs`, `Services/InputService.cs`;
> capture + window-spy added (Task 1.6): `Native/CapturerHandle.cs`,
> `Native/WindowNative.cs`, `Models/Capture.cs`, `Models/WindowInfo.cs`,
> `Services/CaptureService.cs`, `Services/WindowSpyService.cs`, `OcrService.RecognizeFrameAsync`.
> `FileService`/`Json` pre-exist.
> `LuaRuntime` coroutine bridge (Task 2.1.1) and plugin manifest/config loading
> with permission interception (Task 2.1.2) are implemented.
> `TaskScheduler` and host lifecycle integration are subsequent Phase 2 targets.

## Internal Topology

| Path | Responsibility |
|---|---|
| `Native/Rug.Core.Native.cs` | **Only** Rug.Core P/Invoke surface — `LibraryImport`(UTF-8) + blittable struct maps (frame/OCR/match/input/capture) |
| `Native/WindowNative.cs` | user32 windowing interop for the crosshair picker (`WindowFromPoint`/`GetDpiForWindow`/`MonitorFromWindow`/`GetMonitorInfoW`/`ScreenToClient`/…); OS inspection, not Rug.Core capability |
| `Native/OcrEngineHandle.cs` · `SafeOcrResult.cs` · `SafeModelList.cs` · `InputControllerHandle.cs` · `CapturerHandle.cs` | `SafeHandle` wrappers releasing native engine/result/list/input/capturer deterministically |
| `Native/RugNativeException.cs` | Non-zero native status → exception |
| `Helpers/DpiHelper.cs` | Per-Monitor DPI `PhysicalToLogical` / `LogicalToPhysical` point conversion (96-DPI baseline) |
| `Models/Vision.cs` | Managed records: `OcrTextBlock`, `TemplateMatchResult`, `Rect`, `OcrEngineType` |
| `Models/Input.cs` | Managed records: `InputMode`, `MouseButton`(L/R/M/X1/X2), `TrajectoryType`, `HumanizeConfig`, `TrajectorySample` |
| `Models/Capture.cs` · `Models/WindowInfo.cs` · `Models/Geometry.cs` | `CapturedFrame` (managed BGRA8 copy) · `WindowInfo` (HWND/title/process/window+client size/DPI/**monitor name+bounds**) · `PointInt` |
| `Contracts/Services/` | `IOcrService`, `ITemplateMatchService`, `IInputService`, `ICaptureService`, `IWindowSpyService`, `IFileService` |
| `Services/OcrService.cs` | Async OCR (Task.Run / MTA) from a file **or** an in-memory `CapturedFrame`; engine = WinRT or Paddle by discovered id; frees native memory |
| `Services/TemplateMatchService.cs` | Async template match over `Rug_MatchTemplate` |
| `Services/InputService.cs` | Async humanized input (Task.Run / MTA): mouse move/click/drag, key press, `SendText`, dry-run `PlanTrajectoryAsync`; `SetTargetAsync(hwnd, background)` binds the window + delivery mode. Mouse coords are **client-space**, confined to the window by native |
| `Services/CaptureService.cs` | Async WGC capture (Task.Run / MTA): start/stop a native capturer, copy each frame to managed BGRA8, free the native buffer |
| `Services/WindowSpyService.cs` | Resolve the window under the cursor → `WindowInfo` (incl. its monitor via `MonitorFromWindow`); client-size query + `ClientPointUnderCursor` for the scripting coordinate picker. All physical pixels (PMv2-aware host) |
| `Abstractions/IScriptRuntime.cs` · `Models/ScriptExecutionResult.cs` | Script lifecycle/state contract |
| `Models/PluginManifest.cs` · `Models/PluginConfigSchema.cs` · `Models/Permission.cs` | Plugin identity/entry/permission declarations, UI control schema and typed defaults, exact permission names |
| `Services/LuaRuntime.cs` | NLua coroutine bridge: `StepAsync` yields a `rug.*` operation, starts its .NET task, and `ResumeAsync` awaits that task before resuming Lua; all Lua-state access is serialized |
| `Abstractions/IPluginManager.cs` · `Services/PluginManager.cs` | Scan immediate plugin directories, validate manifest/entry/config, cache valid plugins and UI schema; skip malformed plugins with a warning |
| `Security/PermissionInterceptor.cs` · `Exceptions/PermissionDeniedException.cs` | Exact-match permission checks before sensitive API tasks start; denied calls log an audit warning and fault the script |
| `Services/FileService.cs` · `Helpers/Json.cs` | File IO / JSON helpers |
| `TaskScheduler` / host lifecycle integration | **planned (Phase 2)** |

## Plugin declarations (Task 2.1.2)

* Each immediate child of the plugins root may contain `manifest.json` and the
  entry script. Required manifest properties are `id`, `name`, `version`,
  `targetProcess`, and `permissions` (an array of exact names); `entry` defaults
  to `main.lua`. `PluginManager.ScanPlugins` validates the entry stays inside its
  plugin directory, rejects links and duplicate IDs, and skips malformed JSON or
  invalid UI metadata. `GetPluginConfig(id)` returns the scanned config or throws
  for unknown/invalid IDs. A missing `config.json` means an empty UI schema.
* `config.json` uses `{ "fields": [...] }`. Each field has a unique `key`, `type`
  (`CheckBox`, `TextBox`, `Slider`, or `ComboBox`), and typed `default`; `label` is
  optional. Slider fields need `min`/`max` and may set `step`; ComboBox fields need
  string `options` containing the default. `PluginConfig.Defaults` exposes validated
  Boolean, string or numeric values for host-side rendering and initialization.
* `Configuration`, `TargetWindow`, and `BackgroundInput` on `PluginManifest` are
  runtime fields ignored during JSON parsing. The manager fills `Configuration`
  with typed config defaults; the host may override values and supplies the window.
  Permissions are copied into `PermissionInterceptor` on initialization and checked
  before each `rug.*` service task. A missing grant throws
  `PermissionDeniedException`, logs plugin/API/
  permission details, and puts the runtime in `Faulted` state. Defined names:
  `timer`, `vision.capture`, `vision.ocr`, `input`, `network`, `filesystem`.

## Lua coroutine contract (Task 2.1.1)

* `InitializeAsync` compiles a script into a Lua coroutine. The script body runs first;
  a declared `on_tick()` then runs once in the same coroutine. The future scheduler
  can create a fresh runtime for each tick. `StepAsync` begins execution and returns
  on the first async yield. `ResumeAsync` asynchronously waits for exactly one pending
  operation and drives the coroutine to the next yield or completion. Neither holds a
  thread during `Task.Delay` or a pending service call. `Stop()` cancels the linked
  lifetime token and promptly interrupts a pending `ResumeAsync`.
* `rug.sleep(ms)` requires `timer`; `rug.capture()` requires `vision.capture` and
  returns frame width/height/stride metadata; `rug.ocr(engine_type)` requires
  `vision.ocr`, recognizes the most recently captured frame, and returns an array of
  text/score/box tables. `rug.click(x,y)` and `rug.press_key(vk)` require `input`.
  The host starts an `ICaptureService` session before `rug.capture()` is called.
  An input-enabled manifest must provide `TargetWindow`; initialization binds it via
  `IInputService.SetTargetAsync`. Click coordinates are client-space and confinement
  is performed by the input service.
  `rug.get_config(key)` reads manifest configuration; `rug.log(level,message)` routes
  through `ILogger` (one argument defaults to info). Permissions default to denied.
* Lua runs on a worker thread behind a state gate. `io`, `os`, `package`, `require`,
  `load`, `debug`, `collectgarbage` and NLua CLR globals are removed before script
  execution. Host services are only reachable through gated `rug.*` operations.
  CPU preemption of non-yielding Lua and capture-session ownership are Task 2.1.3.
* The bridge is exercised by [script tests](../tests/Rug.UI.Core.Script.Tests/README.md)
  using fake services; no native DLL or target window is needed.

## Native Interop & Memory Safety (CRITICAL)

* **All** Rug.Core P/Invoke lives in `Native/Rug.Core.Native.cs`; OS window inspection
  lives in `Native/WindowNative.cs` (user32). Neither appears in `Rug.UI`, services or
  view-models. Rug.Core imports use `LibraryImport` with `StringMarshalling.Utf8`; C
  fixed char buffers map to `fixed byte` and are decoded as UTF-8, so CJK text and
  paths never mojibake.
* Native memory is owned by native: engine/result/list/input/capturer handles are
  wrapped in `SafeHandle`s that call `Rug_DestroyOcrEngine` / `Rug_FreeOcrResult` /
  `Rug_FreeModelList` / `Rug_DestroyInputController` / `Rug_DestroyCapturer`; raw frame
  buffers are freed with `Rug_FreeBuffer`. A `CapturedFrame` is a **managed copy** — its
  pinned array is passed to `Rug_RecognizeText` read-only and must never be freed natively.
  Managed code never `Marshal.FreeHGlobal`s a native pointer.
* Capture, inference and input run on a background **MTA** thread (`Task.Run`) — never
  block the UI thread; the native WGC + WinRT OCR paths require MTA. Input synthesis
  (which sleeps between humanized samples) also runs off the UI thread.
* `Rug.UI` consumes `IOcrService` / `ITemplateMatchService` / `IInputService` /
  `ICaptureService` / `IWindowSpyService`; it does **not** P/Invoke.

## Plugin & Permission Rules

* Enforce `manifest.json` permissions before starting each sensitive `rug.*` task.
  Unauthorized calls throw `PermissionDeniedException` and emit an audit warning.

## Constraints

* Depends downward only on `Rug.Core` (through the `Native/` P/Invoke boundary);
  never references `Rug.UI` (L1 §5.1).
* Requires the native `Rug.Core.dll` (x64) plus its dependency DLLs at runtime.
