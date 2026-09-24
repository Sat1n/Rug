// =============================================================================
//  test_ocr_stress.cpp — Task 3 (engine-selectable)
//  Repeatedly recognizes one image (20x) with the user-selected engine, freeing
//  each result, to stress handle/result churn. Reports avg/min/max latency and
//  surfaces leaks (CRT leak check in main under _DEBUG) or access violations (a
//  crash means the run does not complete).
// =============================================================================

#include "TestCommon.h"

#include <algorithm>
#include <cstdio>
#include <string>
#include <vector>

using namespace rugtest;

namespace {
constexpr int kIterations = 20;
}

int rugtest::RunOcrStress(int engineIndex) {
    const std::vector<std::wstring> images = FindOcrImages();
    if (images.empty()) {
        std::printf("[ERROR] stress: no ocr_* images found in %s\n", ToUtf8(TestImagesDir()).c_str());
        return 2;
    }

    FrameBuf fb;
    if (!LoadImageToFrame(images[0], fb)) {
        std::printf("[ERROR] stress: failed to decode %s\n", ToUtf8(FileName(images[0])).c_str());
        return 2;
    }

    const std::vector<EngineChoice> choices = BuildEngineChoices();
    if (engineIndex < 0 || engineIndex >= static_cast<int>(choices.size())) engineIndex = 0;
    const EngineChoice& choice = choices[engineIndex];

    int32_t rc = 0;
    RugOcrEngineHandle engine = CreateChosenEngine(choice, rc);
    const std::string label = ToUtf8(choice.label);
    if (!engine) {
        std::printf("[ERROR] stress: engine '%s' unavailable (code %d)\n", label.c_str(), rc);
        return 2;
    }

    std::printf("Image: %s (%dx%d) | Engine: %s | %d iterations\n",
                ToUtf8(FileName(images[0])).c_str(), fb.frame.width, fb.frame.height,
                label.c_str(), kIterations);

    std::vector<double> times;
    times.reserve(kIterations);
    int okCount = 0;
    for (int i = 0; i < kIterations; ++i) {
        RugOcrResultHandle result = nullptr;
        const auto t0 = Clock::now();
        const int32_t r = Rug_RecognizeText(engine, &fb.frame, &result);
        const auto t1 = Clock::now();
        if (r == RUG_OK && result) { times.push_back(MsBetween(t0, t1)); ++okCount; }
        if (result) Rug_FreeOcrResult(result);  // free every iteration (alloc/free churn)
    }

    const int32_t drc = Rug_DestroyOcrEngine(engine);  // explicit destroy at the end

    if (times.empty()) {
        std::printf("[%s] no successful iterations (recognize kept failing)\n", label.c_str());
        return 1;
    }
    double sum = 0.0;
    for (double t : times) sum += t;
    const double avg = sum / static_cast<double>(times.size());
    const double mn = *std::min_element(times.begin(), times.end());
    const double mx = *std::max_element(times.begin(), times.end());

    std::printf("[%s] iters=%d ok=%d | avg=%.2f ms | min=%.2f ms | max=%.2f ms | destroy=%s(code %d)\n",
                label.c_str(), kIterations, okCount, avg, mn, mx,
                drc == RUG_OK ? "ok" : "FAIL", drc);
    std::printf("Completed without access violation (process reached end of stress test).\n");
    return 0;
}
