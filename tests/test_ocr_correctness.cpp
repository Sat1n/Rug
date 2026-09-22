// =============================================================================
//  test_ocr_correctness.cpp — Task 2
//  Runs WinRT OCR and PP-OCRv6 over the ocr_*.png set and prints an aligned,
//  structured side-by-side comparison (per-line score, box, text).
// =============================================================================

#include "TestCommon.h"

#include <cstdio>
#include <string>

using namespace rugtest;

namespace {

void PrintEngineBlock(const char* label, RugOcrEngineHandle engine,
                      int32_t createRc, const RugFrame& frame) {
    std::printf("--------------------------------------------------\n");
    if (!engine) {
        std::printf("> [Engine]: %s | [Status]: unavailable (create code %d)\n", label, createRc);
        return;
    }

    RugOcrResultHandle result = nullptr;
    const auto t0 = Clock::now();
    const int32_t rc = Rug_RecognizeText(engine, &frame, &result);
    const auto t1 = Clock::now();
    const double ms = MsBetween(t0, t1);

    if (rc != RUG_OK || !result) {
        std::printf("> [Engine]: %s | [Time]: %.1f ms | [Status]: FAILED (code %d)\n", label, ms, rc);
        if (result) Rug_FreeOcrResult(result);
        return;
    }

    int32_t count = 0;
    Rug_OcrResultGetLineCount(result, &count);
    std::printf("> [Engine]: %s | [Time]: %.1f ms | [Count]: %d blocks\n", label, ms, count);

    for (int32_t k = 0; k < count; ++k) {
        RugOcrLine ln{};
        if (Rug_OcrResultGetLine(result, k, &ln) != RUG_OK) continue;
        std::printf("  #%d Score: %.2f | Box: [x=%d, y=%d, w=%d, h=%d] | Text: \"%s\"\n",
                    k + 1, ln.confidence, ln.x, ln.y, ln.width, ln.height, ln.text);
    }
    Rug_FreeOcrResult(result);
}

}  // namespace

int rugtest::RunOcrCorrectness() {
    const std::vector<std::wstring> images = FindOcrImages();
    if (images.empty()) {
        std::printf("[ERROR] No OCR test images found.\n");
        std::printf("        Place ocr_test_1.png .. ocr_test_5.png (any ocr_*.png) into:\n");
        std::printf("        %s\n", ToUtf8(TestImagesDir()).c_str());
        return 2;
    }

    RugOcrEngineHandle winrt = nullptr;
    RugOcrEngineHandle paddle = nullptr;
    const int32_t rcWin = Rug_CreateOcrEngine(RUG_OCR_ENGINE_WINRT, nullptr, &winrt);
    const std::string modelUtf8 = ToUtf8(OcrModelDir());
    const int32_t rcPad = Rug_CreateOcrEngine(RUG_OCR_ENGINE_PADDLE, modelUtf8.c_str(), &paddle);

    std::printf("Engines : WinRT OCR = %s (code %d) | PP-OCRv6 = %s (code %d)\n",
                rcWin == RUG_OK ? "ready" : "unavailable", rcWin,
                rcPad == RUG_OK ? "ready" : "unavailable", rcPad);
    std::printf("Model   : %s\n", modelUtf8.c_str());
    std::printf("Images  : %zu file(s) matching ocr_*.png\n\n", images.size());

    const int total = static_cast<int>(images.size());
    for (int i = 0; i < total; ++i) {
        const std::string name = ToUtf8(FileName(images[i]));
        std::printf("==================================================\n");
        std::printf("[Image %d/%d]: %s\n", i + 1, total, name.c_str());

        FrameBuf fb;
        if (!LoadImageToFrame(images[i], fb)) {
            std::printf("  [ERROR] failed to decode PNG\n");
            std::printf("==================================================\n\n");
            continue;
        }
        std::printf("  (decoded %dx%d, BGRA8)\n", fb.frame.width, fb.frame.height);

        PrintEngineBlock("WinRT OCR", winrt, rcWin, fb.frame);
        PrintEngineBlock("PP-OCRv6", paddle, rcPad, fb.frame);

        std::printf("==================================================\n\n");
    }

    if (winrt)  Rug_DestroyOcrEngine(winrt);
    if (paddle) Rug_DestroyOcrEngine(paddle);
    return 0;
}
