#include <windows.h>
#include <tlhelp32.h>
#include <iostream>
#include <fstream>
#include <string>
#include <string_view>
#include <vector>
#include <algorithm>
#include <cstdint>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Globalization.h>
#include <winrt/Windows.Media.Ocr.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/Windows.Storage.Streams.h>

#include <windows.graphics.capture.interop.h>
#include <Windows.Graphics.DirectX.Direct3D11.interop.h>
#include <MemoryBuffer.h>
#include <d3d11.h>

#pragma comment(lib, "windowsapp")
#pragma comment(lib, "d3d11")

using namespace winrt;
using namespace winrt::Windows::Globalization;
using namespace winrt::Windows::Media::Ocr;
using namespace winrt::Windows::Graphics::Imaging;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
using namespace winrt::Windows::Storage::Streams;

constexpr wchar_t kDefaultProcess[] = L"QQMusic.exe";
constexpr const wchar_t* kTargetTexts[] = { L"推荐", L"音乐" };
constexpr uint32_t kOcrScale = 2;
constexpr wchar_t kDebugImagePath[] = L"debug_capture.png";

com_ptr<ID3D11Device> g_d3dDevice;
com_ptr<ID3D11DeviceContext> g_d3dContext;
IDirect3DDevice g_winrtDevice{ nullptr };

void InitD3D() {
    winrt::check_hresult(D3D11CreateDevice(
        nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
        D3D11_SDK_VERSION, g_d3dDevice.put(), nullptr, g_d3dContext.put()));

    com_ptr<IDXGIDevice> dxgiDevice = g_d3dDevice.as<IDXGIDevice>();
    com_ptr<IInspectable> inspectable;
    winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.get(), inspectable.put()));
    g_winrtDevice = inspectable.as<IDirect3DDevice>();
}

GraphicsCaptureItem CreateCaptureItemForWindow(HWND hwnd) {
    auto interop = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    GraphicsCaptureItem item{ nullptr };
    winrt::check_hresult(interop->CreateForWindow(hwnd, guid_of<GraphicsCaptureItem>(), put_abi(item)));
    return item;
}

struct EnumWindowData { DWORD pid; HWND hwnd; long area; bool titled; };

BOOL CALLBACK EnumWindowsProc(HWND hwnd, LPARAM lParam) {
    EnumWindowData* data = reinterpret_cast<EnumWindowData*>(lParam);
    DWORD windowPid = 0;
    GetWindowThreadProcessId(hwnd, &windowPid);
    if (windowPid != data->pid || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return TRUE;

    RECT rect;
    GetWindowRect(hwnd, &rect);
    long w = rect.right - rect.left;
    long h = rect.bottom - rect.top;
    if (w <= 300 || h <= 300) return TRUE;

    int ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
    if (ex & WS_EX_TRANSPARENT) return TRUE;

    wchar_t title[256] = {};
    GetWindowTextW(hwnd, title, 256);
    bool hasTitle = title[0] != L'\0';
    long area = w * h;

    bool better = (hasTitle && !data->titled) || (hasTitle == data->titled && area > data->area);
    if (better || !data->hwnd) {
        data->hwnd = hwnd;
        data->area = area;
        data->titled = hasTitle;
    }
    return TRUE;
}

HWND GetHWNDByProcessName(const wchar_t* processName) {
    DWORD pid = 0;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot != INVALID_HANDLE_VALUE) {
        PROCESSENTRY32W entry = { sizeof(PROCESSENTRY32W) };
        if (Process32FirstW(snapshot, &entry)) {
            do {
                if (_wcsicmp(entry.szExeFile, processName) == 0) {
                    pid = entry.th32ProcessID;
                    break;
                }
            } while (Process32NextW(snapshot, &entry));
        }
        CloseHandle(snapshot);
    }
    if (pid == 0) return NULL;
    EnumWindowData data = { pid, NULL, 0, false };
    EnumWindows(EnumWindowsProc, reinterpret_cast<LPARAM>(&data));
    return data.hwnd;
}

std::string ToUtf8(std::wstring_view text) {
    if (text.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, text.data(), (int)text.size(), nullptr, 0, nullptr, nullptr);
    std::string result(size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), (int)text.size(), result.data(), size, nullptr, nullptr);
    return result;
}

bool SaveBitmapAsPng(SoftwareBitmap const& bitmap, wchar_t const* path) {
    try {
        auto stream = InMemoryRandomAccessStream();
        auto encoder = BitmapEncoder::CreateAsync(BitmapEncoder::PngEncoderId(), stream).get();
        encoder.SetSoftwareBitmap(bitmap);
        encoder.FlushAsync().get();

        uint32_t size = static_cast<uint32_t>(stream.Size());
        Buffer request(size);
        stream.Seek(0);
        IBuffer filled = stream.ReadAsync(request, size, InputStreamOptions::None).get();
        DataReader reader = DataReader::FromBuffer(filled);
        std::vector<uint8_t> bytes(reader.UnconsumedBufferLength());
        reader.ReadBytes(bytes);

        std::ofstream file(path, std::ios::binary);
        file.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
        return static_cast<bool>(file);
    }
    catch (...) {
        return false;
    }
}

SoftwareBitmap PrepareOcrBitmap(SoftwareBitmap const& raw, uint32_t targetWidth, uint32_t targetHeight) {
    auto stream = InMemoryRandomAccessStream();
    auto encoder = BitmapEncoder::CreateAsync(BitmapEncoder::PngEncoderId(), stream).get();
    encoder.SetSoftwareBitmap(raw);
    encoder.FlushAsync().get();

    auto decoder = BitmapDecoder::CreateAsync(stream).get();
    BitmapTransform transform;
    transform.ScaledWidth(targetWidth);
    transform.ScaledHeight(targetHeight);
    transform.InterpolationMode(BitmapInterpolationMode::Fant);
    auto provider = decoder.GetPixelDataAsync(
        BitmapPixelFormat::Bgra8,
        BitmapAlphaMode::Ignore,
        transform,
        ExifOrientationMode::IgnoreExifOrientation,
        ColorManagementMode::DoNotColorManage).get();
    auto pixels = provider.DetachPixelData();

    DataWriter writer;
    writer.WriteBytes(pixels);
    return SoftwareBitmap::CreateCopyFromBuffer(
        writer.DetachBuffer(), BitmapPixelFormat::Bgra8,
        static_cast<int32_t>(targetWidth), static_cast<int32_t>(targetHeight));
}

bool IsAllBlack(SoftwareBitmap const& bitmap) {
    BitmapBuffer buffer = bitmap.LockBuffer(BitmapBufferAccessMode::Read);
    auto reference = buffer.CreateReference();
    auto byteAccess = reference.as<::Windows::Foundation::IMemoryBufferByteAccess>();
    BYTE* data = nullptr;
    UINT32 capacity = 0;
    winrt::check_hresult(byteAccess->GetBuffer(&data, &capacity));
    for (UINT32 i = 0; i + 4 <= capacity; i += 64) {
        if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 0) return false;
    }
    return true;
}

SoftwareBitmap CaptureWindowForOcr(HWND hwnd, uint32_t* scaleOut) {
    *scaleOut = 1;
    if (IsIconic(hwnd)) {
        std::cout << "[!] Target window is minimized, restoring without activation...\n";
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        Sleep(300);
    }
    auto item = CreateCaptureItemForWindow(hwnd);
    winrt::Windows::Graphics::SizeInt32 poolSize = item.Size();

    auto framePool = Direct3D11CaptureFramePool::Create(
        g_winrtDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, poolSize);
    auto session = framePool.CreateCaptureSession(item);
    session.IsCursorCaptureEnabled(false);
    session.StartCapture();
    std::cout << "[dbg] capture started" << std::endl;

    auto tryGetLatestFrame = [](Direct3D11CaptureFramePool const& pool) {
        Direct3D11CaptureFrame frame{ nullptr };
        for (int i = 0; i < 50 && !frame; ++i) {
            frame = pool.TryGetNextFrame();
            if (!frame) Sleep(20);
        }
        if (frame) {
            for (int extra = 0; extra < 8; ++extra) {
                auto next = pool.TryGetNextFrame();
                if (!next) break;
                frame = next;
            }
        }
        return frame;
        };

    Sleep(100);
    Direct3D11CaptureFrame frame = tryGetLatestFrame(framePool);
    if (!frame) {
        session.Close();
        framePool.Close();
        std::cout << "[ERROR] No frame received from capture pool!" << std::endl;
        return nullptr;
    }
    std::cout << "[dbg] got frame " << frame.ContentSize().Width << "x" << frame.ContentSize().Height << std::endl;

    SoftwareBitmap raw = SoftwareBitmap::CreateCopyFromSurfaceAsync(frame.Surface()).get();
    for (int retry = 0; IsAllBlack(raw) && retry < 5; ++retry) {
        std::cout << "[dbg] black frame, retry " << retry + 1 << std::endl;
        Sleep(100);
        Direct3D11CaptureFrame next = tryGetLatestFrame(framePool);
        if (next) {
            raw = SoftwareBitmap::CreateCopyFromSurfaceAsync(next.Surface()).get();
        }
    }
    session.Close();
    framePool.Close();
    std::cout << "[dbg] frame content copied" << std::endl;

    if (IsAllBlack(raw)) {
        std::cout << "[ERROR] Captured frame is black! (window may be protected or not rendering)\n";
        return nullptr;
    }

    SaveBitmapAsPng(raw, kDebugImagePath);

    uint32_t scale = kOcrScale;
    uint32_t maxDim = OcrEngine::MaxImageDimension();
    if (maxDim != 0 && (raw.PixelWidth() * scale > maxDim || raw.PixelHeight() * scale > maxDim)) {
        scale = 1;
    }
    *scaleOut = scale;
    if (scale <= 1) return raw;
    SoftwareBitmap scaled = PrepareOcrBitmap(raw, raw.PixelWidth() * scale, raw.PixelHeight() * scale);
    SaveBitmapAsPng(scaled, L"debug_ocr_input.png");
    return scaled;
}

winrt::Windows::Foundation::Rect LineBoundingRect(OcrLine const& line) {
    float minX = 1e9f, minY = 1e9f, maxX = 0, maxY = 0;
    for (auto const& word : line.Words()) {
        auto b = word.BoundingRect();
        minX = (std::min)(minX, b.X);
        minY = (std::min)(minY, b.Y);
        maxX = (std::max)(maxX, b.X + b.Width);
        maxY = (std::max)(maxY, b.Y + b.Height);
    }
    return { minX, minY, maxX - minX, maxY - minY };
}

std::wstring CompactText(std::wstring_view text) {
    std::wstring out;
    for (wchar_t ch : text) {
        if (!iswspace(ch)) out.push_back(ch);
    }
    return out;
}

int run(int argc, wchar_t* argv[]) {
    init_apartment();
    InitD3D();

    std::cout << "=== WGC Capture & OCR POC ===\n";

    wchar_t const* processName = (argc > 1) ? argv[1] : kDefaultProcess;
    HWND hwnd = GetHWNDByProcessName(processName);
    if (!hwnd) {
        std::cout << "[ERROR] Could not find HWND for " << ToUtf8(processName) << "!\n";
        return -1;
    }
    std::cout << "[+] Found HWND: " << (void*)hwnd << "\n";

    RECT windowRect{};
    GetWindowRect(hwnd, &windowRect);
    POINT clientOrigin{ 0, 0 };
    ClientToScreen(hwnd, &clientOrigin);
    int offsetX = clientOrigin.x - windowRect.left;
    int offsetY = clientOrigin.y - windowRect.top;

    uint32_t scale = 1;
    SoftwareBitmap bitmap = CaptureWindowForOcr(hwnd, &scale);
    if (!bitmap) {
        std::cout << "[ERROR] Frame capture failed!\n";
        return -1;
    }
    std::cout << "[+] Frame captured: " << bitmap.PixelWidth() << "x" << bitmap.PixelHeight()
        << " (ocr scale x" << scale << ")\n";
    std::cout << "[+] Raw capture saved to " << ToUtf8(kDebugImagePath) << "\n";

    Language zhLang(L"zh-Hans");
    OcrEngine engine = OcrEngine::TryCreateFromLanguage(zhLang);
    if (!engine) engine = OcrEngine::TryCreateFromUserProfileLanguages();
    if (!engine) {
        std::cout << "[ERROR] No OCR engine available!\n";
        return -1;
    }
    std::cout << "[+] OCR engine language: " << ToUtf8(engine.RecognizerLanguage().LanguageTag()) << "\n";
    std::cout << "[+] Running OCR...\n\n";

    auto result = engine.RecognizeAsync(bitmap).get();

    {
        std::ofstream ocrFile("ocr_result.txt", std::ios::binary);
        ocrFile << "language=" << ToUtf8(engine.RecognizerLanguage().LanguageTag()) << "\n";
        for (auto const& line : result.Lines()) {
            auto r = LineBoundingRect(line);
            ocrFile << "[Line] (" << (int)(r.X / scale) << "," << (int)(r.Y / scale) << " "
                << (int)(r.Width / scale) << "x" << (int)(r.Height / scale) << ") "
                << ToUtf8(line.Text()) << "\n";
        }
    }

    for (auto const& line : result.Lines()) {
        auto r = LineBoundingRect(line);
        std::cout << "[Line] (" << (int)(r.X / scale) << "," << (int)(r.Y / scale) << " "
            << (int)(r.Width / scale) << "x" << (int)(r.Height / scale) << ") "
            << ToUtf8(line.Text()) << "\n";
    }

    bool matched = false;
    std::wstring matchedText;
    int targetX = -1, targetY = -1;
    for (wchar_t const* target : kTargetTexts) {
        for (auto const& line : result.Lines()) {
            std::wstring compact = CompactText(line.Text());
            if (compact.find(target) == std::wstring::npos) continue;
            auto r = LineBoundingRect(line);
            targetX = static_cast<int>((r.X + r.Width / 2) / scale) - offsetX;
            targetY = static_cast<int>((r.Y + r.Height / 2) / scale) - offsetY;
            matchedText = std::move(compact);
            matched = true;
            break;
        }
        if (matched) break;
    }

    if (matched) {
        std::cout << "\n[!] Matched: " << ToUtf8(matchedText)
            << " at client (" << targetX << ", " << targetY << ")\n";
        LPARAM lParam = MAKELPARAM(targetX, targetY);
        PostMessage(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lParam);
        Sleep(30);
        PostMessage(hwnd, WM_LBUTTONUP, 0, lParam);
        Sleep(40);
        PostMessage(hwnd, WM_LBUTTONDBLCLK, MK_LBUTTON, lParam);
        Sleep(30);
        PostMessage(hwnd, WM_LBUTTONUP, 0, lParam);
        std::cout << "[SUCCESS] PostMessage Click Sent!\n";
    }
    else {
        std::cout << "\n[MISS] Target text not found.\n";
    }

    return 0;
}

int wmain(int argc, wchar_t* argv[]) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    SetConsoleOutputCP(CP_UTF8);
    try {
        return run(argc, argv);
    }
    catch (winrt::hresult_error const& e) {
        std::cout << "[EXCEPTION] " << ToUtf8(e.message()) << " code=0x" << std::hex << e.code().value << std::dec << std::endl;
    }
    catch (std::exception const& e) {
        std::cout << "[EXCEPTION] " << e.what() << std::endl;
    }
    return -2;
}
