// =============================================================================
//  test_ocr_correctness.cpp — Task 2 (engine-selectable)
//  Runs the user-selected OCR engine (WinRT or a discovered Paddle model) over
//  the ocr_* image set and prints an aligned, structured per-line report.
// =============================================================================

#include "TestCommon.h"

#include <cstdio>
#include <string>

using namespace rugtest;

namespace {

void PrintEngineBlock(const char* label, RugOcrEngineHandle engine, const RugFrame& frame) {
    std::printf("--------------------------------------------------\n");

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

int rugtest::RunOcrCorrectness(int engineIndex) {
    const std::vector<std::wstring> images = FindOcrImages();
    if (images.empty()) {
        std::printf("[ERROR] No OCR test images found.\n");
        std::printf("        Place ocr_test_1.png .. ocr_test_5.png (any ocr_*.png/.jpg) into:\n");
        std::printf("        %s\n", ToUtf8(TestImagesDir()).c_str());
        return 2;
    }

    const std::vector<EngineChoice> choices = BuildEngineChoices();
    if (engineIndex < 0 || engineIndex >= static_cast<int>(choices.size())) engineIndex = 0;
    const EngineChoice& choice = choices[engineIndex];

    int32_t rc = 0;
    RugOcrEngineHandle engine = CreateChosenEngine(choice, rc);
    const std::string label = ToUtf8(choice.label);
    std::printf("Engine: %s | create=%s (code %d)\n", label.c_str(), engine ? "ok" : "FAIL", rc);
    if (!engine) {
        std::printf("[ERROR] selected engine unavailable (code %d)\n", rc);
        return 2;
    }
    std::printf("Images: %zu file(s) matching ocr_*\n\n", images.size());

    const int total = static_cast<int>(images.size());
    for (int i = 0; i < total; ++i) {
        const std::string name = ToUtf8(FileName(images[i]));
        std::printf("==================================================\n");
        std::printf("[Image %d/%d]: %s\n", i + 1, total, name.c_str());

        FrameBuf fb;
        if (!LoadImageToFrame(images[i], fb)) {
            std::printf("  [ERROR] failed to decode image\n");
            std::printf("==================================================\n\n");
            continue;
        }
        std::printf("  (decoded %dx%d, BGRA8)\n", fb.frame.width, fb.frame.height);

        PrintEngineBlock(label.c_str(), engine, fb.frame);

        std::printf("==================================================\n\n");
    }

    Rug_DestroyOcrEngine(engine);
    return 0;
}
