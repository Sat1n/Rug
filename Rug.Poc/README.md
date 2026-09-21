---
id: rug_poc
type: logic_node
inputs: [rug_core]
outputs: []
tags: [sandbox, validation, reference]
---

# Rug.Poc Index (L2)

Standalone C++ console sandbox that proves the capture → OCR → input pipeline
end-to-end. It is the **reference implementation** Phase 1 decouples into
`Rug.Core`; it is not shipped and not part of the host runtime.

## Internal Topology

| File | Responsibility |
|---|---|
| `Rug.Poc.cpp` | Entire pipeline: D3D init, WGC capture, WinRT OCR, PostMessage click |

## Symbol Anchors

* Entry point: `[run](Rug.Poc.cpp#function:run)`
* Capture: `[CaptureWindowForOcr](Rug.Poc.cpp#function:CaptureWindowForOcr)`
* D3D setup: `[InitD3D](Rug.Poc.cpp#function:InitD3D)`
* OCR scaling: `[PrepareOcrBitmap](Rug.Poc.cpp#function:PrepareOcrBitmap)`
* Window lookup: `[GetHWNDByProcessName](Rug.Poc.cpp#function:GetHWNDByProcessName)`

## Data Flow

```text
[process name] ─> HWND ─> WGC frame ─> SoftwareBitmap ─> OCR lines
   ─> matched-text center (scale + client-offset normalized) ─> PostMessage click
```

## Constraints

* Sandbox only — no C-ABI, no managed interop; logic must stay portable into Rug.Core.
* Hardcoded targets (`QQMusic.exe`, `推荐` / `音乐`) are test fixtures, not product config.
