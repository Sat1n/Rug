// =============================================================================
//  WinRtOcrEngine.h
//  Windows.Media.Ocr back-end for IOcrEngine (Task 1.3).
//
//  Pipeline: BGRA8 frame -> 2x Fant super-resolution -> WinRT OCR ->
//  per-line bounding boxes (de-scaled) -> CompactText (strip CJK inter-glyph
//  spaces that Windows OCR inserts between Han characters).
// =============================================================================

#ifndef WINRT_OCR_ENGINE_H
#define WINRT_OCR_ENGINE_H
#pragma once

#include "IOcrEngine.h"
#include <memory>

namespace rug::core {

class WinRtOcrEngine : public IOcrEngine {
public:
    // Create the engine. Tries zh-Hans first, then the user's profile languages.
    // Returns a RugStatus value; on RUG_OK, `out` holds a ready engine.
    static int32_t Create(std::unique_ptr<IOcrEngine>& out);

    ~WinRtOcrEngine() override;

    int32_t Recognize(const ImageView& image, OcrResult& out) override;

private:
    WinRtOcrEngine();
    struct Impl;
    std::unique_ptr<Impl> m_impl;
};

}  // namespace rug::core

#endif  // WINRT_OCR_ENGINE_H
