// =============================================================================
//  WinRtOcrEngine.cpp — Windows.Media.Ocr back-end.
//  Reference: Rug.Poc/Rug.Poc.cpp (PrepareOcrBitmap / LineBoundingRect / CompactText).
// =============================================================================

#include "pch.h"
#include "WinRtOcrEngine.h"
#include "RugCoreAbi.h"   // RugStatus codes
#include "CoreCom.h"
#include "OcrTextUtils.h" // shared CompactText / IsCjk

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Globalization.h>
#include <winrt/Windows.Media.Ocr.h>

#include <cstring>
#include <cwctype>
#include <string_view>
#include <vector>

#pragma comment(lib, "windowsapp.lib")

using namespace winrt;
using namespace winrt::Windows::Graphics::Imaging;
using namespace winrt::Windows::Storage::Streams;
using winrt::Windows::Globalization::Language;

namespace ocr = winrt::Windows::Media::Ocr;
using WinRect = winrt::Windows::Foundation::Rect;

namespace rug::core {
namespace {

constexpr uint32_t kOcrUpscale = 2;  // Fant super-resolution factor

std::wstring Utf8ToWide(const char* s) {
    if (!s || !s[0]) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 0) return {};
    std::wstring w(static_cast<size_t>(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}

WinRect LineBoundingRect(ocr::OcrLine const& line) {
    float minX = 1e9f, minY = 1e9f, maxX = 0.f, maxY = 0.f;
    for (auto const& word : line.Words()) {
        auto b = word.BoundingRect();
        minX = (std::min)(minX, b.X);
        minY = (std::min)(minY, b.Y);
        maxX = (std::max)(maxX, b.X + b.Width);
        maxY = (std::max)(maxY, b.Y + b.Height);
    }
    return { minX, minY, maxX - minX, maxY - minY };
}

// Build a Bgra8 SoftwareBitmap from a (possibly row-padded) BGRA8 view.
SoftwareBitmap MakeBitmap(const ImageView& image) {
    const uint32_t tightStride = static_cast<uint32_t>(image.width) * 4u;
    std::vector<uint8_t> buf(static_cast<size_t>(tightStride) * image.height);

    if (image.stride == static_cast<int32_t>(tightStride)) {
        std::memcpy(buf.data(), image.data, buf.size());
    } else {
        for (int32_t y = 0; y < image.height; ++y) {
            std::memcpy(buf.data() + static_cast<size_t>(y) * tightStride,
                        image.data + static_cast<size_t>(y) * image.stride,
                        tightStride);
        }
    }

    DataWriter writer;
    writer.WriteBytes(buf);  // std::vector<uint8_t> -> array_view<uint8_t const>
    return SoftwareBitmap::CreateCopyFromBuffer(
        writer.DetachBuffer(), BitmapPixelFormat::Bgra8, image.width, image.height);
}

// 2x Fant super-resolution via an in-memory PNG round-trip (proven in the POC).
SoftwareBitmap PrepareOcrBitmap(SoftwareBitmap const& raw, uint32_t targetW, uint32_t targetH) {
    InMemoryRandomAccessStream stream;
    BitmapEncoder encoder = BitmapEncoder::CreateAsync(BitmapEncoder::PngEncoderId(), stream).get();
    encoder.SetSoftwareBitmap(raw);
    encoder.FlushAsync().get();

    stream.Seek(0);
    BitmapDecoder decoder = BitmapDecoder::CreateAsync(stream).get();
    BitmapTransform transform;
    transform.ScaledWidth(targetW);
    transform.ScaledHeight(targetH);
    transform.InterpolationMode(BitmapInterpolationMode::Fant);

    PixelDataProvider provider = decoder.GetPixelDataAsync(
        BitmapPixelFormat::Bgra8, BitmapAlphaMode::Ignore, transform,
        ExifOrientationMode::IgnoreExifOrientation,
        ColorManagementMode::DoNotColorManage).get();
    auto pixels = provider.DetachPixelData();

    DataWriter writer;
    writer.WriteBytes(pixels);
    return SoftwareBitmap::CreateCopyFromBuffer(
        writer.DetachBuffer(), BitmapPixelFormat::Bgra8,
        static_cast<int32_t>(targetW), static_cast<int32_t>(targetH));
}

}  // namespace

// -----------------------------------------------------------------------------
struct WinRtOcrEngine::Impl {
    ocr::OcrEngine engine{ nullptr };
};

WinRtOcrEngine::WinRtOcrEngine() : m_impl(std::make_unique<Impl>()) {}
WinRtOcrEngine::~WinRtOcrEngine() = default;

int32_t WinRtOcrEngine::Create(const char* languageTag, std::unique_ptr<IOcrEngine>& out) {
    EnsureApartment();
    try {
        ocr::OcrEngine engine{ nullptr };

        // Explicit BCP-47 tag when provided (e.g. "zh-Hans", "en", "ja").
        if (languageTag && languageTag[0] != '\0') {
            const std::wstring tag = Utf8ToWide(languageTag);
            if (!tag.empty())
                engine = ocr::OcrEngine::TryCreateFromLanguage(Language(tag.c_str()));
        }
        // Default / fallback: the user's system (profile) languages.
        if (!engine) engine = ocr::OcrEngine::TryCreateFromUserProfileLanguages();
        if (!engine) return RUG_ERR_OCR_FAILED;  // no OCR language pack available

        std::unique_ptr<WinRtOcrEngine> created(new WinRtOcrEngine());
        created->m_impl->engine = engine;
        out = std::move(created);
        return RUG_OK;
    }
    catch (...) {
        return RUG_ERR_OCR_FAILED;
    }
}

int32_t WinRtOcrEngine::Recognize(const ImageView& image, OcrResult& out) {
    if (!m_impl || !m_impl->engine)                          return RUG_ERR_NOT_INITIALIZED;
    if (!image.data || image.width <= 0 || image.height <= 0) return RUG_ERR_INVALID_PARAM;

    try {
        SoftwareBitmap base = MakeBitmap(image);
        if (!base) return RUG_ERR_OCR_FAILED;

        uint32_t scale = 1;
        SoftwareBitmap ocrInput = base;
        const uint32_t maxDim = ocr::OcrEngine::MaxImageDimension();
        const bool fits = (maxDim == 0) ||
            (static_cast<uint32_t>(image.width) * kOcrUpscale <= maxDim &&
             static_cast<uint32_t>(image.height) * kOcrUpscale <= maxDim);
        if (fits) {
            ocrInput = PrepareOcrBitmap(base,
                                        static_cast<uint32_t>(image.width) * kOcrUpscale,
                                        static_cast<uint32_t>(image.height) * kOcrUpscale);
            scale = kOcrUpscale;
        }

        ocr::OcrResult result = m_impl->engine.RecognizeAsync(ocrInput).get();

        out.lines.clear();
        out.lines.reserve(result.Lines().Size());
        for (auto const& line : result.Lines()) {
            WinRect r = LineBoundingRect(line);
            OcrLine ol;
            ol.box.x      = static_cast<int32_t>(r.X / scale);
            ol.box.y      = static_cast<int32_t>(r.Y / scale);
            ol.box.width  = static_cast<int32_t>(r.Width / scale);
            ol.box.height = static_cast<int32_t>(r.Height / scale);
            ol.text       = CompactText(line.Text().c_str());
            ol.confidence = 1.0f;  // WinRT OCR exposes no per-line confidence
            out.lines.push_back(std::move(ol));
        }
        return RUG_OK;
    }
    catch (...) {
        return RUG_ERR_OCR_FAILED;
    }
}

}  // namespace rug::core
