// =============================================================================
//  ImageView.h — shared non-owning pixel-buffer view used by OCR and matching.
// =============================================================================

#ifndef IMAGE_VIEW_H
#define IMAGE_VIEW_H
#pragma once

#include <cstdint>

namespace rug::core {

// A non-owning view of a pixel buffer. Data is BGRA8 unless stated otherwise;
// the consumer never frees it. `stride` is bytes per row (may exceed width*4).
struct ImageView {
    const uint8_t* data   = nullptr;
    int32_t        width  = 0;
    int32_t        height = 0;
    int32_t        stride = 0;
};

}  // namespace rug::core

#endif  // IMAGE_VIEW_H
