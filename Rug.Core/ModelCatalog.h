// =============================================================================
//  ModelCatalog.h
//  Discovery of OCR model bundles under a category directory (e.g. models/ocr).
//
//  A bundle is an immediate subdirectory containing a model.json that declares
//  kind="ocr" and a supported engine, plus the conventional model files. This is
//  what lets users drop in extra Paddle models (v6 tiny/small/medium, v5, ...)
//  and select them by id without any code change.
// =============================================================================

#ifndef MODEL_CATALOG_H
#define MODEL_CATALOG_H
#pragma once

#include <string>
#include <vector>

namespace rug::core {

struct OcrModelInfo {
    std::string id;        // unique selector from model.json
    std::string engine;    // back-end key ("paddle")
    std::string version;   // informational (e.g. "6.0")
    std::string dirPath;   // resolved bundle directory
};

// Scan `ocrDir` for valid bundles. Directories without a valid model.json, with
// an unsupported engine, or missing required files are skipped. Sorted by id.
std::vector<OcrModelInfo> ScanOcrModels(const std::string& ocrDir);

// Find a bundle by id under `ocrDir`; returns true and fills `out` when found.
bool FindOcrModelById(const std::string& ocrDir, const std::string& id, OcrModelInfo& out);

}  // namespace rug::core

#endif  // MODEL_CATALOG_H
