// =============================================================================
//  IOcrEngine.h
//  Strategy abstraction for OCR back-ends (Task 1.3).
//
//  Concrete engines: WinRtOcrEngine (Windows.Media.Ocr) and PaddleOcrEngine
//  (PP-OCRv5 via ONNX Runtime). The C-ABI factory in RugCoreAbi.cpp selects one
//  by RugOcrEngineType and owns it through an opaque handle.
//
//  This header has no third-party dependencies and never exposes WinRT/ONNX
//  types, so it is safe to include from anywhere in the core.
// =============================================================================

#ifndef IOCR_ENGINE_H
#define IOCR_ENGINE_H
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "ImageView.h"

namespace rug::core {

struct OcrBox {
    int32_t x = 0, y = 0, width = 0, height = 0;  // frame pixel coordinates
};

struct OcrLine {
    OcrBox         box;
    std::wstring   text;  // recognized line text (CJK spacing already compacted)
};

struct OcrResult {
    std::vector<OcrLine> lines;
};

class IOcrEngine {
public:
    virtual ~IOcrEngine() = default;

    // Recognize text within `image`. On RUG_OK, `out` is populated.
    // Returns a RugStatus value (RUG_OK == 0, errors < 0).
    virtual int32_t Recognize(const ImageView& image, OcrResult& out) = 0;
};

}  // namespace rug::core

#endif  // IOCR_ENGINE_H
