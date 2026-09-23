// =============================================================================
//  ModelCatalog.cpp — OCR model bundle discovery (model.json driven).
//  model.json is parsed with yaml-cpp (JSON is a YAML subset), so no extra
//  dependency is introduced beyond what PaddleOcrEngine already uses.
// =============================================================================

#include "pch.h"
#include "ModelCatalog.h"

#include <algorithm>
#include <filesystem>

#pragma warning(push)
#pragma warning(disable : 4251 4275)  // yaml-cpp DLL-interface warnings (benign)
#include <yaml-cpp/yaml.h>
#pragma warning(pop)

namespace fs = std::filesystem;

namespace rug::core {
namespace {

// Conventional file layout a Paddle bundle must provide (see models/README.md).
constexpr const char* kRequiredFiles[] = { "det.onnx", "rec.onnx", "det.yml", "rec.yml" };

bool HasRequiredFiles(const fs::path& dir) {
    std::error_code ec;
    for (const char* f : kRequiredFiles)
        if (!fs::exists(dir / f, ec)) return false;
    return true;
}

// Parse model.json. Accepts only kind="ocr" bundles with the supported engine.
bool ParseModelJson(const fs::path& jsonPath, OcrModelInfo& info) {
    try {
        YAML::Node root = YAML::LoadFile(jsonPath.string());
        if (!root["id"] || !root["engine"]) return false;

        if (root["kind"]) {
            const std::string kind = root["kind"].as<std::string>();
            if (kind != "ocr") return false;
        }

        info.id      = root["id"].as<std::string>();
        info.engine  = root["engine"].as<std::string>();
        if (root["version"]) info.version = root["version"].as<std::string>();

        if (info.id.empty())   return false;
        if (info.engine != "paddle") return false;  // only the Paddle family today
        return true;
    }
    catch (...) {
        return false;
    }
}

}  // namespace

std::vector<OcrModelInfo> ScanOcrModels(const std::string& ocrDir) {
    std::vector<OcrModelInfo> result;
    std::error_code ec;
    const fs::path root(ocrDir);
    if (!fs::is_directory(root, ec)) return result;

    for (const auto& entry : fs::directory_iterator(root, ec)) {
        if (!entry.is_directory()) continue;
        const fs::path dir  = entry.path();
        const fs::path json = dir / "model.json";
        if (!fs::exists(json, ec)) continue;

        OcrModelInfo info;
        if (!ParseModelJson(json, info)) continue;   // invalid/unsupported -> skip
        if (!HasRequiredFiles(dir))      continue;   // not downloaded yet -> skip

        info.dirPath = dir.string();
        result.push_back(std::move(info));
    }

    std::sort(result.begin(), result.end(),
              [](const OcrModelInfo& a, const OcrModelInfo& b) { return a.id < b.id; });
    return result;
}

bool FindOcrModelById(const std::string& ocrDir, const std::string& id, OcrModelInfo& out) {
    if (id.empty()) return false;
    for (const auto& m : ScanOcrModels(ocrDir)) {
        if (m.id == id) { out = m; return true; }
    }
    return false;
}

}  // namespace rug::core
