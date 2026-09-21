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

> **Status:** scaffolding only (`dllmain.cpp`, `pch.h`, `framework.h`). The
> components below are **Phase 1 targets**, not yet implemented; symbol anchors are
> added in the same commit as the code (BLUEPRINT §4). The working reference
> implementation is [Rug.Poc](../Rug.Poc/README.md).

## Internal Topology (planned)

| File | Responsibility |
|---|---|
| `WgcCapturer.*` | D3D11 device + `GraphicsCaptureItem` + frame pool → `SoftwareBitmap` |
| `WinRtOcr.*` | OCR engine lifecycle, recognize, bounding-rect + DPI/scale normalization |
| `InputController.*` | Dual-mode input: PostMessage and Bezier-curve SendInput |
| `RugAbi.h` | `extern "C"` exports, error codes, buffer-ownership API |
| `pch.h` / `framework.h` | Precompiled Win32 + WinRT headers |

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
