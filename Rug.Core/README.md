---
id: rug_core
type: logic_node
inputs: []
outputs: [rug_ui_core, rug_ui, rug_poc]
tags: [core, native, cabi]
---

# Rug.Core Index (L2)

Native C++20 DLL owning the perception-and-input pipeline: window capture (WGC),
OCR (WinRT), coordinate normalization and humanized input synthesis. Everything is
exposed to the managed host through a stable C-ABI. It contains **no** UI,
scripting, scheduling or plugin logic — those live above it.

> **Status:** Task 1.1 C-ABI surface; Task 1.2 capture (`WgcCapturer`);
> Task 1.3 OCR abstraction + WinRT back-end + template matching; Task 1.3.1 wires
> the real PP-OCRv5 (ONNX) and OpenCV paths plus the vcpkg/prebuilt dependency
> setup; Task 1.5 dual-mode input (`Win32InputController` live, KMBox guarded
> skeleton) with humanized trajectories. Reference impl:
> [Rug.Poc](../Rug.Poc/README.md).

## Dependencies & Build Guards

* **OpenCV + yaml-cpp** — supplied by **vcpkg manifest mode** ([vcpkg.json](vcpkg.json):
  `opencv4` features `jpeg`+`png`+`world` (single `opencv_world` lib/dll), plus
  `yaml-cpp`; dynamic `x64-windows`/`x86-windows`). Requires `vcpkg integrate install`
  once per dev machine. [ThirdParty.props](ThirdParty.props) always defines `RUG_HAS_OPENCV`.
* **ONNX Runtime** — **prebuilt** (official release zip). Set the `ONNXRUNTIME_ROOT`
  env var to the unzipped folder (must contain `include\` + `lib\`). When set,
  ThirdParty.props defines `RUG_HAS_ONNX`, wires include/lib, and adds a post-build
  step copying `onnxruntime.dll` + `onnxruntime_providers_shared.dll` into `$(OutDir)`;
  otherwise the Paddle path compiles out.
* **PP-OCR assets** — `Rug_CreateOcrEngine(1, model_path, ...)` takes a **directory**
  holding `det.onnx`, `rec.onnx`, `det.yml`, `rec.yml` (PaddleX export). The CTC
  dictionary is **embedded in `rec.yml`** and parsed via yaml-cpp — there is no
  `keys.txt`. Missing dir/files → `RUG_ERR_OCR_MODEL_NOT_FOUND`; without
  `RUG_HAS_ONNX` → `RUG_ERR_UNSUPPORTED`. Layout & download:
  [../models/README.md](../models/README.md).

## Internal Topology

| File | Responsibility |
|---|---|
| `include/RugCoreAbi.h` | **C-ABI surface** — exports, status codes, handles, structs (exists) |
| `RugCoreAbi.cpp` | C-ABI impl — capture, image I/O, OCR, discovery, template-matching exports + buffer ownership (exists) |
| `WgcCapturer.h` / `.cpp` | WGC + D3D11 capture: window resolve, black-frame retry, DPI scale (exists) |
| `IOcrEngine.h` | OCR strategy interface + `OcrResult`/`OcrLine` types (exists) |
| `ImageView.h` | Shared non-owning BGRA8 pixel view (exists) |
| `OcrTextUtils.h` | Shared `CompactText` / `IsCjk` CJK space stripping (both OCR back-ends) (exists) |
| `WinRtOcrEngine.h` / `.cpp` | WinRT OCR: 2x Fant upscale, recognize, `CompactText` (exists) |
| `PaddleOcrEngine.h` / `.cpp` | PP-OCR ONNX Det(DBNet)+Rec(CTC); params + dict parsed from `det.yml`/`rec.yml` — active under `RUG_HAS_ONNX`+`RUG_HAS_OPENCV` (exists) |
| `ImageMatcher.h` / `.cpp` | OpenCV `matchTemplate` (TM_CCOEFF_NORMED) + iterative peak suppression — active under `RUG_HAS_OPENCV` (exists) |
| `ModelCatalog.h` / `.cpp` | Scans `models/ocr/*`, parses `model.json` (yaml-cpp), routes by `engine` — model auto-discovery (exists) |
| `CoreCom.h` | Shared COM apartment helper (exists) |
| `vcpkg.json` | vcpkg manifest — OpenCV dependency set (exists) |
| `ThirdParty.props` | Defines `RUG_HAS_OPENCV`/`RUG_HAS_ONNX`, wires ONNX Runtime include/lib (exists) |
| `Input/IInputController.h` | Dual-mode input contract: enums, `HumanizeConfig`, `TrajectorySample`, factory decl (exists) |
| `Input/Humanizer.*` | Ease-in-out trajectory planner + velocity-driven dynamic polling cadence + Bezier corridor (exists) |
| `Input/Win32InputController.*` | Win32 back-end: bound-window client-space coords clamped to the client rect (cursor never leaves the window); background PostMessage or foreground SendInput via `SetBackgroundDelivery` (absolute moves normalized over the **virtual screen** with `MOUSEEVENTF_VIRTUALDESK`, so negative-coordinate multi-monitor setups work); left/right/middle + side buttons X1/X2 (exists) |
| `Input/KmboxInputController.*` | KMBox B+/Pro/Net back-end — **guarded skeleton**, returns `RUG_ERR_UNSUPPORTED` until the vendor protocol lands (exists) |
| `Input/InputControllerFactory.cpp` | `CreateInputController(mode, hwnd)` — routes to the Win32 or KMBox back-end (exists) |
| `pch.h` / `framework.h` | Precompiled Win32 + WinRT headers |

## Symbol Anchors (C-ABI)

* Status codes: `[RugStatus](include/RugCoreAbi.h#enum:RugStatus)`
* Buffer release: `[Rug_FreeBuffer](include/RugCoreAbi.h#function:Rug_FreeBuffer)`
* Image I/O: `[Rug_LoadImageFile](include/RugCoreAbi.h#function:Rug_LoadImageFile)`
* Capture: `[Rug_CreateCapturer](include/RugCoreAbi.h#function:Rug_CreateCapturer)` ·
  `[Rug_GrabFrame](include/RugCoreAbi.h#function:Rug_GrabFrame)`
* OCR: `[Rug_CreateOcrEngine](include/RugCoreAbi.h#function:Rug_CreateOcrEngine)` ·
  `[Rug_RecognizeText](include/RugCoreAbi.h#function:Rug_RecognizeText)` ·
  `[Rug_FreeOcrResult](include/RugCoreAbi.h#function:Rug_FreeOcrResult)`
* Discovery: `[Rug_ScanOcrModels](include/RugCoreAbi.h#function:Rug_ScanOcrModels)` ·
  `[Rug_CreateOcrEngineById](include/RugCoreAbi.h#function:Rug_CreateOcrEngineById)`
* Matching: `[Rug_MatchTemplate](include/RugCoreAbi.h#function:Rug_MatchTemplate)`
* Input: `[Rug_CreateInputController](include/RugCoreAbi.h#function:Rug_CreateInputController)` ·
  `[Rug_Input_MouseMove](include/RugCoreAbi.h#function:Rug_Input_MouseMove)` ·
  `[Rug_Input_Click](include/RugCoreAbi.h#function:Rug_Input_Click)` ·
  `[Rug_Input_PlanTrajectory](include/RugCoreAbi.h#function:Rug_Input_PlanTrajectory)`

## Symbol Anchors (capture internals)

* Capturer class: `[WgcCapturer](WgcCapturer.h#class:WgcCapturer)`
* Window resolution: `[ResolveRenderWindow](WgcCapturer.cpp#function:ResolveRenderWindow)`
* Black-frame guard: `[IsAllBlack](WgcCapturer.cpp#function:IsAllBlack)`

## Symbol Anchors (OCR / matching internals)

* Strategy interface: `[IOcrEngine](IOcrEngine.h#class:IOcrEngine)`
* WinRT back-end: `[WinRtOcrEngine](WinRtOcrEngine.h#class:WinRtOcrEngine)`
* CJK space stripping: `[CompactText](OcrTextUtils.h#function:CompactText)`
* 2x super-resolution: `[PrepareOcrBitmap](WinRtOcrEngine.cpp#function:PrepareOcrBitmap)`
* Paddle back-end: `[PaddleOcrEngine](PaddleOcrEngine.h#class:PaddleOcrEngine)`
* PP-OCR det post-process: `[BoxScore](PaddleOcrEngine.cpp#function:BoxScore)` ·
  `[GetRotateCropImage](PaddleOcrEngine.cpp#function:GetRotateCropImage)`
* PP-OCR CTC decode: `[CtcDecode](PaddleOcrEngine.cpp#function:CtcDecode)`
* Template matcher: `[ImageMatcher](ImageMatcher.h#class:ImageMatcher)`
* Model catalog: `[ScanOcrModels](ModelCatalog.cpp#function:ScanOcrModels)`

## Symbol Anchors (input internals)

* Input contract: `[IInputController](Input/IInputController.h#class:IInputController)`
* Factory: `[CreateInputController](Input/InputControllerFactory.cpp#function:CreateInputController)`
* Trajectory planner: `[Humanizer::Plan](Input/Humanizer.cpp#function:Plan)`
* Dynamic-cadence wait: `[Humanizer::Wait](Input/Humanizer.cpp#function:Wait)`
* Win32 back-end: `[Win32InputController](Input/Win32InputController.h#class:Win32InputController)`
* Client-rect confinement: `[Win32InputController::ClampClient](Input/Win32InputController.cpp#function:ClampClient)`
* KMBox back-end (skeleton): `[KmboxInputController](Input/KmboxInputController.h#class:KmboxInputController)`

## C-ABI Contract (CRITICAL)

1. **Exports:** every exported API uses `extern "C"` and returns `int32_t` error
   codes — `RUG_OK = 0`, `RUG_ERR_* < 0`. C++ exceptions must never cross the ABI;
   catch and translate them to codes.
2. **Memory ownership:** memory allocated by C++ must be freed by C++ via
   `Rug_FreeBuffer(uint8_t* ptr)`. Managed callers must never `Marshal.FreeHGlobal`
   a native pointer.
3. **Boundary:** this DLL is the only native surface the host may touch; the
   managed-side import restriction is enforced in [Rug.UI L2](../Rug.UI/README.md).

## Coding Standards (C++20)

* Compile with `/utf-8`.
* Normalize OCR coordinates against **Per-Monitor V2** DPI scaling before passing
  them to `InputController`.
* Keep WinRT apartment usage explicit; never leak `winrt::hresult_error` across the ABI.

## Data Flow

```text
[HWND] ─> [WgcCapturer] ─> BGRA8 frame ─┬─> [IOcrEngine: WinRT | Paddle] ─> lines + boxes ─┐
                                        └─> [ImageMatcher (OpenCV)] ─> match boxes ─────────┤
                                                                                            ▼
                                                       [InputController] ─> [OS input]  (Win32 live · KMBox skeleton)
   all wrapped by RugCoreAbi.h (int32_t codes; Rug_FreeBuffer / Rug_FreeOcrResult)
```

## Constraints

* No kernel anti-cheat bypass, no memory hacking, no CAPTCHA solving (L1 §5.2).
* No managed/UI dependencies; dependency direction is strictly one-way (L1 §5.1).
* Capture blocks on WinRT async (`.get()`); the host must call `Rug_*Capturer` /
  `Rug_GrabFrame` from a background **MTA** thread, never the STA UI thread
  (AGENTS.md: never block the UI thread).
* `Rug_GrabFrame` returns BGRA8 memory allocated with `new[]`; it MUST be released
  via `Rug_FreeBuffer` (`delete[]`) — managed code never frees it directly.
* `Rug_RecognizeText` allocates the result on the C++ heap; release it with
  `Rug_FreeOcrResult`, NOT `Rug_FreeBuffer` (which is only for raw `new[]` buffers).
* OpenCV / ONNX Runtime paths are compile-guarded: `RUG_HAS_OPENCV` is always on
  (vcpkg); `RUG_HAS_ONNX` is defined only when `ONNXRUNTIME_ROOT` is set. Without
  ONNX the Paddle engine returns `RUG_ERR_UNSUPPORTED`; WinRT + OpenCV still work.
* PP-OCR preprocessing params and the CTC dictionary are **parsed from `det.yml` /
  `rec.yml`** at load time (single source of truth); the constants in
  `PaddleOcrEngine.cpp` are only fallbacks for missing fields. det normalization
  keeps the exported **BGR** channel order (no RGB swap).
