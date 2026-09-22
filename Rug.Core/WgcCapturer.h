// =============================================================================
//  WgcCapturer.h
//  Windows Graphics Capture (WGC) + Direct3D11 window capturer.
//
//  Internal C++ class (NOT part of the C-ABI). The public ABI wrappers
//  Rug_CreateCapturer / Rug_GrabFrame / Rug_DestroyCapturer live in
//  RugCoreAbi.cpp and own instances of this class through opaque handles.
//
//  Responsibilities (Task 1.2):
//    - Resolve an input HWND to the largest valid main render window, skipping
//      WS_EX_LAYERED / WS_EX_TRANSPARENT click-through overlays (e.g. CEF).
//    - Maintain a persistent WGC session backed by a D3D11 device.
//    - Wait for the first frame and retry/discard black or empty frames.
//    - Report physical pixel data plus the Per-Monitor V2 DPI scale.
// =============================================================================

#ifndef WGC_CAPTURER_H
#define WGC_CAPTURER_H
#pragma once

#include <windows.h>
#include <cstdint>
#include <memory>

namespace rug::core {

// A captured frame. `data` is a BGRA8 buffer allocated with new[] and MUST be
// released with delete[] (the ABI frees it via Rug_FreeBuffer).
struct CapturedFrame {
    uint8_t* data       = nullptr;
    uint32_t dataLength = 0;
    int32_t  width      = 0;     // physical pixels
    int32_t  height     = 0;     // physical pixels
    int32_t  stride     = 0;     // bytes per row
    double   dpiScale   = 1.0;   // physical / logical (Per-Monitor V2)
};

class WgcCapturer {
public:
    struct Config {
        HWND targetWindow  = nullptr;  // any window of the target process
        bool captureCursor = false;    // include the cursor in the capture
    };

    explicit WgcCapturer(Config config);
    ~WgcCapturer();

    WgcCapturer(const WgcCapturer&)            = delete;
    WgcCapturer& operator=(const WgcCapturer&) = delete;

    // Initialize D3D11/WGC, resolve the render window and start the session.
    // Returns a RugStatus value (RUG_OK == 0).
    int32_t Start();

    // Grab the latest frame into `out`. On RUG_OK, out.data is new[]-allocated.
    int32_t GrabFrame(CapturedFrame& out);

    // Close the capture session and release native resources. Idempotent.
    void Stop();

    HWND   ResolvedWindow() const;
    double DpiScale()       const;

private:
    struct Impl;
    std::unique_ptr<Impl> m_impl;
};

}  // namespace rug::core

#endif  // WGC_CAPTURER_H
