// =============================================================================
//  TestCommon.h — shared helpers for the Rug.Core native test harness.
//  Tests exercise Rug.Core exclusively through the public C-ABI (RugCoreAbi.h);
//  this layer only loads PNGs into RugFrames and resolves repo-relative paths.
// =============================================================================

#ifndef RUG_TEST_COMMON_H
#define RUG_TEST_COMMON_H
#pragma once

#include <chrono>
#include <cstdint>
#include <string>
#include <vector>

#include "RugCoreAbi.h"

namespace rugtest {

// A decoded image: `pixels` owns the BGRA8 buffer that `frame.data` points into.
struct FrameBuf {
    std::vector<uint8_t> pixels;
    RugFrame             frame{};
};

using Clock = std::chrono::steady_clock;

// Directory that contains Rug.slnx, derived from the executable path (cached).
const std::wstring& RepoRoot();
std::wstring TestImagesDir();  // <root>\tests\test_images
std::wstring OcrCategoryDir(); // <root>\models\ocr

// Decode an image (PNG/JPEG/BMP) into a BGRA8 RugFrame (WinRT BitmapDecoder).
// False on failure. `pixels` owns the buffer that frame.data points into.
bool LoadImageToFrame(const std::wstring& path, FrameBuf& out);

// All ocr_*.{png,jpg,jpeg,bmp} under TestImagesDir(), sorted by name.
std::vector<std::wstring> FindOcrImages();

// First existing <dir>\<base>.{png,jpg,jpeg,bmp} (in that order), or empty.
std::wstring FindImageByBase(const std::wstring& dir, const std::wstring& base);

std::string  ToUtf8(const std::wstring& w);
std::wstring ToWide(const std::string& s);
std::wstring FileName(const std::wstring& path);

void   SetupConsole();                 // UTF-8 output
double MsBetween(Clock::time_point a, Clock::time_point b);

// A selectable OCR engine for the interactive tests.
struct EngineChoice {
    std::wstring label;    // display string
    bool         isWinRt;  // true = WinRT system engine; false = Paddle model (by id)
    std::string  id;       // Paddle model id (empty for WinRT)
};

// [0] = WinRT (always available), then each discovered Paddle model (by id).
std::vector<EngineChoice> BuildEngineChoices();

// Print a numbered table and read a selection from stdin. `preset` >= 0 selects
// that index without prompting. Returns a valid index (or -1 if none available).
int ChooseEngine(const std::vector<EngineChoice>& choices, int preset);

// Create the engine for `choice`; returns nullptr on failure (rc set).
RugOcrEngineHandle CreateChosenEngine(const EngineChoice& choice, int32_t& rc);

// Test entry points (defined in the test_*.cpp files).
int RunOcrCorrectness(int engineIndex);
int RunOcrStress(int engineIndex);
int RunTemplateMatch();
int RunModelDiscovery();

}  // namespace rugtest

#endif  // RUG_TEST_COMMON_H
