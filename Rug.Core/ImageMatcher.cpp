// =============================================================================
//  ImageMatcher.cpp — OpenCV template matching skeleton (Task 1.3).
// =============================================================================

#include "pch.h"
#include "ImageMatcher.h"
#include "RugCoreAbi.h"   // RugStatus codes

#ifdef RUG_HAS_OPENCV
#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>
#include <opencv2/imgcodecs.hpp>
#endif

namespace rug::core {

#ifdef RUG_HAS_OPENCV
namespace {
constexpr int kMaxMatches = 64;  // safety cap on multi-object results
}  // namespace
#endif

int32_t ImageMatcher::MatchTemplate(const ImageView& frame,
                                    const char* templatePath,
                                    float threshold,
                                    std::vector<MatchBox>& out) {
    out.clear();

#ifndef RUG_HAS_OPENCV
    (void)frame; (void)templatePath; (void)threshold;
    return RUG_ERR_UNSUPPORTED;  // OpenCV not wired into this build (Task 1.3 skeleton)
#else
    if (!frame.data || frame.width <= 0 || frame.height <= 0) return RUG_ERR_INVALID_PARAM;
    if (!templatePath || templatePath[0] == '\0')             return RUG_ERR_INVALID_PARAM;

    try {
        cv::Mat tmpl = cv::imread(templatePath, cv::IMREAD_COLOR);
        if (tmpl.empty()) return RUG_ERR_INVALID_PARAM;  // template image missing/unreadable

        // Wrap the BGRA8 frame (honoring stride) and convert to BGR for matching.
        cv::Mat bgra(frame.height, frame.width, CV_8UC4,
                     const_cast<uint8_t*>(frame.data), static_cast<size_t>(frame.stride));
        cv::Mat bgr;
        cv::cvtColor(bgra, bgr, cv::COLOR_BGRA2BGR);

        if (tmpl.cols > bgr.cols || tmpl.rows > bgr.rows) return RUG_OK;  // no match possible

        cv::Mat result;
        cv::matchTemplate(bgr, tmpl, result, cv::TM_CCOEFF_NORMED);

        // Iteratively take the strongest peak, record it, suppress its area.
        for (int found = 0; found < kMaxMatches; ++found) {
            double maxVal = 0.0;
            cv::Point maxLoc;
            cv::minMaxLoc(result, nullptr, &maxVal, nullptr, &maxLoc);
            if (maxVal < static_cast<double>(threshold)) break;

            MatchBox box;
            box.x = maxLoc.x;
            box.y = maxLoc.y;
            box.width = tmpl.cols;
            box.height = tmpl.rows;
            box.confidence = static_cast<float>(maxVal);
            out.push_back(box);

            cv::rectangle(result,
                          cv::Point(maxLoc.x - tmpl.cols / 2, maxLoc.y - tmpl.rows / 2),
                          cv::Point(maxLoc.x + tmpl.cols / 2, maxLoc.y + tmpl.rows / 2),
                          cv::Scalar(-1.0), cv::FILLED);
        }
        return RUG_OK;
    }
    catch (...) {
        return RUG_ERR_UNSUPPORTED;
    }
#endif
}

}  // namespace rug::core
