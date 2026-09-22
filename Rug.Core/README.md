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

> **Status:** C-ABI surface declared (Task 1.1) and the **capture** path
> implemented (Task 1.2: `WgcCapturer` + `Rug_CreateCapturer`/`Rug_GrabFrame`/
> `Rug_DestroyCapturer`/`Rug_FreeBuffer`). OCR and input components are still
> **Phase 1 targets**. The working reference implementation is
> [Rug.Poc](../Rug.Poc/README.md).

## Internal Topology

| File | Responsibility |
|---|---|
| `include/RugCoreAbi.h` | **C-ABI surface** — `extern "C"` exports, status codes, handles, structs (exists) |
| `RugCoreAbi.cpp` | C-ABI implementation — capture exports + buffer ownership (exists) |
| `WgcCapturer.h` / `WgcCapturer.cpp` | WGC + D3D11 capture: window resolve, black-frame retry, DPI scale (exists) |
| `WinRtOcr.*` | OCR engine lifecycle, recognize, bounding-rect + DPI/scale normalization (planned) |
| `InputController.*` | Dual-mode input: PostMessage and Bezier-curve SendInput (planned) |
| `pch.h` / `framework.h` | Precompiled Win32 + WinRT headers |

## Symbol Anchors (C-ABI)

* Status codes: `[RugStatus](include/RugCoreAbi.h#enum:RugStatus)`
* Buffer release: `[Rug_FreeBuffer](include/RugCoreAbi.h#function:Rug_FreeBuffer)`
* Capture: `[Rug_CreateCapturer](include/RugCoreAbi.h#function:Rug_CreateCapturer)` ·
  `[Rug_GrabFrame](include/RugCoreAbi.h#function:Rug_GrabFrame)`
* OCR: `[Rug_RecognizeText](include/RugCoreAbi.h#function:Rug_RecognizeText)`
* Input: `[Rug_Click](include/RugCoreAbi.h#function:Rug_Click)`

## Symbol Anchors (capture internals)

* Capturer class: `[WgcCapturer](WgcCapturer.h#class:WgcCapturer)`
* Window resolution: `[ResolveRenderWindow](WgcCapturer.cpp#function:ResolveRenderWindow)`
* Black-frame guard: `[IsAllBlack](WgcCapturer.cpp#function:IsAllBlack)`

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
[HWND] ─> [WgcCapturer] ─> SoftwareBitmap ─> [WinRtOcr] ─> normalized coords ─> [InputController] ─> [OS input]
                         all wrapped by RugAbi.h (int32_t codes + Rug_FreeBuffer)
```

## Constraints

* No kernel anti-cheat bypass, no memory hacking, no CAPTCHA solving (L1 §5.2).
* No managed/UI dependencies; dependency direction is strictly one-way (L1 §5.1).
* Capture blocks on WinRT async (`.get()`); the host must call `Rug_*Capturer` /
  `Rug_GrabFrame` from a background **MTA** thread, never the STA UI thread
  (AGENTS.md: never block the UI thread).
* `Rug_GrabFrame` returns BGRA8 memory allocated with `new[]`; it MUST be released
  via `Rug_FreeBuffer` (`delete[]`) — managed code never frees it directly.
