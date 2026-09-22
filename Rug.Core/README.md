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
> Task 1.3 OCR (`IOcrEngine` + full `WinRtOcrEngine`, `PaddleOcrEngine` skeleton)
> and OpenCV template matching (`ImageMatcher` skeleton). Input is the remaining
> **Phase 1 target**. Reference impl: [Rug.Poc](../Rug.Poc/README.md).
>
> **Optional native deps:** OpenCV and ONNX Runtime are NOT yet wired in. Their
> code is behind `RUG_HAS_OPENCV` / `RUG_HAS_ONNX`; without those macros
> `ImageMatcher` returns `RUG_ERR_UNSUPPORTED` and `PaddleOcrEngine` returns
> `RUG_ERR_OCR_MODEL_NOT_FOUND` (missing model) or `RUG_ERR_UNSUPPORTED`.

## Internal Topology

| File | Responsibility |
|---|---|
| `include/RugCoreAbi.h` | **C-ABI surface** — exports, status codes, handles, structs (exists) |
| `RugCoreAbi.cpp` | C-ABI impl — capture, OCR, template-matching exports + buffer ownership (exists) |
| `WgcCapturer.h` / `.cpp` | WGC + D3D11 capture: window resolve, black-frame retry, DPI scale (exists) |
| `IOcrEngine.h` | OCR strategy interface + `OcrResult`/`OcrLine` types (exists) |
| `ImageView.h` | Shared non-owning BGRA8 pixel view (exists) |
| `WinRtOcrEngine.h` / `.cpp` | WinRT OCR: 2x Fant upscale, recognize, CJK `CompactText` (exists) |
| `PaddleOcrEngine.h` / `.cpp` | PP-OCRv5/ONNX back-end — guarded skeleton `RUG_HAS_ONNX` (exists) |
| `ImageMatcher.h` / `.cpp` | OpenCV template matching — guarded skeleton `RUG_HAS_OPENCV` (exists) |
| `CoreCom.h` | Shared COM apartment helper (exists) |
| `InputController.*` | Dual-mode input: PostMessage and Bezier-curve SendInput (planned) |
| `pch.h` / `framework.h` | Precompiled Win32 + WinRT headers |

## Symbol Anchors (C-ABI)

* Status codes: `[RugStatus](include/RugCoreAbi.h#enum:RugStatus)`
* Buffer release: `[Rug_FreeBuffer](include/RugCoreAbi.h#function:Rug_FreeBuffer)`
* Capture: `[Rug_CreateCapturer](include/RugCoreAbi.h#function:Rug_CreateCapturer)` ·
  `[Rug_GrabFrame](include/RugCoreAbi.h#function:Rug_GrabFrame)`
* OCR: `[Rug_CreateOcrEngine](include/RugCoreAbi.h#function:Rug_CreateOcrEngine)` ·
  `[Rug_RecognizeText](include/RugCoreAbi.h#function:Rug_RecognizeText)` ·
  `[Rug_FreeOcrResult](include/RugCoreAbi.h#function:Rug_FreeOcrResult)`
* Matching: `[Rug_MatchTemplate](include/RugCoreAbi.h#function:Rug_MatchTemplate)`
* Input: `[Rug_Click](include/RugCoreAbi.h#function:Rug_Click)`

## Symbol Anchors (capture internals)

* Capturer class: `[WgcCapturer](WgcCapturer.h#class:WgcCapturer)`
* Window resolution: `[ResolveRenderWindow](WgcCapturer.cpp#function:ResolveRenderWindow)`
* Black-frame guard: `[IsAllBlack](WgcCapturer.cpp#function:IsAllBlack)`

## Symbol Anchors (OCR / matching internals)

* Strategy interface: `[IOcrEngine](IOcrEngine.h#class:IOcrEngine)`
* WinRT back-end: `[WinRtOcrEngine](WinRtOcrEngine.h#class:WinRtOcrEngine)`
* CJK space stripping: `[CompactText](WinRtOcrEngine.cpp#function:CompactText)`
* 2x super-resolution: `[PrepareOcrBitmap](WinRtOcrEngine.cpp#function:PrepareOcrBitmap)`
* Paddle back-end: `[PaddleOcrEngine](PaddleOcrEngine.h#class:PaddleOcrEngine)`
* Template matcher: `[ImageMatcher](ImageMatcher.h#class:ImageMatcher)`

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
                                                       [InputController] ─> [OS input]  (planned)
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
* OpenCV / ONNX Runtime paths are compile-guarded (`RUG_HAS_OPENCV` /
  `RUG_HAS_ONNX`); Rug.Core builds and runs WinRT-only until they are wired in.
