// =============================================================================
//  PaddleOcrEngine.cpp — PP-OCRv5/v6 ONNX Runtime back-end (Task 1.3.1).
//
//  Two-stage pipeline (Det -> Rec) following the conventional PaddleOCR ONNX C++
//  recipe. All model-specific parameters and the CTC dictionary are read from the
//  PaddleX-exported sidecar configs (det.yml / rec.yml), so swapping model
//  versions needs no code change.
//
//    Det : resize (limit max side, pad to /32) -> BGR NormalizeImage(mean/std)
//          -> NCHW -> DBNet probability map -> binarize -> findContours ->
//          minAreaRect -> box score -> unclip -> scale back to source pixels.
//    Rec : perspective-crop each box -> resize to image_shape height ->
//          (x/255-0.5)/0.5 -> NCHW -> CTC greedy decode -> CompactText.
//
//  model_path is a DIRECTORY containing: det.onnx, rec.onnx, det.yml, rec.yml.
//
//  Without RUG_HAS_ONNX + RUG_HAS_OPENCV this TU compiles with only the C++
//  standard library and fails gracefully (RUG_ERR_OCR_MODEL_NOT_FOUND when the
//  model dir is missing, RUG_ERR_UNSUPPORTED when the back-end is not built in).
// =============================================================================

#include "pch.h"
#include "PaddleOcrEngine.h"
#include "RugCoreAbi.h"   // RugStatus codes
#include "OcrTextUtils.h" // shared CompactText

#include <algorithm>
#include <cmath>
#include <filesystem>
#include <string>
#include <vector>

#if defined(RUG_HAS_ONNX) && defined(RUG_HAS_OPENCV)
#define RUG_PADDLE_ACTIVE 1
#endif

#if RUG_PADDLE_ACTIVE
#include <onnxruntime_cxx_api.h>
#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>
#pragma warning(push)
#pragma warning(disable : 4251 4275)  // yaml-cpp DLL-interface warnings (benign for consumers)
#include <yaml-cpp/yaml.h>
#pragma warning(pop)
#pragma comment(lib, "onnxruntime.lib")
#endif

namespace rug::core {

#if RUG_PADDLE_ACTIVE
namespace {

// Fallbacks used only when a field is absent from the sidecar yml.
constexpr float kMinBoxSide        = 3.0f;
constexpr int   kDefDetMaxSide     = 960;
constexpr int   kDefDetResizeMul   = 32;
constexpr float kDefDetBinThresh   = 0.20f;
constexpr float kDefDetBoxThresh   = 0.40f;
constexpr float kDefDetUnclipRatio = 1.40f;
constexpr int   kDefDetMaxCand     = 3000;
constexpr int   kDefRecImgH        = 48;
constexpr int   kDefRecMaxW        = 3200;

struct DetParams {
    float mean[3] = { 0.485f, 0.456f, 0.406f };  // applied in stored BGR channel order
    float sd[3]   = { 0.229f, 0.224f, 0.225f };
    float scale   = 1.f / 255.f;
    int   maxSide = kDefDetMaxSide;
    int   resizeMul = kDefDetResizeMul;
    float binThresh = kDefDetBinThresh;
    float boxThresh = kDefDetBoxThresh;
    float unclipRatio = kDefDetUnclipRatio;
    int   maxCandidates = kDefDetMaxCand;
};

std::wstring PathToWide(const char* utf8) {
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
    if (n <= 0) return {};
    std::wstring w(static_cast<size_t>(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, w.data(), n);
    return w;
}

std::wstring Utf8ToWide(const std::string& s) {
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0);
    if (n <= 0) return {};
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

// A YAML scalar that may be a number or an expression string like "1./255.".
float AsFloatOr(const YAML::Node& n, float fallback) {
    if (!n || !n.IsScalar()) return fallback;
    try { return n.as<float>(); }
    catch (...) { return fallback; }
}

// det.yml -> DetParams (NormalizeImage + DetResizeForTest + DBPostProcess).
void ParseDetYaml(const std::string& path, DetParams& p) {
    YAML::Node root = YAML::LoadFile(path);

    if (root["PreProcess"] && root["PreProcess"]["transform_ops"]) {
        for (const auto& op : root["PreProcess"]["transform_ops"]) {
            for (YAML::const_iterator it = op.begin(); it != op.end(); ++it) {
                const std::string name = it->first.as<std::string>();
                const YAML::Node& v = it->second;
                if (name == "NormalizeImage") {
                    if (v["mean"] && v["mean"].IsSequence() && v["mean"].size() >= 3)
                        for (int i = 0; i < 3; ++i) p.mean[i] = v["mean"][i].as<float>();
                    if (v["std"] && v["std"].IsSequence() && v["std"].size() >= 3)
                        for (int i = 0; i < 3; ++i) p.sd[i] = v["std"][i].as<float>();
                    p.scale = AsFloatOr(v["scale"], 1.f / 255.f);
                }
                else if (name == "DetResizeForTest" && v && v.IsMap()) {
                    if (v["limit_side_len"]) p.maxSide = v["limit_side_len"].as<int>();
                    if (v["resize_long"])    p.maxSide = v["resize_long"].as<int>();
                }
            }
        }
    }
    if (root["PostProcess"]) {
        const YAML::Node& pp = root["PostProcess"];
        if (pp["thresh"])          p.binThresh     = pp["thresh"].as<float>();
        if (pp["box_thresh"])      p.boxThresh     = pp["box_thresh"].as<float>();
        if (pp["unclip_ratio"])    p.unclipRatio   = pp["unclip_ratio"].as<float>();
        if (pp["max_candidates"])  p.maxCandidates = pp["max_candidates"].as<int>();
    }
}

// rec.yml -> dictionary (character_dict) + recognition height (image_shape[1]).
bool ParseRecYaml(const std::string& path, std::vector<std::string>& keys, int& imgH) {
    YAML::Node root = YAML::LoadFile(path);

    if (root["PostProcess"] && root["PostProcess"]["character_dict"]) {
        const YAML::Node& d = root["PostProcess"]["character_dict"];
        keys.clear();
        keys.reserve(d.size());
        for (const auto& c : d) keys.push_back(c.as<std::string>());
    }
    if (root["PreProcess"] && root["PreProcess"]["transform_ops"]) {
        for (const auto& op : root["PreProcess"]["transform_ops"]) {
            for (YAML::const_iterator it = op.begin(); it != op.end(); ++it) {
                if (it->first.as<std::string>() == "RecResizeImg") {
                    const YAML::Node is = it->second["image_shape"];
                    if (is && is.IsSequence() && is.size() >= 2) imgH = is[1].as<int>();
                }
            }
        }
    }
    return !keys.empty();
}

std::vector<float> RunSession(Ort::Session& session,
                              const std::string& inName,
                              const std::string& outName,
                              std::vector<float>& blob,
                              const std::vector<int64_t>& shape,
                              std::vector<int64_t>& outShape) {
    Ort::MemoryInfo mem = Ort::MemoryInfo::CreateCpu(OrtArenaAllocator, OrtMemTypeDefault);
    Ort::Value input = Ort::Value::CreateTensor<float>(
        mem, blob.data(), blob.size() * sizeof(float), shape.data(), shape.size());

    const char* inNames[]  = { inName.c_str() };
    const char* outNames[] = { outName.c_str() };
    auto outputs = session.Run(Ort::RunOptions{ nullptr }, inNames, &input, 1, outNames, 1);

    outShape = outputs[0].GetTensorTypeAndShapeInfo().GetShape();
    size_t count = 1;
    for (int64_t d : outShape) count *= static_cast<size_t>(d);
    const float* data = outputs[0].GetTensorData<float>();
    return std::vector<float>(data, data + count);
}

// BGR 8UC3 -> scale, NormalizeImage(mean/std in stored BGR order), NCHW float.
std::vector<float> DetBlob(const cv::Mat& img, const DetParams& p) {
    const int h = img.rows, w = img.cols;
    const size_t planeHW = static_cast<size_t>(h) * w;
    std::vector<float> blob(3 * planeHW);
    for (int y = 0; y < h; ++y) {
        const cv::Vec3b* row = img.ptr<cv::Vec3b>(y);
        for (int x = 0; x < w; ++x) {
            const cv::Vec3b& px = row[x];  // B, G, R (stored order)
            const size_t idx = static_cast<size_t>(y) * w + x;
            for (int c = 0; c < 3; ++c) {
                const float v = px[c] * p.scale;
                blob[static_cast<size_t>(c) * planeHW + idx] = (v - p.mean[c]) / p.sd[c];
            }
        }
    }
    return blob;
}

float BoxScore(const cv::Mat& probMap, const std::vector<cv::Point>& cont) {
    cv::Rect r = cv::boundingRect(cont) & cv::Rect(0, 0, probMap.cols, probMap.rows);
    if (r.width <= 0 || r.height <= 0) return 0.f;
    cv::Mat mask = cv::Mat::zeros(r.height, r.width, CV_8UC1);
    std::vector<cv::Point> shifted;
    shifted.reserve(cont.size());
    for (const cv::Point& p : cont) shifted.emplace_back(p.x - r.x, p.y - r.y);
    cv::fillPoly(mask, std::vector<std::vector<cv::Point>>{ shifted }, cv::Scalar(1));
    return static_cast<float>(cv::mean(probMap(r), mask)[0]);
}

// Order 4 points as top-left, top-right, bottom-right, bottom-left.
void OrderPoints(cv::Point2f pts[4]) {
    cv::Point2f o[4];
    float minS = 1e18f, maxS = -1e18f, minD = 1e18f, maxD = -1e18f;
    for (int i = 0; i < 4; ++i) {
        const float s = pts[i].x + pts[i].y;
        const float d = pts[i].x - pts[i].y;
        if (s < minS) { minS = s; o[0] = pts[i]; }
        if (s > maxS) { maxS = s; o[2] = pts[i]; }
        if (d > maxD) { maxD = d; o[1] = pts[i]; }
        if (d < minD) { minD = d; o[3] = pts[i]; }
    }
    for (int i = 0; i < 4; ++i) pts[i] = o[i];
}

cv::Mat GetRotateCropImage(const cv::Mat& src, cv::RotatedRect rr) {
    cv::Point2f pts[4];
    rr.points(pts);
    OrderPoints(pts);

    const int cropW = static_cast<int>(std::round(
        (std::max)(cv::norm(pts[1] - pts[0]), cv::norm(pts[2] - pts[3]))));
    const int cropH = static_cast<int>(std::round(
        (std::max)(cv::norm(pts[3] - pts[0]), cv::norm(pts[2] - pts[1]))));
    if (cropW < 1 || cropH < 1) return {};

    cv::Point2f dst[4] = {
        { 0.f, 0.f }, { static_cast<float>(cropW), 0.f },
        { static_cast<float>(cropW), static_cast<float>(cropH) }, { 0.f, static_cast<float>(cropH) }
    };
    cv::Mat M = cv::getPerspectiveTransform(pts, dst);
    cv::Mat crop;
    cv::warpPerspective(src, crop, M, cv::Size(cropW, cropH),
                        cv::INTER_LINEAR, cv::BORDER_REPLICATE);
    if (static_cast<float>(crop.rows) / crop.cols >= 1.5f)
        cv::rotate(crop, crop, cv::ROTATE_90_CLOCKWISE);
    return crop;
}

// CTC greedy decode. Index 0 = blank, 1..N = keys, N+1 = space (when present).
void CtcDecode(const float* logits, int T, int C,
               const std::vector<std::string>& keys,
               std::string& text, float& conf) {
    text.clear();
    conf = 0.f;
    int last = -1, cnt = 0;
    float sum = 0.f;
    for (int t = 0; t < T; ++t) {
        const float* row = logits + static_cast<size_t>(t) * C;
        int am = 0;
        float mx = row[0];
        for (int c = 1; c < C; ++c) {
            if (row[c] > mx) { mx = row[c]; am = c; }
        }
        if (am != 0 && am != last) {
            if (am - 1 < static_cast<int>(keys.size()))       text += keys[am - 1];
            else if (am - 1 == static_cast<int>(keys.size())) text += " ";
            sum += mx;
            ++cnt;
        }
        last = am;
    }
    if (cnt > 0) conf = sum / cnt;
}

}  // namespace
#endif  // RUG_PADDLE_ACTIVE

// -----------------------------------------------------------------------------
struct PaddleOcrEngine::Impl {
    std::string modelDir;
#if RUG_PADDLE_ACTIVE
    Ort::Env                      env{ ORT_LOGGING_LEVEL_WARNING, "rug.paddle" };
    Ort::SessionOptions           options;
    std::unique_ptr<Ort::Session> det;
    std::unique_ptr<Ort::Session> rec;
    std::string detInput, detOutput, recInput, recOutput;

    std::vector<std::string> keys;  // CTC dictionary from rec.yml
    DetParams detParams;
    int recImgH = kDefRecImgH;
    int recMaxW = kDefRecMaxW;
#endif
};

PaddleOcrEngine::PaddleOcrEngine(std::string modelPath)
    : m_impl(std::make_unique<Impl>()) {
    m_impl->modelDir = std::move(modelPath);
}

PaddleOcrEngine::~PaddleOcrEngine() = default;

int32_t PaddleOcrEngine::Create(const char* modelPath, std::unique_ptr<IOcrEngine>& out) {
    if (!modelPath || modelPath[0] == '\0') return RUG_ERR_OCR_MODEL_NOT_FOUND;

    std::error_code ec;
    if (!std::filesystem::exists(modelPath, ec)) return RUG_ERR_OCR_MODEL_NOT_FOUND;

#if !RUG_PADDLE_ACTIVE
    (void)out;  // ONNX Runtime and/or OpenCV not wired into this build.
    return RUG_ERR_UNSUPPORTED;
#else
    namespace fs = std::filesystem;
    if (!fs::is_directory(modelPath, ec)) return RUG_ERR_OCR_MODEL_NOT_FOUND;

    const fs::path dir(modelPath);
    if (!fs::exists(dir / "det.onnx", ec) || !fs::exists(dir / "rec.onnx", ec) ||
        !fs::exists(dir / "det.yml", ec)  || !fs::exists(dir / "rec.yml", ec))
        return RUG_ERR_OCR_MODEL_NOT_FOUND;

    try {
        std::unique_ptr<PaddleOcrEngine> created(new PaddleOcrEngine(modelPath));
        Impl& im = *created->m_impl;

        // Sidecar configs are authoritative for params + dictionary.
        ParseDetYaml((dir / "det.yml").string(), im.detParams);
        if (!ParseRecYaml((dir / "rec.yml").string(), im.keys, im.recImgH))
            return RUG_ERR_OCR_MODEL_NOT_FOUND;  // empty/invalid dictionary

        im.options.SetIntraOpNumThreads(1);
        im.options.SetGraphOptimizationLevel(GraphOptimizationLevel::ORT_ENABLE_ALL);

        const std::wstring wdir = PathToWide(modelPath);
        im.det = std::make_unique<Ort::Session>(im.env, (wdir + L"\\det.onnx").c_str(), im.options);
        im.rec = std::make_unique<Ort::Session>(im.env, (wdir + L"\\rec.onnx").c_str(), im.options);

        Ort::AllocatorWithDefaultOptions alloc;
        im.detInput  = im.det->GetInputNameAllocated(0, alloc).get();
        im.detOutput = im.det->GetOutputNameAllocated(0, alloc).get();
        im.recInput  = im.rec->GetInputNameAllocated(0, alloc).get();
        im.recOutput = im.rec->GetOutputNameAllocated(0, alloc).get();

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
#if !RUG_PADDLE_ACTIVE
    return RUG_ERR_UNSUPPORTED;
#else
    if (!m_impl || !m_impl->det || !m_impl->rec)             return RUG_ERR_NOT_INITIALIZED;
    if (!image.data || image.width <= 0 || image.height <= 0) return RUG_ERR_INVALID_PARAM;

    try {
        Impl& im = *m_impl;
        const DetParams& dp = im.detParams;

        cv::Mat bgra(image.height, image.width, CV_8UC4,
                     const_cast<uint8_t*>(image.data), static_cast<size_t>(image.stride));
        cv::Mat bgr;
        cv::cvtColor(bgra, bgr, cv::COLOR_BGRA2BGR);

        // --- Detection -------------------------------------------------------
        const int ow = bgr.cols, oh = bgr.rows;
        float ratio = 1.0f;
        const int maxSide = (std::max)(ow, oh);
        if (maxSide > dp.maxSide) ratio = static_cast<float>(dp.maxSide) / maxSide;
        const int mul = (std::max)(1, dp.resizeMul);
        const int rw = (std::max)(mul, static_cast<int>(std::round(ow * ratio / mul) * mul));
        const int rh = (std::max)(mul, static_cast<int>(std::round(oh * ratio / mul) * mul));

        cv::Mat resized;
        cv::resize(bgr, resized, cv::Size(rw, rh));
        std::vector<float> detBlob = DetBlob(resized, dp);
        std::vector<int64_t> detShape{ 1, 3, rh, rw }, detOutShape;
        std::vector<float> detOut = RunSession(*im.det, im.detInput, im.detOutput,
                                               detBlob, detShape, detOutShape);
        if (detOutShape.size() != 4) return RUG_ERR_OCR_FAILED;
        const int mapH = static_cast<int>(detOutShape[2]);
        const int mapW = static_cast<int>(detOutShape[3]);

        cv::Mat probMap(mapH, mapW, CV_32FC1, detOut.data());
        cv::Mat bin, bin8;
        cv::threshold(probMap, bin, dp.binThresh, 255.0, cv::THRESH_BINARY);
        bin.convertTo(bin8, CV_8UC1);

        std::vector<std::vector<cv::Point>> contours;
        cv::findContours(bin8, contours, cv::RETR_LIST, cv::CHAIN_APPROX_SIMPLE);

        // Respect max_candidates: keep the largest contours.
        if (dp.maxCandidates > 0 && static_cast<int>(contours.size()) > dp.maxCandidates) {
            std::partial_sort(contours.begin(), contours.begin() + dp.maxCandidates, contours.end(),
                              [](const std::vector<cv::Point>& a, const std::vector<cv::Point>& b) {
                                  return cv::contourArea(a) > cv::contourArea(b);
                              });
            contours.resize(dp.maxCandidates);
        }

        const float sx = static_cast<float>(ow) / mapW;
        const float sy = static_cast<float>(oh) / mapH;

        std::vector<cv::RotatedRect> boxes;
        for (const auto& cont : contours) {
            if (cont.size() < 4) continue;
            cv::RotatedRect rr = cv::minAreaRect(cont);
            if (rr.size.width < kMinBoxSide || rr.size.height < kMinBoxSide) continue;
            if (BoxScore(probMap, cont) < dp.boxThresh) continue;

            const float area = rr.size.width * rr.size.height;
            const float peri = 2.f * (rr.size.width + rr.size.height);
            const float offset = (peri > 0.f) ? area * dp.unclipRatio / peri : 0.f;
            rr.size.width  += 2.f * offset;
            rr.size.height += 2.f * offset;

            rr.center.x    *= sx;
            rr.center.y    *= sy;
            rr.size.width  *= sx;
            rr.size.height *= sy;
            if (rr.size.width < kMinBoxSide || rr.size.height < kMinBoxSide) continue;
            boxes.push_back(rr);
        }

        std::sort(boxes.begin(), boxes.end(),
                  [](const cv::RotatedRect& a, const cv::RotatedRect& b) {
                      if (std::abs(a.center.y - b.center.y) < 10.f) return a.center.x < b.center.x;
                      return a.center.y < b.center.y;
                  });

        // --- Recognition -----------------------------------------------------
        out.lines.clear();
        out.lines.reserve(boxes.size());
        for (const cv::RotatedRect& rr : boxes) {
            cv::Mat crop = GetRotateCropImage(bgr, rr);
            if (crop.empty() || crop.cols < 1 || crop.rows < 1) continue;

            int resizedW = static_cast<int>(std::ceil(
                static_cast<float>(im.recImgH) * crop.cols / crop.rows));
            resizedW = (std::max)(1, (std::min)(resizedW, im.recMaxW));

            cv::Mat recImg;
            cv::resize(crop, recImg, cv::Size(resizedW, im.recImgH));

            std::vector<float> recBlob(static_cast<size_t>(3) * im.recImgH * resizedW, 0.f);
            const size_t planeHW = static_cast<size_t>(im.recImgH) * resizedW;
            for (int c = 0; c < 3; ++c) {
                for (int y = 0; y < im.recImgH; ++y) {
                    const cv::Vec3b* row = recImg.ptr<cv::Vec3b>(y);
                    for (int x = 0; x < resizedW; ++x) {
                        const float v = row[x][c] / 255.f;  // BGR order, no swap
                        recBlob[static_cast<size_t>(c) * planeHW +
                                static_cast<size_t>(y) * resizedW + x] = (v - 0.5f) / 0.5f;
                    }
                }
            }

            std::vector<int64_t> recShape{ 1, 3, im.recImgH, resizedW }, recOutShape;
            std::vector<float> recOut = RunSession(*im.rec, im.recInput, im.recOutput,
                                                   recBlob, recShape, recOutShape);
            if (recOutShape.size() != 3) continue;
            const int T = static_cast<int>(recOutShape[1]);
            const int C = static_cast<int>(recOutShape[2]);
            if (T <= 0 || C <= 0) continue;

            std::string utf8;
            float conf = 0.f;
            CtcDecode(recOut.data(), T, C, im.keys, utf8, conf);
            if (utf8.empty()) continue;

            cv::Point2f pts[4];
            rr.points(pts);
            float minx = 1e9f, miny = 1e9f, maxx = -1e9f, maxy = -1e9f;
            for (int i = 0; i < 4; ++i) {
                minx = (std::min)(minx, pts[i].x); maxx = (std::max)(maxx, pts[i].x);
                miny = (std::min)(miny, pts[i].y); maxy = (std::max)(maxy, pts[i].y);
            }

            OcrLine line;
            line.box.x      = static_cast<int32_t>(minx);
            line.box.y      = static_cast<int32_t>(miny);
            line.box.width  = static_cast<int32_t>(maxx - minx);
            line.box.height = static_cast<int32_t>(maxy - miny);
            line.text       = CompactText(Utf8ToWide(utf8));
            line.confidence = conf;
            out.lines.push_back(std::move(line));
        }
        return RUG_OK;
    }
    catch (...) {
        return RUG_ERR_OCR_FAILED;
    }
#endif
}

}  // namespace rug::core
