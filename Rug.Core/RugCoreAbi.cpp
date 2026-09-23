// =============================================================================
//  RugCoreAbi.cpp — C-ABI implementation.
//  Task 1.2: capture + buffer ownership. Task 1.3: OCR + template matching.
//
//  Implements the capture, OCR and matching exports declared in
//  include/RugCoreAbi.h. Input exports are added in a later Phase 1 task.
//
//  Memory rule: any buffer handed to the caller here is allocated with new[]
//  and released by Rug_FreeBuffer with delete[]. The two must always pair.
// =============================================================================

#include "pch.h"
#include "RugCoreAbi.h"
#include "WgcCapturer.h"
#include "IOcrEngine.h"
#include "WinRtOcrEngine.h"
#include "PaddleOcrEngine.h"
#include "ImageMatcher.h"
#include "ModelCatalog.h"
#include "Input/IInputController.h"

#ifdef RUG_HAS_OPENCV
#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>
#include <opencv2/imgcodecs.hpp>
#endif

#include <cstring>
#include <memory>
#include <new>
#include <string>
#include <vector>

using rug::core::WgcCapturer;
using rug::core::CapturedFrame;
using rug::core::IOcrEngine;
using rug::core::ImageView;
using rug::core::OcrResult;
using rug::core::MatchBox;
using rug::core::input::IInputController;
using rug::core::input::InputMode;
using rug::core::input::MouseButton;
using rug::core::input::TrajectoryType;
using rug::core::input::HumanizeConfig;
using rug::core::input::TrajectorySample;
using rug::core::input::CreateInputController;

namespace {
// Opaque ABI handles are the underlying C++ instance pointers.
inline WgcCapturer* ToCapturer(RugCapturerHandle h) {
    return reinterpret_cast<WgcCapturer*>(h);
}
inline RugCapturerHandle FromCapturer(WgcCapturer* p) {
    return reinterpret_cast<RugCapturerHandle>(p);
}
inline IOcrEngine* ToEngine(RugOcrEngineHandle h) {
    return reinterpret_cast<IOcrEngine*>(h);
}
inline RugOcrEngineHandle FromEngine(IOcrEngine* p) {
    return reinterpret_cast<RugOcrEngineHandle>(p);
}
inline OcrResult* ToResult(RugOcrResultHandle h) {
    return reinterpret_cast<OcrResult*>(h);
}
inline RugOcrResultHandle FromResult(OcrResult* p) {
    return reinterpret_cast<RugOcrResultHandle>(p);
}
inline IInputController* ToInput(RugInputControllerHandle h) {
    return reinterpret_cast<IInputController*>(h);
}
inline RugInputControllerHandle FromInput(IInputController* p) {
    return reinterpret_cast<RugInputControllerHandle>(p);
}

// Map the blittable ABI struct onto the internal HumanizeConfig.
inline HumanizeConfig ToHumanizeConfig(const RugHumanizeConfig* c) {
    HumanizeConfig cfg;  // start from defaults, then overlay provided fields
    if (!c) return cfg;
    cfg.minClickHoldMs = c->minClickHoldMs;
    cfg.maxClickHoldMs = c->maxClickHoldMs;
    cfg.minKeyHoldMs   = c->minKeyHoldMs;
    cfg.maxKeyHoldMs   = c->maxKeyHoldMs;
    cfg.corridorRatio  = c->corridorRatio;
    cfg.enableJitter   = (c->enableJitter != 0);
    return cfg;
}

// Copy a wide string into a fixed UTF-8 buffer, truncating safely.
void CopyUtf8(const std::wstring& w, char* dst, size_t dstSize) {
    if (!dst || dstSize == 0) return;
    dst[0] = '\0';
    if (w.empty()) return;
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()),
                                dst, static_cast<int>(dstSize) - 1, nullptr, nullptr);
    if (n < 0) n = 0;
    dst[n] = '\0';
}

// Copy a UTF-8 std::string into a fixed char buffer, truncating safely.
void CopyStr(const std::string& s, char* dst, size_t dstSize) {
    if (!dst || dstSize == 0) return;
    size_t n = s.size();
    if (n > dstSize - 1) n = dstSize - 1;
    if (n > 0) std::memcpy(dst, s.c_str(), n);
    dst[n] = '\0';
}

// RugFrame -> ImageView. Returns false if the frame is not usable BGRA8.
bool FrameToView(const RugFrame* f, ImageView& v) {
    if (!f || !f->data || f->width <= 0 || f->height <= 0) return false;
    if (f->format != RUG_PIXEL_BGRA8) return false;
    v.data = f->data; v.width = f->width; v.height = f->height; v.stride = f->stride;
    return true;
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

// --- OCR ---------------------------------------------------------------------

RUGCORE_API int32_t RUGCORE_CALL Rug_CreateOcrEngine(int32_t engine_type,
                                                     const char* model_path,
                                                     RugOcrEngineHandle* out_handle) {
    if (!out_handle) return RUG_ERR_INVALID_PARAM;
    *out_handle = nullptr;

    try {
        std::unique_ptr<IOcrEngine> engine;
        int32_t st;
        switch (engine_type) {
            case RUG_OCR_ENGINE_WINRT:
                st = rug::core::WinRtOcrEngine::Create(model_path, engine);  // model_path = optional BCP-47 tag
                break;
            case RUG_OCR_ENGINE_PADDLE:
                st = rug::core::PaddleOcrEngine::Create(model_path, engine);
                break;
            default:
                return RUG_ERR_INVALID_PARAM;
        }
        if (st != RUG_OK) return st;

        *out_handle = FromEngine(engine.release());
        return RUG_OK;
    }
    catch (...) {
        return RUG_ERR_OCR_FAILED;  // never let an exception cross the ABI
    }
}

RUGCORE_API int32_t RUGCORE_CALL Rug_DestroyOcrEngine(RugOcrEngineHandle handle) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    try {
        std::unique_ptr<IOcrEngine> engine(ToEngine(handle));  // virtual dtor
        return RUG_OK;
    }
    catch (...) {
        return RUG_ERR_UNKNOWN;
    }
}

RUGCORE_API int32_t RUGCORE_CALL Rug_RecognizeText(RugOcrEngineHandle handle,
                                                   const RugFrame* frame,
                                                   RugOcrResultHandle* outResult) {
    if (!handle || !outResult) return RUG_ERR_INVALID_PARAM;
    *outResult = nullptr;

    ImageView view;
    if (!FrameToView(frame, view)) return RUG_ERR_INVALID_PARAM;

    OcrResult* result = new (std::nothrow) OcrResult();
    if (!result) return RUG_ERR_OUT_OF_MEMORY;

    try {
        int32_t st = ToEngine(handle)->Recognize(view, *result);
        if (st != RUG_OK) { delete result; return st; }
    }
    catch (...) {
        delete result;
        return RUG_ERR_OCR_FAILED;
    }

    *outResult = FromResult(result);  // ownership transfers; free via Rug_FreeOcrResult
    return RUG_OK;
}

RUGCORE_API int32_t RUGCORE_CALL Rug_OcrResultGetLineCount(RugOcrResultHandle result,
                                                           int32_t* outCount) {
    if (!result || !outCount) return RUG_ERR_INVALID_PARAM;
    *outCount = static_cast<int32_t>(ToResult(result)->lines.size());
    return RUG_OK;
}

RUGCORE_API int32_t RUGCORE_CALL Rug_OcrResultGetLine(RugOcrResultHandle result,
                                                      int32_t index,
                                                      RugOcrLine* outLine) {
    if (!result || !outLine || index < 0) return RUG_ERR_INVALID_PARAM;
    OcrResult* r = ToResult(result);
    if (index >= static_cast<int32_t>(r->lines.size())) return RUG_ERR_INVALID_PARAM;

    const rug::core::OcrLine& line = r->lines[static_cast<size_t>(index)];
    std::memset(outLine, 0, sizeof(*outLine));
    outLine->x      = line.box.x;
    outLine->y      = line.box.y;
    outLine->width  = line.box.width;
    outLine->height = line.box.height;
    outLine->confidence = line.confidence;
    CopyUtf8(line.text, outLine->text, RUG_OCR_LINE_TEXT_MAX);
    return RUG_OK;
}

RUGCORE_API void RUGCORE_CALL Rug_FreeOcrResult(RugOcrResultHandle result) {
    delete ToResult(result);  // delete nullptr is a no-op
}

// --- Model discovery ---------------------------------------------------------

RUGCORE_API int32_t RUGCORE_CALL Rug_ScanOcrModels(const char* ocr_dir,
                                                   RugModelListHandle* out_list) {
    if (!ocr_dir || !out_list) return RUG_ERR_INVALID_PARAM;
    *out_list = nullptr;
    try {
        using List = std::vector<rug::core::OcrModelInfo>;
        List* list = new (std::nothrow) List(rug::core::ScanOcrModels(ocr_dir));
        if (!list) return RUG_ERR_OUT_OF_MEMORY;
        *out_list = reinterpret_cast<RugModelListHandle>(list);
        return RUG_OK;
    }
    catch (...) {
        return RUG_ERR_UNKNOWN;
    }
}

RUGCORE_API int32_t RUGCORE_CALL Rug_ModelListGetCount(RugModelListHandle list,
                                                       int32_t* out_count) {
    if (!list || !out_count) return RUG_ERR_INVALID_PARAM;
    auto* v = reinterpret_cast<std::vector<rug::core::OcrModelInfo>*>(list);
    *out_count = static_cast<int32_t>(v->size());
    return RUG_OK;
}

RUGCORE_API int32_t RUGCORE_CALL Rug_ModelListGetInfo(RugModelListHandle list,
                                                      int32_t index,
                                                      RugModelInfo* out_info) {
    if (!list || !out_info || index < 0) return RUG_ERR_INVALID_PARAM;
    auto* v = reinterpret_cast<std::vector<rug::core::OcrModelInfo>*>(list);
    if (index >= static_cast<int32_t>(v->size())) return RUG_ERR_INVALID_PARAM;

    const rug::core::OcrModelInfo& m = (*v)[static_cast<size_t>(index)];
    std::memset(out_info, 0, sizeof(*out_info));
    CopyStr(m.id,      out_info->id,      RUG_MODEL_ID_MAX);
    CopyStr(m.version, out_info->version, RUG_MODEL_VERSION_MAX);
    CopyStr(m.engine,  out_info->engine,  RUG_MODEL_ENGINE_MAX);
    return RUG_OK;
}

RUGCORE_API void RUGCORE_CALL Rug_FreeModelList(RugModelListHandle list) {
    delete reinterpret_cast<std::vector<rug::core::OcrModelInfo>*>(list);
}

RUGCORE_API int32_t RUGCORE_CALL Rug_CreateOcrEngineById(const char* ocr_dir,
                                                         const char* id,
                                                         RugOcrEngineHandle* out_handle) {
    if (!ocr_dir || !id || !out_handle) return RUG_ERR_INVALID_PARAM;
    *out_handle = nullptr;
    try {
        rug::core::OcrModelInfo info;
        if (!rug::core::FindOcrModelById(ocr_dir, id, info)) return RUG_ERR_OCR_MODEL_NOT_FOUND;
        if (info.engine != "paddle") return RUG_ERR_UNSUPPORTED;  // only Paddle family today

        std::unique_ptr<IOcrEngine> engine;
        const int32_t st = rug::core::PaddleOcrEngine::Create(info.dirPath.c_str(), engine);
        if (st != RUG_OK) return st;

        *out_handle = FromEngine(engine.release());
        return RUG_OK;
    }
    catch (...) {
        return RUG_ERR_OCR_FAILED;
    }
}

// --- Template matching -------------------------------------------------------

RUGCORE_API int32_t RUGCORE_CALL Rug_MatchTemplate(const RugFrame* frame,
                                                   const char* template_path,
                                                   float threshold,
                                                   RugMatchBox* boxes,
                                                   int32_t* inout_count) {
    if (!inout_count || *inout_count < 0) return RUG_ERR_INVALID_PARAM;
    const int32_t capacity = *inout_count;
    if (capacity > 0 && !boxes) return RUG_ERR_INVALID_PARAM;

    ImageView view;
    if (!FrameToView(frame, view)) return RUG_ERR_INVALID_PARAM;
    *inout_count = 0;

    std::vector<MatchBox> matches;
    try {
        int32_t st = rug::core::ImageMatcher::MatchTemplate(view, template_path, threshold, matches);
        if (st != RUG_OK) return st;
    }
    catch (...) {
        return RUG_ERR_UNSUPPORTED;
    }

    const int32_t total = static_cast<int32_t>(matches.size());
    const int32_t write = (total < capacity) ? total : capacity;
    for (int32_t i = 0; i < write; ++i) {
        boxes[i].x          = matches[i].x;
        boxes[i].y          = matches[i].y;
        boxes[i].width      = matches[i].width;
        boxes[i].height     = matches[i].height;
        boxes[i].confidence = matches[i].confidence;
    }
    *inout_count = write;
    return (total > capacity) ? RUG_ERR_BUFFER_TOO_SMALL : RUG_OK;
}

// --- Image I/O ---------------------------------------------------------------

RUGCORE_API int32_t RUGCORE_CALL Rug_LoadImageFile(const char* path, RugFrame* outFrame) {
    if (!path || !outFrame) return RUG_ERR_INVALID_PARAM;
    std::memset(outFrame, 0, sizeof(*outFrame));
#ifndef RUG_HAS_OPENCV
    return RUG_ERR_UNSUPPORTED;
#else
    try {
        cv::Mat img = cv::imread(path, cv::IMREAD_COLOR);  // BGR 8UC3
        if (img.empty()) return RUG_ERR_INVALID_PARAM;     // missing / unreadable
        cv::Mat bgra;
        cv::cvtColor(img, bgra, cv::COLOR_BGR2BGRA);

        const int32_t w = bgra.cols, h = bgra.rows;
        const int32_t stride = w * 4;
        const uint32_t len = static_cast<uint32_t>(stride) * static_cast<uint32_t>(h);
        uint8_t* dst = new (std::nothrow) uint8_t[len];
        if (!dst) return RUG_ERR_OUT_OF_MEMORY;
        for (int y = 0; y < h; ++y)
            std::memcpy(dst + static_cast<size_t>(y) * stride, bgra.ptr(y), static_cast<size_t>(stride));

        outFrame->data       = dst;   // ownership transfers; free via Rug_FreeBuffer
        outFrame->dataLength = len;
        outFrame->width      = w;
        outFrame->height     = h;
        outFrame->stride     = stride;
        outFrame->format     = RUG_PIXEL_BGRA8;
        return RUG_OK;
    }
    catch (...) {
        return RUG_ERR_UNSUPPORTED;
    }
#endif
}

// --- Input -------------------------------------------------------------------

RUGCORE_API int32_t RUGCORE_CALL Rug_CreateInputController(int32_t mode,
                                                           void* hwnd,
                                                           RugInputControllerHandle* out_handle) {
    if (!out_handle) return RUG_ERR_INVALID_PARAM;
    *out_handle = nullptr;
    std::unique_ptr<IInputController> ctrl;
    int32_t rc = CreateInputController(static_cast<InputMode>(mode),
                                       static_cast<HWND>(hwnd), ctrl);
    if (rc != RUG_OK || !ctrl) return rc;
    *out_handle = FromInput(ctrl.release());
    return RUG_OK;
}

RUGCORE_API int32_t RUGCORE_CALL Rug_DestroyInputController(RugInputControllerHandle handle) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    delete ToInput(handle);
    return RUG_OK;
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_MouseMove(RugInputControllerHandle handle,
                                                     int32_t x, int32_t y,
                                                     int32_t trajectory, int32_t smooth) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    return ToInput(handle)->MouseMove(x, y, static_cast<TrajectoryType>(trajectory), smooth != 0);
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_MouseMoveRelative(RugInputControllerHandle handle,
                                                             int32_t dx, int32_t dy,
                                                             int32_t trajectory, int32_t smooth) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    return ToInput(handle)->MouseMoveRelative(dx, dy, static_cast<TrajectoryType>(trajectory), smooth != 0);
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_MouseDown(RugInputControllerHandle handle, int32_t button) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    return ToInput(handle)->MouseDown(static_cast<MouseButton>(button));
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_MouseUp(RugInputControllerHandle handle, int32_t button) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    return ToInput(handle)->MouseUp(static_cast<MouseButton>(button));
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_Click(RugInputControllerHandle handle,
                                                 int32_t button, int32_t holdTimeMs) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    return ToInput(handle)->Click(static_cast<MouseButton>(button), holdTimeMs);
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_DragAndDrop(RugInputControllerHandle handle,
                                                       int32_t sx, int32_t sy,
                                                       int32_t ex, int32_t ey,
                                                       int32_t trajectory, int32_t smooth) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    return ToInput(handle)->DragAndDrop(sx, sy, ex, ey, static_cast<TrajectoryType>(trajectory), smooth != 0);
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_KeyDown(RugInputControllerHandle handle, int32_t vk) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    return ToInput(handle)->KeyDown(vk);
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_KeyUp(RugInputControllerHandle handle, int32_t vk) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    return ToInput(handle)->KeyUp(vk);
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_KeyPress(RugInputControllerHandle handle,
                                                    int32_t vk, int32_t holdTimeMs) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    return ToInput(handle)->KeyPress(vk, holdTimeMs);
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_SendText(RugInputControllerHandle handle,
                                                    const char* utf8_text) {
    if (!handle || !utf8_text) return RUG_ERR_INVALID_PARAM;
    return ToInput(handle)->SendText(std::string(utf8_text));
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_SetTargetWindow(RugInputControllerHandle handle, void* hwnd) {
    if (!handle) return RUG_ERR_INVALID_PARAM;
    ToInput(handle)->SetTargetWindow(static_cast<HWND>(hwnd));
    return RUG_OK;
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_SetHumanizeConfig(RugInputControllerHandle handle,
                                                             const RugHumanizeConfig* config) {
    if (!handle || !config) return RUG_ERR_INVALID_PARAM;
    ToInput(handle)->SetHumanizeConfig(ToHumanizeConfig(config));
    return RUG_OK;
}

RUGCORE_API int32_t RUGCORE_CALL Rug_Input_PlanTrajectory(RugInputControllerHandle handle,
                                                          int32_t sx, int32_t sy,
                                                          int32_t ex, int32_t ey,
                                                          int32_t trajectory,
                                                          int32_t* xs, int32_t* ys,
                                                          float* delays,
                                                          int32_t* inout_count) {
    if (!handle || !inout_count) return RUG_ERR_INVALID_PARAM;
    const int32_t capacity = *inout_count;
    if (capacity < 0) return RUG_ERR_INVALID_PARAM;
    if (capacity > 0 && (!xs || !ys || !delays)) return RUG_ERR_INVALID_PARAM;

    std::vector<TrajectorySample> plan =
        ToInput(handle)->PlanTrajectory(sx, sy, ex, ey, static_cast<TrajectoryType>(trajectory));

    const int32_t total = static_cast<int32_t>(plan.size());
    const int32_t write = (total < capacity) ? total : capacity;
    for (int32_t i = 0; i < write; ++i) {
        xs[i]     = plan[i].x;
        ys[i]     = plan[i].y;
        delays[i] = plan[i].delayMs;
    }
    *inout_count = write;
    return (total > capacity) ? RUG_ERR_BUFFER_TOO_SMALL : RUG_OK;
}

}  // extern "C"
