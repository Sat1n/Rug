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
> `LuaRuntime` coroutine bridge (Task 2.1.1), plugin manifest/config loading and
> permission interception (Task 2.1.2), and multi-instance lifecycle scheduling
> with a Lua instruction watchdog (Task 2.1.3), plus anomaly black-box logging
> and the Agent rescue hook (Task 2.1.4), are implemented.
> Task 2.2 adds the managed input bridge and physical coordinate mapper.

## Internal Topology

| Path | Responsibility |
|---|---|
| `Native/Rug.Core.Native.cs` | **Only** Rug.Core P/Invoke surface — `LibraryImport`(UTF-8) + blittable struct maps (frame/OCR/match/input/capture) |
| `Native/WindowNative.cs` | user32 windowing interop for the crosshair picker (`WindowFromPoint`/`GetDpiForWindow`/`MonitorFromWindow`/`GetMonitorInfoW`/`ScreenToClient`/…); OS inspection, not Rug.Core capability |
| `Native/OcrEngineHandle.cs` · `SafeOcrResult.cs` · `SafeModelList.cs` · `InputControllerHandle.cs` · `CapturerHandle.cs` | `SafeHandle` wrappers releasing native engine/result/list/input/capturer deterministically |
| `Native/RugNativeException.cs` | Non-zero native status → exception |
| `Helpers/DpiHelper.cs` | Per-Monitor DPI `PhysicalToLogical` / `LogicalToPhysical` point conversion (96-DPI baseline) |
| `Models/Vision.cs` | Managed records: `OcrTextBlock`, `TemplateMatchResult`, `Rect`, `OcrEngineType` |
| `Models/Input.cs` | Managed records: native `InputMode`, delivery `InputDeliveryMode` (`Win32SendInput`/`Win32PostMessage`), `MouseButton`(L/R/M/X1/X2), `TrajectoryType`, `HumanizeConfig`, `TrajectorySample` |
| `Models/Capture.cs` · `Models/WindowInfo.cs` · `Models/Geometry.cs` | `CapturedFrame` (managed BGRA8 copy) · `WindowInfo` (HWND/title/process/window+client size/DPI/**monitor name+bounds**) · `PointInt` |
| `Contracts/Services/` | `IOcrService`, `ITemplateMatchService`, `IInputService`, `ICaptureService`, `IWindowSpyService`, `IFileService` |
| `Services/OcrService.cs` | Async OCR (Task.Run / MTA) from a file **or** an in-memory `CapturedFrame`; engine = WinRT or Paddle by discovered id; frees native memory |
| `Services/TemplateMatchService.cs` | Async template match over `Rug_MatchTemplate` |
| `Services/InputService.cs` | Async humanized input (Task.Run / MTA): mouse move/click/drag, key press, `SendText`, dry-run `PlanTrajectoryAsync`; `SetTargetAsync(hwnd, background)` binds the window + delivery mode. Mouse coords are **client-space**, confined to the window by native |
| `Abstractions/ICoordinateMapper.cs` · `Services/CoordinateMapper.cs` | Validate a physical client point against the target HWND and project it through Win32 `ClientToScreen` for diagnostics and tests |
| `Services/InputBridge.cs` | Permission-gated input dispatch, per-call foreground/background selection, focus guard, button selection and cancellable key hold |
| `Services/CaptureService.cs` | Async WGC capture (Task.Run / MTA): start/stop a native capturer, copy each frame to managed BGRA8, free the native buffer |
| `Services/WindowSpyService.cs` | Resolve the window under the cursor → `WindowInfo` (incl. its monitor via `MonitorFromWindow`); client-size query + `ClientPointUnderCursor` for the scripting coordinate picker. All physical pixels (PMv2-aware host) |
| `Abstractions/IScriptRuntime.cs` · `Models/ScriptExecutionResult.cs` | Script lifecycle/state contract |
| `Models/PluginManifest.cs` · `Models/PluginConfigSchema.cs` · `Models/Permission.cs` | Plugin identity/entry/permission declarations, UI control schema and typed defaults, exact permission names |
| `Services/LuaRuntime.cs` | NLua coroutine bridge: `StepAsync` yields a `rug.*` operation, starts its .NET task, and `ResumeAsync` awaits that task before resuming Lua; all Lua-state access is serialized |
| `Abstractions/IPluginManager.cs` · `Services/PluginManager.cs` | Scan immediate plugin directories, validate manifest/entry/config, cache valid plugins and UI schema; skip malformed plugins with a warning |
| `Security/PermissionInterceptor.cs` · `Exceptions/PermissionDeniedException.cs` | Exact-match permission checks before sensitive API tasks start; denied calls log an audit warning and fault the script |
| `Abstractions/ITaskScheduler.cs` · `Models/TaskExecutionContext.cs` · `Services/TaskScheduler.cs` | Concurrent plugin/window instances, per-instance capture/input/runtime ownership, pause/resume/stop state and periodic lifecycle dispatch |
| `Abstractions/IAnomalyLogger.cs` · `Models/AnomalyLogModel.cs` · `Services/AnomalyLogger.cs` | Per-instance incident capture, PNG encoding and JSON black-box records |
| `Services/FileService.cs` · `Helpers/Json.cs` | File IO / JSON helpers |

## Task lifecycle and watchdog (Task 2.1.3)

* `TaskScheduler.StartTaskAsync(pluginId, hwnd)` gets a scanned plugin, creates
  independent capture/input/runtime instances through factories, starts capture
  before Lua can call `rug.capture()`, prepares the lifecycle coroutine, then
  registers the instance and returns its ID. A background runner executes
  `on_init()` once, followed by `on_tick()` at a configurable interval. The script
  body defines hooks on its first run. Missing hooks are no-ops.
* `PauseTask` suspends progress at the next coroutine boundary and preserves the
  pending operation; `ResumeTask` continues it. `StopTaskAsync` cancels the instance,
  awaits the runner, invokes `on_stop()` with a bounded watchdog, stops/disposes
  capture, and disposes Lua/input resources. `on_stop()` is synchronous; yielding
  from it is rejected during cleanup. Instances are removed after cleanup.
* The host captures `debug.sethook` before removing Lua's `debug` and `coroutine`
  globals. Each host-created coroutine gets a count hook that checks cancellation
  every 10,000 Lua instructions. The hook and coroutine handles live in private
  Lua closures held by C#, so script globals cannot replace the cancellation
  callback or create an unhooked coroutine. A tight loop is interrupted once
  `Stop()` cancels its token; the stop hook has its own deadline. The capture and
  input factories must provide distinct service instances for each task.

## Anomaly black box and Agent hook (Task 2.1.4)

* A manifest with `agent` permission may call
  `rug.agent.resolve_anomaly(reason, context)`. The Lua bridge yields immediately,
  captures a private Lua traceback alongside the supplied context, and dispatches
  to the owning task's `IAnomalyLogger`. Unauthorized calls raise an audited
  `PermissionDeniedException` before invoking the logger.
* `AnomalyLogger` captures the owning instance's current BGRA frame and writes a
  real RGBA PNG plus same-basename JSON under `./logs/anomalies/` by default.
  The JSON stores a UTC timestamp, plugin/instance IDs, reason, script context,
  relative screenshot filename, and an explicit null `agent_resolution` field for
  Phase 4. The service returns the JSON path and emits `[Anomaly Detected]` at
  warning level. The directory can be overridden by the host or tests.
* Once the record is written, the runtime returns `Faulted` without resuming the
  reporting Lua coroutine. The scheduler records the error, runs `on_stop`, and
  releases the capture session and per-instance resources. A capture or disk
  failure also faults the task and is surfaced through `LastError`.

## Native input bridge and coordinates (Task 2.2)

* `rug.click(x, y, button?, mode?)` and `rug.press_key(vk, duration_ms?)` require
  `input`. Buttons accept `left`, `right`, `middle`, `x1`, `x2` or their native
  integer values; modes accept `Win32SendInput`/`sendinput`/`foreground` and
  `Win32PostMessage`/`postmessage`/`background`, case-insensitively. A missing mode
  uses the plugin default. A positive key duration uses an asynchronous delay
  between KeyDown and KeyUp; cancellation still releases the key. Zero duration
  uses the native humanized press duration.
* `manifest.json` may declare `inputDelivery` as `Win32SendInput` or
  `Win32PostMessage`. If absent, the host-only `BackgroundInput` flag chooses the
  default (background by default). The bridge binds the task's HWND before any
  input, validates click points within the current client rect, and rejects a
  foreground click/key if that HWND cannot be brought to the foreground.
* The WinUI app manifest declares PerMonitorV2 awareness. Script and native input
  coordinates are **physical client pixels**. `CoordinateMapper.ClientToScreen`
  uses Win32 for the physical screen projection without applying a second DPI
  scale. The bound native controller still receives the original client point:
  its PostMessage path needs client coordinates, and its SendInput path performs
  ClientToScreen plus virtual-desktop normalization itself. Passing the managed
  screen projection to the native controller would offset the click twice.

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
* `Configuration`, `TargetWindow`, `BackgroundInput`, and `PluginDirectory` on `PluginManifest` are
  host-only runtime fields ignored during JSON parsing. `InputDelivery` is an
  optional JSON declaration validated at scan time. The manager fills `Configuration`
  with typed config defaults; the host may override values and supplies the window.
  Permissions are copied into `PermissionInterceptor` on initialization and checked
  before each `rug.*` service task. A missing grant throws
  `PermissionDeniedException`, logs plugin/API/
  permission details, and puts the runtime in `Faulted` state. Defined names:
  `timer`, `vision.capture`, `vision.ocr`, `input`, `network`, `filesystem`, `agent`.

## Lua coroutine contract (Task 2.1.1)

* `InitializeAsync` compiles a script into a Lua coroutine. In one-shot mode the
  script body runs first, followed by `on_tick()` once. In scheduler mode,
  `PrepareLifecycleAsync` changes the initial hook to `on_init()` and
  `BeginTickAsync` creates a fresh coroutine for each tick. `StepAsync` begins execution and returns
  on the first async yield. `ResumeAsync` asynchronously waits for exactly one pending
  operation and drives the coroutine to the next yield or completion. Neither holds a
  thread during `Task.Delay` or a pending service call. `Stop()` cancels the linked
  lifetime token and promptly interrupts a pending `ResumeAsync`.
* `rug.sleep(ms)` requires `timer`; `rug.capture()` requires `vision.capture` and
  returns frame width/height/stride metadata; `rug.ocr(engine_type)` requires
  `vision.ocr`, recognizes the most recently captured frame, and returns an array of
  text/score/box tables. `rug.click(x,y,button?,mode?)` and
  `rug.press_key(vk,duration_ms?)` require `input`.
  The host starts an `ICaptureService` session before `rug.capture()` is called.
  An input-enabled manifest must provide `TargetWindow`; initialization binds it via
  `IInputService.SetTargetAsync`. Click coordinates are client-space and confinement
  is performed by the input service.
  `rug.get_config(key)` reads manifest configuration; `rug.log(level,message)` routes
  through `ILogger` (one argument defaults to info). Permissions default to denied.
* Lua runs on a worker thread behind a state gate. `io`, `os`, `package`, `require`,
  `load`, `debug`, `coroutine`, `collectgarbage` and NLua CLR globals are removed
  before script execution. Host services are only reachable through gated `rug.*`
  operations.
  KeraLua is configured for UTF-8 so non-ASCII Lua reasons and context survive
  the bridge into the JSON incident record.
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
