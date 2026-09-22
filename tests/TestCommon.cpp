// =============================================================================
//  TestCommon.cpp — PNG decoding (WinRT), path resolution, console/UTF-8 helpers.
// =============================================================================

#include "TestCommon.h"

#include <windows.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.Graphics.Imaging.h>

#include <algorithm>
#include <cwctype>
#include <filesystem>

#pragma comment(lib, "windowsapp.lib")

using namespace winrt;
using namespace winrt::Windows::Storage;
using namespace winrt::Windows::Graphics::Imaging;

namespace fs = std::filesystem;

namespace rugtest {

const std::wstring& RepoRoot() {
    static const std::wstring root = [] {
        std::wstring exe;
        DWORD cap = MAX_PATH;
        for (;;) {
            exe.resize(cap);
            DWORD got = GetModuleFileNameW(nullptr, exe.data(), cap);
            if (got == 0) break;
            if (got < cap) { exe.resize(got); break; }
            cap *= 2;
        }
        fs::path dir = fs::path(exe).parent_path();
        for (int i = 0; i < 12 && !dir.empty(); ++i) {
            std::error_code ec;
            if (fs::exists(dir / L"Rug.slnx", ec)) return dir.wstring();
            if (dir == dir.parent_path()) break;
            dir = dir.parent_path();
        }
        return fs::current_path().wstring();  // fallback
    }();
    return root;
}

std::wstring TestImagesDir() { return RepoRoot() + L"\\tests\\test_images"; }
std::wstring OcrModelDir()   { return RepoRoot() + L"\\models\\ocr\\ppocr_v6"; }

bool LoadImageToFrame(const std::wstring& path, FrameBuf& out) {
    try {
        StorageFile file = StorageFile::GetFileFromPathAsync(path).get();
        auto stream   = file.OpenAsync(FileAccessMode::Read).get();
        BitmapDecoder decoder = BitmapDecoder::CreateAsync(stream).get();

        auto provider = decoder.GetPixelDataAsync(
            BitmapPixelFormat::Bgra8, BitmapAlphaMode::Premultiplied,
            BitmapTransform(), ExifOrientationMode::IgnoreExifOrientation,
            ColorManagementMode::DoNotColorManage).get();

        auto data = provider.DetachPixelData();
        out.pixels.assign(data.begin(), data.end());
        if (out.pixels.empty()) return false;

        const int32_t w = static_cast<int32_t>(decoder.PixelWidth());
        const int32_t h = static_cast<int32_t>(decoder.PixelHeight());

        out.frame            = RugFrame{};
        out.frame.data       = out.pixels.data();
        out.frame.dataLength = static_cast<uint32_t>(out.pixels.size());
        out.frame.width      = w;
        out.frame.height     = h;
        out.frame.stride     = w * 4;
        out.frame.format     = RUG_PIXEL_BGRA8;
        return true;
    }
    catch (...) {
        return false;
    }
}

std::vector<std::wstring> FindOcrImages() {
    std::vector<std::wstring> result;
    std::error_code ec;
    const fs::path dir = TestImagesDir();
    if (!fs::is_directory(dir, ec)) return result;

    for (const auto& entry : fs::directory_iterator(dir, ec)) {
        if (!entry.is_regular_file()) continue;
        const fs::path& p = entry.path();
        std::wstring ext = p.extension().wstring();
        for (wchar_t& c : ext) c = static_cast<wchar_t>(std::towlower(c));
        if (ext != L".png" && ext != L".jpg" && ext != L".jpeg" && ext != L".bmp") continue;
        if (p.filename().wstring().rfind(L"ocr_", 0) == 0)
            result.push_back(p.wstring());
    }
    std::sort(result.begin(), result.end());
    return result;
}

std::wstring FindImageByBase(const std::wstring& dir, const std::wstring& base) {
    static const wchar_t* kExts[] = { L".png", L".jpg", L".jpeg", L".bmp" };
    for (const wchar_t* ext : kExts) {
        const fs::path p = fs::path(dir) / (base + ext);
        std::error_code ec;
        if (fs::exists(p, ec)) return p.wstring();
    }
    return {};
}

std::string ToUtf8(const std::wstring& w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()),
                                nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()),
                        s.data(), n, nullptr, nullptr);
    return s;
}

std::wstring ToWide(const std::string& s) {
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()),
                                nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

std::wstring FileName(const std::wstring& path) {
    return fs::path(path).filename().wstring();
}

void SetupConsole() {
    SetConsoleOutputCP(CP_UTF8);
    SetConsoleCP(CP_UTF8);
}

double MsBetween(Clock::time_point a, Clock::time_point b) {
    return std::chrono::duration<double, std::milli>(b - a).count();
}

}  // namespace rugtest
