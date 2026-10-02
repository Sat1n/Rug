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
synthesis and OS window inspection; it also hosts task scheduling, plugin
loading and the sandboxed Lua runtime. It contains no XAML.

> **Status:** native vision/input/capture interop, Lua/plugins/scheduler,
> black-box logs, scoped state/commands, schema/hot reload and Gateway are implemented.
> Process-local explicit grants and shared input arbitration support host manual takeover.
> Task 3.5-05B-01 adds opt-in Raw Input capture/diagnostics; automatic takeover
> integration and human-input acceptance remain pending.

## Internal Topology

| Path | Responsibility |
|---|---|
| `Native/Rug.Core.Native.cs` | **Only** Rug.Core P/Invoke surface — `LibraryImport`(UTF-8) + blittable struct maps (frame/OCR/match/input/capture) |
| `Native/WindowNative.cs` | user32 windowing interop for the crosshair picker (`WindowFromPoint`/`GetDpiForWindow`/`MonitorFromWindow`/`GetMonitorInfoW`/`ScreenToClient`/…); OS inspection, not Rug.Core capability |
| `Native/OcrEngineHandle.cs` · `SafeOcrResult.cs` · `SafeModelList.cs` · `InputControllerHandle.cs` · `CapturerHandle.cs` | `SafeHandle` wrappers releasing native engine/result/list/input/capturer deterministically |
| `Native/RugNativeException.cs` | Non-zero native status → exception |
| `Helpers/DpiHelper.cs` | Per-Monitor DPI `PhysicalToLogical` / `LogicalToPhysical` point conversion (96-DPI baseline) |
| `Models/Vision.cs` · `Models/VisionResultModels.cs` | Native-facing OCR/match records and Lua-facing best image hit / aggregated OCR result |
| `Models/Input.cs` | Managed records: native `InputMode`, delivery `InputDeliveryMode` (`Win32SendInput`/`Win32PostMessage`), `MouseButton`(L/R/M/X1/X2), `TrajectoryType`, `HumanizeConfig`, `TrajectorySample` |
| `Models/Capture.cs` · `Models/WindowInfo.cs` · `Models/Geometry.cs` | `CapturedFrame` (managed BGRA8 copy) · `WindowInfo` (HWND/title/process/window+client size/DPI/**monitor name+bounds**) · `PointInt` |
| `Contracts/Services/` | `IOcrService`, `ITemplateMatchService`, `IInputService`, `ICaptureService`, `IWindowSpyService`, `IFileService` |
| `Services/OcrService.cs` | Async OCR (Task.Run / MTA) from a file **or** an in-memory `CapturedFrame`; engine = WinRT or Paddle by discovered id; frees native memory |
| `Services/TemplateMatchService.cs` | Async template match over `Rug_MatchTemplate`, accepting a file or pinned managed BGRA frame |
| `Services/VisualEngineBridge.cs` | Single-frame cache per runtime, permission-gated capture/match/OCR, safe template paths, region crops and client-space result coordinates |
| `Services/InputService.cs` | Async humanized input (Task.Run / MTA): mouse move/click/drag, key press, `SendText`, dry-run `PlanTrajectoryAsync`; `SetTargetAsync(hwnd, background)` binds the window + delivery mode. Mouse coords are **client-space**, confined to the window by native |
| `Abstractions/ICoordinateMapper.cs` · `Services/CoordinateMapper.cs` | Validate a physical client point against the target HWND and project it through Win32 `ClientToScreen` for diagnostics and tests |
| `Services/InputBridge.cs` | Permission-gated input dispatch, foreground/background selection, focus guard and full-action shared leases |
| `Abstractions/IInputArbiter.cs` · `Models/InputOwnership.cs` · `Services/InputArbiter.cs` · `Services/InputWindowValidator.cs` · `Services/InputExecutionBoundary.cs` | Host owners, window lifetimes, bounded conflict-FIFO input queue, manual takeover and Gateway focus/input-progress outcome boundary; [contract](InputArbitration.md) |
| `Abstractions/IInputEventSource.cs` · `Models/InputEvents.cs` · `Services/RawInputEventSource.cs` · `Native/WindowsRawInputReceiver.cs` | Explicit host-owned Raw Input receiver, bounded detached events and diagnostics; [contract and acceptance](RawInputEvents.md) |
| `Services/CaptureService.cs` | Async WGC capture (Task.Run / MTA): start/stop a native capturer, copy each frame to managed BGRA8, free the native buffer |
| `Services/WindowSpyService.cs` | Resolve the window under the cursor or enumerate visible top-level windows → `WindowInfo` (incl. monitor via `MonitorFromWindow`); inspect a given HWND, check foreground state, query client size and pick client points. All physical pixels (PMv2-aware host) |
| `Abstractions/IScriptRuntime.cs` · `Models/ScriptExecutionResult.cs` | Script lifecycle/state contract |
| `Models/PluginManifest.cs` · `Models/PluginConfigSchema.cs` · `Models/Permission.cs` | Plugin identity/entry/permission declarations, UI control schema and typed defaults, exact permission names |
| `Services/LuaRuntime.cs` | NLua coroutine bridge: `StepAsync` yields a `rug.*` operation, starts its .NET task, and `ResumeAsync` awaits that task before resuming Lua; all Lua-state access is serialized |
| `Abstractions/IPluginManager.cs` · `Services/PluginManager.cs` | Scan immediate plugin directories, validate manifest/entry/config, cache valid plugins and UI schema; skip malformed plugins with a warning |
| `Abstractions/IPluginAuthorization.cs` · `Services/PluginAuthorization.cs` · `Models/AuthorizationModels.cs` | Process-local installation-bound explicit grants, immutable snapshots/revisions, revocable admission leases; [contract](Authorization.md) |
| `Security/PermissionInterceptor.cs` · `Exceptions/PermissionDeniedException.cs` | Exact-match permission checks before sensitive API tasks start; denied calls log an audit warning and fault the script |
| `Abstractions/ITaskScheduler.cs` · `Models/TaskExecutionContext.cs` · `Models/RunIdentity.cs` · `Services/TaskScheduler.cs` | Concurrent plugin/window runs, independent run generation and target-binding identity, per-instance service ownership and lifecycle dispatch |
| `Models/StateScope.cs` · `Models/StateChangedEventArgs.cs` · `Services/StateStore.cs` | Structural AppState/PluginConfig/InstanceState partitions, bounded JSON values, scoped revisions and detached events |
| `Abstractions/ICommandBus.cs` · `Services/CommandBus.cs` | Scoped state subscriptions with stale UI callback rejection and async named-command routing |
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
  capture, and disposes Lua/input resources after actual input and releases finish.
  Stop can wait for noncooperative native calls; no fixed timeout frees their
  input handles early. `on_stop()` is synchronous; yielding
  from it is rejected during cleanup. Instances are removed after cleanup.
  `ITaskScheduler.InstancesChanged` fires after an instance enters or leaves the
  active collection; the WinUI host marshals it to its dispatcher and rereads
  `Instances` to bind the selected run. A failed UI subscriber cannot fail the run.
* The host captures `debug.sethook` before removing Lua's `debug` and `coroutine`
  globals. Each host-created coroutine gets a count hook that checks cancellation
  every 10,000 Lua instructions. The hook and coroutine handles live in private
  Lua closures held by C#, so script globals cannot replace the cancellation
  callback or create an unhooked coroutine. A tight loop is interrupted once
  `Stop()` cancels its token; the stop hook has its own deadline. The capture and
  input factories must provide distinct service instances for each task.

## Anomaly black box and Agent hook (Task 2.1.4)

* A plugin declaring `agent` and holding a current host grant may call
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

* Task 3.5-05A adds [shared input arbitration](InputArbitration.md). Lua and
  Gateway input compete with host DevTools on actual window/global resources.
  Host takeover stops only input-declaring runs and waits for releases; grants
  are unchanged and returning control never revives an old run.

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

## Visual engine bridge (Task 2.3)

* Phase 1 exposes `ICaptureService`, `IOcrService`, and `ITemplateMatchService`
  separately; there is no `IRecognizeService`. `VisualEngineBridge` composes those
  contracts. `rug.capture()` requires `vision.capture` and replaces the one cached
  managed BGRA8 frame. `rug.find_image(path, threshold?, region?)` requires
  `vision.match`, searches that frame with OpenCV, and returns the strongest hit's
  top-left `{x,y,similarity}` or `nil`. Both threshold and returned score are in
  `[0,1]`. A missing cached frame is an error.
* `rug.ocr(region?, language?, engine?)` requires `vision.ocr` and returns a Lua array of
  `{text,score,x,y,width,height}` blocks plus `.text` (joined lines) and `.blocks`.
  A region is `{x,y,width,height}` in physical client pixels. Match and OCR crop
  the frame in managed memory, then add the region origin to returned boxes so
  every Lua coordinate remains relative to the bound window. The older
  `rug.ocr('winrt')` / `rug.ocr('paddle')` engine selector remains supported.
  A WinRT language argument is a BCP-47 tag passed to the native engine; native
  falls back to user profile languages if the requested language is unavailable.
  For a region-scoped Paddle fallback, use
  `rug.ocr(region, nil, 'paddle:ppocr_v6_medium')`; the suffix selects the installed
  model ID. This lets a plugin combine complementary WinRT and Paddle detections
  when Minecraft's pixel font causes one engine to miss a character.
* `rug.wait_image(path, timeout_ms?, threshold?, interval_ms?)` is a Lua coroutine
  helper that repeatedly captures, matches, and yields through `rug.sleep` until
  the image appears or the monotonic-clock deadline passes. It needs
  `vision.capture`, `vision.match`, and `timer`; defaults are 5 s timeout, 0.8
  threshold, and 100 ms interval. The scheduler thread is released at every
  capture, match, and sleep operation.
* Relative template paths resolve inside the scanned plugin directory. An
  absolute or escaping path additionally requires `filesystem`; linked asset
  paths are rejected. Missing files throw `FileNotFoundException`; native decode
  errors become `InvalidDataException`. Template matching pins the managed
  frame only for the native call, with no ownership transfer or temporary image
  file. OCR follows the same ownership rule. Replacing/disposing the bridge
  releases the previous frame for garbage collection. The native synthetic-BMP
  test verifies the pinned-frame path across 1,000 OpenCV calls.

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
  Permissions are requested capability ceilings. The host must explicitly grant
  all declarations before startup; live authorization and caller/run ceilings are
  checked before sensitive calls. See [Authorization](Authorization.md) for
  installation binding, revocation and reload rules. A missing grant throws
  `PermissionDeniedException`, logs plugin/API/
  permission details, and puts the runtime in `Faulted` state. Defined names:
  `timer`, `vision.capture`, `vision.match`, `vision.ocr`, `input`, `network`, `filesystem`, `agent`.

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
  returns frame width/height/stride metadata; `rug.ocr(region?,language?)` requires
  `vision.ocr` and recognizes the most recently captured frame.
  `rug.click(x,y,button?,mode?)` and
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
  The [Minecraft E2E harness](../Rug.UI.Core.Tests/README.md) instead binds a real
  visible HWND and runs the native WGC, WinRT/Paddle OCR, OpenCV, input, scheduler,
  Lua and anomaly path together.

## Real-window Phase 2 verification (Task 2.4)

* `Rug.UI.Core.Tests/E2E/Phase2EndToEndTests.cs` discovers one visible Minecraft
  window or accepts `--hwnd`. It checks a nonblack WGC frame and runs both WinRT
  `zh-CN` OCR and Paddle `ppocr_v6_medium`. The two OCR outputs are combined to
  locate `选项` and `单人游戏`, since each engine may miss a different pixel glyph.
* The harness crops a button template from the real WGC frame and verifies it with
  native OpenCV, then copies `minecraft_cn_e2e_loop.lua` into an isolated plugin
  directory with `vision.capture`, `vision.match`, `vision.ocr`, `input`, `timer`
  and `agent` grants. The Lua task clicks `选项`, polls WGC until `完成` appears,
  presses Esc, and polls for the main menu to return. Polling is necessary because
  the first frame after input may still show the preceding screen.
* The deliberate `rug.agent.resolve_anomaly` call must fault the task and write
  same-basename PNG/JSON with Chinese reason, Lua traceback, null
  `agent_resolution`, and a visually nonuniform capture. The harness also checks
  `on_stop` and that the scheduler's capture session is released. Output lives
  under the ignored E2E `bin/.../e2e-run/` directory. This is an explicit
  interactive test because it sends real input to the game.

## Scoped state and commands (Tasks 3.1.1, 3.5-02)

* `StateScope` is a structural address, separate from the user key; plugin IDs
  are normalized case-insensitively to match catalog lookup. `AppState`
  belongs to the application host for its lifetime; only host code uses the
  unscoped compatibility API. `PluginConfig` belongs to one plugin and is writable
  by the host/UI; it survives task stop and hot reload in the process, but this
  task does not persist it to disk. Lua reads a detached startup snapshot through
  `rug.get_config`, so later UI edits affect only future starts. `InstanceState`
  belongs to one `(pluginId, instanceId)` run; the host and that run's Lua may
  write it. The host opens the partition before launch inputs are written. Stop
  closes and evicts it, including its subscription registry; a missing instance
  partition rejects delayed writes, so no tombstone pool grows with run count.
  A new run never inherits it. `ActivePartitionCount` reports live partitions.
* A missing key returns the caller's default and is absent from `Snapshot`;
  explicit JSON null is present and reads as `default(T)` (null for nullable or
  reference types). `Contains` and `HadPreviousValue` distinguish it from absence.
  Values must serialize to JSON
  within 64 KiB and 16 levels. Stored bytes, typed reads, snapshots and complex
  event payloads are detached; complex event values are immutable `JsonElement`
  copies. Type mismatches throw `InvalidCastException`. Identical JSON writes do
  not notify. Each partition owns a monotonically increasing revision; per-key
  latest revisions suppress delayed UI notifications. Closed partitions reject
  writes and invalidate queued callbacks.
* `CommandBus.SubscribeState(scope, handler)` delivers only that scope and returns
  a disposable subscription; `CloseInstance` removes its subscribers. The legacy
  `StateChanged` event is AppState-only. Named commands remain host-routed. The
  core takes dispatcher delegates; `Rug.UI` supplies `DispatcherQueue`.
* A start creates a new `RunIdentity`: plugin ID, fresh instance GUID, per-plugin
  monotonic run generation within the scheduler lifetime, and a separate target-binding GUID for windowed
  runs. Windowless runs explicitly have no target binding. HWND is never used as
  identity. Launch inputs are copied into InstanceState before Lua starts;
  `ConfigureState` binds Lua to that scope. Lua `rug.get_state/set_state` cannot
  address AppState, PluginConfig or another instance. JSON launch scalars are
  unwrapped at the Lua read boundary: string, number, Boolean and null become
  Lua string, number, boolean and nil (including false taking the false branch).
  Arrays and objects remain detached structured JSON, preserving typed host
  reads such as StepFlow steps. Lua writes remain limited to nil, Boolean,
  string and number; actual Lua tests cover scalar types/values and structured
  launch inputs. Hot reload still stops the old run and
  starts a new one without preserving its Lua stack or InstanceState.

Raw Input retains native-tagged software with IsCandidateInput=false; unknown
tags and untagged zero-device input remain candidates. This spoofable observation
hint grants no authority. See [diagnostics](RawInputEvents.md) for the 5-second
countdown, separate callback/classification/phase counters and observable
post-start probe thread faults and independent F8/B AUTO rounds. Manual
keyboard-probe diagnosis remains pending; an [independent native probe](../tests/NativeInputProbe/README.md) provides comparison.

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

* [Explicit plugin grants](Authorization.md) are independent process-local host
  data. Scheduler/Lua startup and all sensitive bridges enforce live grants;
  revocation cancels affected instances before normal cleanup.
* Plugin UI lifecycle and Lua automation use `IAutomationGateway.ExecuteAsync`
  before dispatch to the registered run's services. Host-bound caller grants,
  complete run identity, cancellation/deadlines, bounded deduplication and
  sanitized outcomes are defined in [Automation Gateway](AutomationGateway.md).
  Existing bridge/path/input gates remain enforced; denied Lua calls retain
  `PermissionDeniedException` and a security warning. Anomaly/state/config/log
  keep their existing paths.
  `OperationParameterValidator` rejects wrong JSON types, missing fields and
  invalid static ranges before service dispatch. Capture checks cancellation
  again under its cache publication lock, so a late cancelled frame cannot
  replace a newer frame used by OCR or matching.

## Constraints

* Depends downward only on `Rug.Core` (through the `Native/` P/Invoke boundary);
  never references `Rug.UI` (L1 §5.1).
* Requires the native `Rug.Core.dll` (x64) plus its dependency DLLs at runtime.

## .NET 10 x64 build and verification (Task 3.5-01)

* The host targets `net10.0`; root `global.json` pins SDK `10.0.401`.
  Existing NuGet versions remain unchanged after Debug/Release x64 builds.
* Build with `dotnet build Rug.UI.Core/Rug.UI.Core.csproj -c Debug -p:Platform=x64 -warnaserror`
  (or use `Release`). Run the Lua/scheduler suite with
  `dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64`.
* With `x64/Debug/Rug.Core.dll` available, run the C# C-ABI visual path via
  `dotnet run --project tests/Rug.Core.CSharp.Tests/Rug.Core.CSharp.Tests.csproj -c Debug -p:Platform=x64 -- --visual-only`.
  The test project copies native DLLs from `x64/$(Configuration)/` next to its
  executable under `bin/x64/$(Configuration)/net10.0/`.

## Dynamic UI schema (Task 3.2)

* `Schema/SchemaParser.cs` parses UTF-8 `ui.json` into a bounded `UISchemaNode` tree (`Page`, `Group`, `SelectorBarGroup`, `Tab` and card nodes). It rejects malformed JSON, unknown controls, duplicate IDs, wrong default types, invalid numeric ranges, duplicate option keys and empty selector groups. The parser has no WinUI dependency.
* A node uses `type`, unique `id`, optional `title`/`description`, and type-specific properties. State controls use `stateKey` and a typed `default`; command controls use `command` and optional `parameter`. A selector group contains `tabs` of `Tab` nodes, each with `children`. `TaskSelectorCard` uses `tasks` (`id`, `title`) and a string-array default; `StepFlowView` uses `steps` (`id`, `title`, `status`, optional `thumbnail`) as defaults and reads its optional `stateKey` from the active InstanceState. `StepFlowStateBinding` matches notifications by both instance scope and schema key.
* `Rug.UI.Tests` loads the shipped example schema and checks every control kind, tab/tree structure, malformed input and state-bus round trips. The UI project owns actual WinUI construction; the core remains platform-neutral.

## Plugin hot reload (Task 3.3)

* `HotReloadService` watches each immediate plugin directory recursively for JSON/Lua writes, renames and deletes. It merges bursts per directory over a 400 ms window; watcher overflow schedules a full rescan. The service is background-only and exposes `RegisterUiHandler` plus success/failure events without referencing WinUI.
* For a changed plugin, it snapshots running instance HWNDs, stops all old instances (which invokes `on_stop` and releases WGC capture), validates `ui.json` and compiles all Lua sources without executing them, awaits registered UI rebuild callbacks, refreshes the scheduler's cached plugin catalog and starts a fresh runtime for each prior HWND. If validation or startup fails, HWNDs remain pending so the next valid save can restore them. A successful reload clears that pending set.
* `ITaskScheduler.AcquirePluginReloadAsync` holds a per-plugin start gate through stop, validation, UI rebuild and catalog refresh; concurrent starts of that plugin wait, while other plugins can start. `RefreshPluginsAsync` holds the catalog lock used by starts, so future starts observe changed manifest/config/entry files. The hot-reload service does not dispose the shared scheduler. Callers own both lifetimes and must dispose the watcher before the scheduler.

## Script-only demo tasks (Task 3.4)

* `TaskScheduler.StartTaskAsync(pluginId, 0)` accepts a windowless task only when the manifest declares neither `vision.capture` nor `input`. Such tasks still use a fresh Lua runtime and the ordinary lifecycle, but skip WGC session initialization. Window-bound automation continues to require a nonzero HWND and starts capture before Lua.
