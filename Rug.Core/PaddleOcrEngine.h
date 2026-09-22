// =============================================================================
//  PaddleOcrEngine.h
//  Paddle/ONNX (PP-OCRv5) back-end for IOcrEngine (Task 1.3).
//
//  Status: framework skeleton. The ONNX Runtime inference path is compiled only
//  when RUG_HAS_ONNX is defined and onnxruntime is wired into the project; until
//  then Create/Recognize fail gracefully (RUG_ERR_OCR_MODEL_NOT_FOUND when the
//  model file is missing, RUG_ERR_UNSUPPORTED when the back-end is not built in).
// =============================================================================

#ifndef PADDLE_OCR_ENGINE_H
#define PADDLE_OCR_ENGINE_H
#pragma once

#include "IOcrEngine.h"
#include <memory>
#include <string>

namespace rug::core {

class PaddleOcrEngine : public IOcrEngine {
public:
    // modelPath: UTF-8 path to the PP-OCRv5 ONNX model. Required.
    static int32_t Create(const char* modelPath, std::unique_ptr<IOcrEngine>& out);

    ~PaddleOcrEngine() override;

    int32_t Recognize(const ImageView& image, OcrResult& out) override;

private:
    explicit PaddleOcrEngine(std::string modelPath);
    struct Impl;
    std::unique_ptr<Impl> m_impl;
};

}  // namespace rug::core

#endif  // PADDLE_OCR_ENGINE_H
