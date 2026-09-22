// =============================================================================
//  RugCoreAbi.cpp — C-ABI implementation (Task 1.2: capture + buffer ownership).
//
//  Implements the capture-related exports declared in include/RugCoreAbi.h.
//  OCR and input exports are added in later Phase 1 tasks.
//
//  Memory rule: any buffer handed to the caller here is allocated with new[]
//  and released by Rug_FreeBuffer with delete[]. The two must always pair.
// =============================================================================

#include "pch.h"
#include "RugCoreAbi.h"
#include "WgcCapturer.h"

#include <cstring>
#include <memory>

using rug::core::WgcCapturer;
using rug::core::CapturedFrame;

namespace {
// The opaque ABI handle is the WgcCapturer instance pointer.
inline WgcCapturer* ToCapturer(RugCapturerHandle h) {
    return reinterpret_cast<WgcCapturer*>(h);
}
inline RugCapturerHandle FromCapturer(WgcCapturer* p) {
    return reinterpret_cast<RugCapturerHandle>(p);
}
}  // namespace

extern "C" {

// --- Memory ownership --------------------------------------------------------

RUGCORE_API void RUGCORE_CALL Rug_FreeBuffer(uint8_t* ptr) {
    delete[] ptr;  // matches the new[] allocation in Rug_GrabFrame
}

// --- Capture -----------------------------------------------------------------

RUGCORE_API int32_t RUGCORE_CALL Rug_CreateCapturer(const RugCaptureConfig* config,
                                                    RugCapturerHandle* outHandle) {
    if (!config || !outHandle)        return RUG_ERR_INVALID_PARAM;
    if (!config->targetWindow)        return RUG_ERR_INVALID_PARAM;
    *outHandle = nullptr;

    try {
        WgcCapturer::Config c;
        c.targetWindow  = static_cast<HWND>(config->targetWindow);
        c.captureCursor = config->captureCursor != 0;

        auto capturer = std::make_unique<WgcCapturer>(c);
        int32_t st = capturer->Start();
        if (st != RUG_OK) return st;

        *outHandle = FromCapturer(capturer.release());
        return RUG_OK;
    }
    catch (...) {
        return RUG_ERR_CAPTURE_FAILED;  // never let an exception cross the ABI
    }
}

RUGCORE_API int32_t RUGCORE_CALL Rug_DestroyCapturer(RugCapturerHandle handle) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    try {
        std::unique_ptr<WgcCapturer> capturer(ToCapturer(handle));  // Stop() in dtor
        return RUG_OK;
    }
    catch (...) {
        return RUG_ERR_UNKNOWN;
    }
}

RUGCORE_API int32_t RUGCORE_CALL Rug_GrabFrame(RugCapturerHandle handle,
                                               RugFrame* outFrame) {
    if (!handle || !outFrame) return RUG_ERR_INVALID_PARAM;
    std::memset(outFrame, 0, sizeof(*outFrame));

    CapturedFrame cf;
    try {
        int32_t st = ToCapturer(handle)->GrabFrame(cf);
        if (st != RUG_OK) return st;
    }
    catch (...) {
        delete[] cf.data;
        return RUG_ERR_CAPTURE_FAILED;
    }

    outFrame->data       = cf.data;       // ownership transfers to the caller
    outFrame->dataLength = cf.dataLength;
    outFrame->width      = cf.width;
    outFrame->height     = cf.height;
    outFrame->stride     = cf.stride;
    outFrame->format     = RUG_PIXEL_BGRA8;
    return RUG_OK;
}

}  // extern "C"
