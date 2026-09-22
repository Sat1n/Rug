// =============================================================================
//  PaddleOcrEngine.cpp — PP-OCRv5 / ONNX Runtime back-end skeleton (Task 1.3).
//
//  Without RUG_HAS_ONNX this translation unit compiles with only the C++
//  standard library and reports a graceful error. With RUG_HAS_ONNX defined and
//  onnxruntime available, Create() opens an Ort::Session for the model; the
//  det->crop->rec inference pipeline in Recognize() is the next implementation
//  step and currently returns RUG_ERR_UNSUPPORTED.
// =============================================================================

#include "pch.h"
#include "PaddleOcrEngine.h"
#include "RugCoreAbi.h"   // RugStatus codes

#include <filesystem>

#ifdef RUG_HAS_ONNX
#include <onnxruntime_cxx_api.h>
#include <string>
#pragma comment(lib, "onnxruntime.lib")
#endif

namespace rug::core {

#ifdef RUG_HAS_ONNX
namespace {
std::wstring Utf8ToWide(const char* s) {
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 0) return {};
    std::wstring w(static_cast<size_t>(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}
}  // namespace
#endif

struct PaddleOcrEngine::Impl {
    std::string modelPath;
#ifdef RUG_HAS_ONNX
    Ort::Env                     env{ ORT_LOGGING_LEVEL_WARNING, "rug.paddle" };
    Ort::SessionOptions          options;
    std::unique_ptr<Ort::Session> session;
#endif
};

PaddleOcrEngine::PaddleOcrEngine(std::string modelPath)
    : m_impl(std::make_unique<Impl>()) {
    m_impl->modelPath = std::move(modelPath);
}

PaddleOcrEngine::~PaddleOcrEngine() = default;

int32_t PaddleOcrEngine::Create(const char* modelPath, std::unique_ptr<IOcrEngine>& out) {
    if (!modelPath || modelPath[0] == '\0') return RUG_ERR_OCR_MODEL_NOT_FOUND;

    std::error_code ec;
    if (!std::filesystem::exists(modelPath, ec)) return RUG_ERR_OCR_MODEL_NOT_FOUND;

#ifndef RUG_HAS_ONNX
    (void)out;  // ONNX Runtime not wired into this build (Task 1.3 skeleton).
    return RUG_ERR_UNSUPPORTED;
#else
    try {
        std::unique_ptr<PaddleOcrEngine> created(new PaddleOcrEngine(modelPath));
        std::wstring wpath = Utf8ToWide(modelPath);
        created->m_impl->session = std::make_unique<Ort::Session>(
            created->m_impl->env, wpath.c_str(), created->m_impl->options);
        out = std::move(created);
        return RUG_OK;
    }
    catch (...) {
        return RUG_ERR_OCR_FAILED;
    }
#endif
}

int32_t PaddleOcrEngine::Recognize(const ImageView& image, OcrResult& out) {
    (void)image;
    (void)out;
#ifndef RUG_HAS_ONNX
    return RUG_ERR_UNSUPPORTED;
#else
    if (!m_impl || !m_impl->session) return RUG_ERR_NOT_INITIALIZED;
    // TODO(Task 1.3+): PP-OCRv5 detection -> box crop -> recognition pipeline.
    // Run m_impl->session over a preprocessed det model, then a rec model per
    // crop, decode CTC output to UTF-8 text, and append to out.lines.
    return RUG_ERR_UNSUPPORTED;
#endif
}

}  // namespace rug::core
