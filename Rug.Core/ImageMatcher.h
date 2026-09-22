// =============================================================================
//  ImageMatcher.h
//  OpenCV template matching (Task 1.3).
//
//  Status: framework skeleton. The cv::matchTemplate path is compiled only when
//  RUG_HAS_OPENCV is defined and OpenCV is wired into the project; otherwise
//  MatchTemplate returns RUG_ERR_UNSUPPORTED so Rug.Core still builds.
// =============================================================================

#ifndef IMAGE_MATCHER_H
#define IMAGE_MATCHER_H
#pragma once

#include "ImageView.h"
#include <cstdint>
#include <vector>

namespace rug::core {

struct MatchBox {
    int32_t x = 0, y = 0, width = 0, height = 0;
    float   confidence = 0.f;  // normalized match score in [0, 1]
};

class ImageMatcher {
public:
    // Find every occurrence of the template image at `templatePath` inside
    // `frame` whose normalized score >= `threshold`. Appends hits to `out`.
    // Returns a RugStatus value (RUG_OK == 0).
    static int32_t MatchTemplate(const ImageView& frame,
                                 const char* templatePath,
                                 float threshold,
                                 std::vector<MatchBox>& out);
};

}  // namespace rug::core

#endif  // IMAGE_MATCHER_H
