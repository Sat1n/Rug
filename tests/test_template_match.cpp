// =============================================================================
//  test_template_match.cpp — Task 4
//  Exercises Rug_MatchTemplate (OpenCV TM_CCOEFF_NORMED + multi-target peak
//  suppression). Template/target may be png or jpg (tmpl_search.* / tmpl_target.*);
//  the test gracefully skips when either is absent.
// =============================================================================

#include "TestCommon.h"

#include <cstdio>
#include <string>

using namespace rugtest;

int rugtest::RunTemplateMatch() {
    const std::wstring search = FindImageByBase(TestImagesDir(), L"tmpl_search");
    const std::wstring target = FindImageByBase(TestImagesDir(), L"tmpl_target");

    if (search.empty() || target.empty()) {
        std::printf("[SKIP] Template matching skipped: tmpl_search.png or tmpl_target.png not found.\n");
        return 0;
    }

    FrameBuf fb;
    if (!LoadImageToFrame(target, fb)) {
        std::printf("[ERROR] failed to decode target image %s\n", ToUtf8(FileName(target)).c_str());
        return 2;
    }

    constexpr int32_t kCapacity = 64;
    RugMatchBox boxes[kCapacity];
    int32_t count = kCapacity;
    const std::string searchUtf8 = ToUtf8(search);  // Rug.Core cv::imread reads png or jpg

    const auto t0 = Clock::now();
    const int32_t rc = Rug_MatchTemplate(&fb.frame, searchUtf8.c_str(), 0.8f, boxes, &count);
    const auto t1 = Clock::now();
    const double ms = MsBetween(t0, t1);

    if (rc == RUG_ERR_UNSUPPORTED) {
        std::printf("[SKIP] Rug_MatchTemplate unsupported (OpenCV not built into Rug.Core).\n");
        return 0;
    }
    if (rc != RUG_OK && rc != RUG_ERR_BUFFER_TOO_SMALL) {
        std::printf("[ERROR] Rug_MatchTemplate failed (code %d, %.1f ms)\n", rc, ms);
        return 2;
    }

    int best = -1;
    float bestScore = -1.f;
    for (int32_t i = 0; i < count; ++i) {
        if (boxes[i].confidence > bestScore) { bestScore = boxes[i].confidence; best = i; }
    }

    std::printf("== Template match: %s in %s ==\n",
                ToUtf8(FileName(search)).c_str(), ToUtf8(FileName(target)).c_str());
    std::printf("   [Time]: %.1f ms | [Matches >= 0.80]: %d%s\n",
                ms, count, rc == RUG_ERR_BUFFER_TOO_SMALL ? " (truncated)" : "");
    if (best >= 0) {
        std::printf("   Best: (x=%d, y=%d, w=%d, h=%d) | Score: %.4f\n",
                    boxes[best].x, boxes[best].y, boxes[best].width, boxes[best].height,
                    boxes[best].confidence);
    } else {
        std::printf("   Best: none above threshold\n");
    }
    return 0;
}
