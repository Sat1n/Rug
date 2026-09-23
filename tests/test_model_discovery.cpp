// =============================================================================
//  test_model_discovery.cpp — model auto-discovery + model.json routing
//  Scans models/ocr via the C-ABI, lists discovered bundles, and smoke-tests
//  Rug_CreateOcrEngineById on every discovered model (plus an unknown-id case).
// =============================================================================

#include "TestCommon.h"

#include <cstdio>
#include <string>

using namespace rugtest;

int rugtest::RunModelDiscovery() {
    const std::string dir = ToUtf8(OcrCategoryDir());
    std::printf("Scanning OCR model bundles in: %s\n", dir.c_str());

    RugModelListHandle list = nullptr;
    const int32_t rc = Rug_ScanOcrModels(dir.c_str(), &list);
    if (rc != RUG_OK || !list) {
        std::printf("[ERROR] Rug_ScanOcrModels failed (code %d)\n", rc);
        return 2;
    }

    int32_t count = 0;
    Rug_ModelListGetCount(list, &count);
    std::printf("Discovered %d model(s):\n", count);
    for (int32_t i = 0; i < count; ++i) {
        RugModelInfo info{};
        if (Rug_ModelListGetInfo(list, i, &info) != RUG_OK) continue;
        std::printf("  [%d] id=%-20s engine=%-8s version=%s\n", i, info.id, info.engine, info.version);
    }

    if (count == 0) {
        std::printf("[WARN] No bundles found. A bundle needs model.json (kind=ocr,\n"
                    "       engine=paddle) plus det.onnx/rec.onnx/det.yml/rec.yml.\n");
        Rug_FreeModelList(list);
        return 0;
    }

    // Smoke test: create + destroy EVERY discovered model, reporting each.
    int failures = 0;
    for (int32_t i = 0; i < count; ++i) {
        RugModelInfo info{};
        if (Rug_ModelListGetInfo(list, i, &info) != RUG_OK) continue;
        RugOcrEngineHandle engine = nullptr;
        const int32_t crc = Rug_CreateOcrEngineById(dir.c_str(), info.id, &engine);
        std::printf("  CreateById(\"%s\") -> code %d (%s)\n",
                    info.id, crc, engine ? "ready" : "FAILED");
        if (engine) Rug_DestroyOcrEngine(engine);
        if (crc != RUG_OK) ++failures;
    }

    // Negative case: an unknown id must report model-not-found.
    RugOcrEngineHandle bogus = nullptr;
    const int32_t brc = Rug_CreateOcrEngineById(dir.c_str(), "__no_such_model__", &bogus);
    std::printf("  CreateById(\"__no_such_model__\") -> code %d (expect %d)\n",
                brc, RUG_ERR_OCR_MODEL_NOT_FOUND);

    Rug_FreeModelList(list);
    return (failures == 0 && brc == RUG_ERR_OCR_MODEL_NOT_FOUND) ? 0 : 1;
}
