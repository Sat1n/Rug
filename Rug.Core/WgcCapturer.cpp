// =============================================================================
//  WgcCapturer.cpp — implementation of the WGC + D3D11 window capturer.
//  Reference: Rug.Poc/Rug.Poc.cpp (InitD3D / CaptureWindowForOcr / IsAllBlack).
// =============================================================================

#include "pch.h"
#include "WgcCapturer.h"
#include "RugCoreAbi.h"   // RugStatus codes

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/Windows.Graphics.Imaging.h>

#include <windows.graphics.capture.interop.h>
#include <Windows.Graphics.DirectX.Direct3D11.interop.h>
#include <MemoryBuffer.h>
#include <d3d11.h>
#include <dxgi.h>

#include <cstring>
#include <new>
#include <mutex>
#include <dwmapi.h>
#include "CaptureContract.h"
#pragma comment(lib, "dwmapi.lib")

#pragma comment(lib, "windowsapp.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

using namespace winrt;
using namespace winrt::Windows::Graphics;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
using namespace winrt::Windows::Graphics::Imaging;

namespace rug::core {
namespace {

constexpr int    kFirstFrameMaxTries   = 50;   // 50 * 20ms = up to 1s
constexpr int    kFirstFramePollMs     = 20;
constexpr int    kDrainExtraFrames     = 8;    // discard stale frames, keep latest
constexpr int    kBlackFrameMaxRetries = 5;
constexpr long   kMinWindowDimension   = 300;  // ignore tiny helper windows

// COM is per-thread. The host may call from an MTA thread pool thread or an STA
// UI thread; initialize multithreaded and tolerate an existing apartment.
void EnsureApartment() {
    static thread_local bool s_done = [] {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        (void)hr;  // S_FALSE (already init) and RPC_E_CHANGED_MODE (STA host) are both usable
        return true;
    }();
    (void)s_done;
}

// Given any HWND of the target process, pick the largest visible, titled main
// render window, skipping WS_EX_LAYERED / WS_EX_TRANSPARENT overlays.
HWND ResolveRenderWindow(HWND input) {
    if (!input || !IsWindow(input)) return nullptr;

    DWORD pid = 0;
    GetWindowThreadProcessId(input, &pid);
    if (pid == 0) return input;

    struct EnumData { DWORD pid; HWND best; long area; bool titled; };
    EnumData data{ pid, nullptr, 0, false };

    EnumWindows([](HWND hwnd, LPARAM lp) -> BOOL {
        auto* d = reinterpret_cast<EnumData*>(lp);
        DWORD windowPid = 0;
        GetWindowThreadProcessId(hwnd, &windowPid);
        if (windowPid != d->pid) return TRUE;
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return TRUE;

        LONG ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
        if (ex & WS_EX_TRANSPARENT) return TRUE;  // click-through overlay
        if (ex & WS_EX_LAYERED)     return TRUE;  // layered overlay (CEF etc.)

        RECT rect{};
        GetWindowRect(hwnd, &rect);
        long w = rect.right - rect.left;
        long h = rect.bottom - rect.top;
        if (w <= kMinWindowDimension || h <= kMinWindowDimension) return TRUE;

        wchar_t title[256] = {};
        GetWindowTextW(hwnd, title, 256);
        bool hasTitle = title[0] != L'\0';
        long area = w * h;

        bool better = (hasTitle && !d->titled) || (hasTitle == d->titled && area > d->area);
        if (better || !d->best) {
            d->best   = hwnd;
            d->area   = area;
            d->titled = hasTitle;
        }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&data));

    return data.best ? data.best : input;  // fall back to the caller's HWND
}

double WindowDpiScale(HWND hwnd) {
    if (!hwnd) return 1.0;
    UINT dpi = GetDpiForWindow(hwnd);
    return dpi > 0 ? static_cast<double>(dpi) / 96.0 : 1.0;
}

constexpr wchar_t kLifetimeProperty[] = L"Rug.Capture.WindowLifetime.v1";
uint64_t WindowLifetime(HWND hwnd, bool create) {
    static std::mutex gate;
    std::lock_guard lock(gate);
    auto value = GetPropW(hwnd, kLifetimeProperty);
    if (!value && create && IsWindow(hwnd)) {
        GUID id{};
        if (FAILED(CoCreateGuid(&id))) return 0;
        uintptr_t token = 0;
        std::memcpy(&token, &id, sizeof(token));
        token = (token >> 1) | 1;
        if (SetPropW(hwnd, kLifetimeProperty, reinterpret_cast<HANDLE>(token))) value = reinterpret_cast<HANDLE>(token);
    }
    return reinterpret_cast<uintptr_t>(value);
}
struct DpiScope {
    DPI_AWARENESS_CONTEXT previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    ~DpiScope() { if (previous) SetThreadDpiAwarenessContext(previous); }
};
WindowSample SampleWindow(HWND hwnd) {
    DpiScope physical;
    WindowSample s;
    s.tid = GetWindowThreadProcessId(hwnd, &s.pid);
    s.lifetime = WindowLifetime(hwnd, false);
    s.dpi = GetDpiForWindow(hwnd);
    s.style = GetWindowLongPtrW(hwnd, GWL_STYLE); s.exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
    s.monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL);
    s.valid = s.tid && s.lifetime && physical.previous && s.dpi && IsWindowVisible(hwnd) && !IsIconic(hwnd) &&
        GetClientRect(hwnd, &s.client) && ClientToScreen(hwnd, &s.origin) &&
        SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &s.bounds, sizeof(s.bounds)));
    return s;
}
int64_t Qpc() { LARGE_INTEGER q{}; return QueryPerformanceCounter(&q) ? q.QuadPart : 0; }

bool IsAllBlack(SoftwareBitmap const& bitmap) {
    BitmapBuffer buffer = bitmap.LockBuffer(BitmapBufferAccessMode::Read);
    auto reference  = buffer.CreateReference();
    auto byteAccess = reference.as<::Windows::Foundation::IMemoryBufferByteAccess>();
    BYTE*  data     = nullptr;
    UINT32 capacity = 0;
    bool black = true;
    if (SUCCEEDED(byteAccess->GetBuffer(&data, &capacity)) && data) {
        for (UINT32 i = 0; i + 4 <= capacity; i += 64) {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 0) { black = false; break; }
        }
    }
    buffer.Close();
    return black;
}

}  // namespace

// -----------------------------------------------------------------------------
// Impl
// -----------------------------------------------------------------------------
struct WgcCapturer::Impl {
    WgcCapturer::Config cfg;
    HWND   resolved = nullptr;
    double dpiScale = 1.0;
    bool   started  = false;
    uint64_t boundLifetime = 0, capturedLifetime = 0;
    std::mutex gate;

    com_ptr<ID3D11Device>        d3dDevice;
    com_ptr<ID3D11DeviceContext> d3dContext;
    IDirect3DDevice              winrtDevice{ nullptr };

    GraphicsCaptureItem         item{ nullptr };
    Direct3D11CaptureFramePool  framePool{ nullptr };
    GraphicsCaptureSession      session{ nullptr };
    SizeInt32                   poolSize{};

    int32_t InitD3D() {
        HRESULT hr = D3D11CreateDevice(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
            D3D11_SDK_VERSION, d3dDevice.put(), nullptr, d3dContext.put());
        if (FAILED(hr)) return RUG_ERR_NOT_INITIALIZED;

        com_ptr<IDXGIDevice> dxgiDevice = d3dDevice.as<IDXGIDevice>();
        com_ptr<IInspectable> inspectable;
        hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.get(), inspectable.put());
        if (FAILED(hr)) return RUG_ERR_NOT_INITIALIZED;

        winrtDevice = inspectable.as<IDirect3DDevice>();
        return RUG_OK;
    }

    Direct3D11CaptureFramePool CreatePool() {
        return Direct3D11CaptureFramePool::CreateFreeThreaded(
            winrtDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, poolSize);
    }

    Direct3D11CaptureFrame TryGetLatestFrame() {
        Direct3D11CaptureFrame frame{ nullptr };
        for (int i = 0; i < kFirstFrameMaxTries && !frame; ++i) {
            frame = framePool.TryGetNextFrame();
            if (!frame) Sleep(kFirstFramePollMs);
        }
        if (frame) {
            for (int extra = 0; extra < kDrainExtraFrames; ++extra) {
                Direct3D11CaptureFrame next = framePool.TryGetNextFrame();
                if (!next) break;
                frame.Close();
                frame = next;
            }
        }
        return frame;
    }

    int32_t Start() {
        EnsureApartment();

        resolved = cfg.exactWindow ? cfg.targetWindow : ResolveRenderWindow(cfg.targetWindow);
        if (!resolved || !IsWindow(resolved)) return RUG_ERR_INVALID_PARAM;
        boundLifetime = WindowLifetime(cfg.targetWindow, true);
        capturedLifetime = WindowLifetime(resolved, true);

        if (!GraphicsCaptureSession::IsSupported()) return RUG_ERR_UNSUPPORTED;

        int32_t st = InitD3D();
        if (st != RUG_OK) return st;

        auto interop = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        GraphicsCaptureItem created{ nullptr };
        HRESULT hr = interop->CreateForWindow(
            resolved, guid_of<GraphicsCaptureItem>(), put_abi(created));
        if (FAILED(hr) || !created) return RUG_ERR_CAPTURE_FAILED;
        item = created;

        poolSize = item.Size();
        if (poolSize.Width <= 0 || poolSize.Height <= 0) return RUG_ERR_CAPTURE_FAILED;

        framePool = CreatePool();
        session   = framePool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled(cfg.captureCursor); }
        catch (...) { /* not supported before Win10 2004 — ignore */ }

        session.StartCapture();
        started = true;
        Sleep(100);  // let the pipeline deliver a first frame
        return RUG_OK;
    }

    bool WindowsStillValid() const {
        return IsWindow(cfg.targetWindow) && IsWindow(resolved) &&
            KnownLifetimeMatches(boundLifetime, WindowLifetime(cfg.targetWindow, false)) &&
            KnownLifetimeMatches(capturedLifetime, WindowLifetime(resolved, false));
    }

    // @shape out: owned padded BGRA8 + same-final-frame facts; failure owns no buffer.
    // @source frame: Direct3D11CaptureFrame ContentSize/SystemRelativeTime/Surface.
    int32_t GrabFrame(CapturedFrame& out) {
        if (!started) return RUG_ERR_NOT_INITIALIZED;

        if (!WindowsStillValid()) return RUG_ERR_CAPTURE_FAILED;
        const auto before = SampleWindow(resolved);
        if (!KnownLifetimeMatches(capturedLifetime, before.lifetime)) return RUG_ERR_CAPTURE_FAILED;
        const int64_t beforeQpc = Qpc();
        dpiScale = WindowDpiScale(resolved);
        struct Candidate { SoftwareBitmap pixels{ nullptr }; SizeInt32 content{}; int64_t time100ns = 0; };
        auto read = [&]() -> Candidate {
            auto frame = TryGetLatestFrame();
            if (!frame) return {};
            Candidate c;
            try {
                c.content = frame.ContentSize();
                try { c.time100ns = frame.SystemRelativeTime().count(); } catch (...) { c.time100ns = 0; }
                if (c.content.Width <= 0 || c.content.Height <= 0) { frame.Close(); return {}; }
                if (c.content.Width != poolSize.Width || c.content.Height != poolSize.Height) {
                    poolSize = c.content; frame.Close();
                    framePool.Recreate(winrtDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, poolSize);
                    return {}; // Reacquire: old surface may have been clipped.
                }
                c.pixels = SoftwareBitmap::CreateCopyFromSurfaceAsync(frame.Surface()).get();
                frame.Close(); return c;
            } catch (...) { frame.Close(); throw; }
        };
        auto candidate = ReadCaptureCandidate<Candidate>(read,
            [&](const Candidate& c) { return c.pixels && !IsAllBlack(c.pixels) &&
                (!cfg.exactWindow || FrameAtOrAfter(c.time100ns, beforeQpc, [] { LARGE_INTEGER f{}; QueryPerformanceFrequency(&f); return f.QuadPart; }())); },
            [] { Sleep(100); }, kBlackFrameMaxRetries);
        auto raw = candidate.pixels;
        if (!raw || IsAllBlack(raw)) return RUG_ERR_CAPTURE_FAILED;

        SoftwareBitmap bgra =
            (raw.BitmapPixelFormat() == BitmapPixelFormat::Bgra8)
                ? raw
                : SoftwareBitmap::Convert(raw, BitmapPixelFormat::Bgra8);

        BitmapBuffer buffer = bgra.LockBuffer(BitmapBufferAccessMode::Read);
        auto reference  = buffer.CreateReference();
        auto byteAccess = reference.as<::Windows::Foundation::IMemoryBufferByteAccess>();
        BYTE*  srcData  = nullptr;
        UINT32 capacity = 0;
        HRESULT hr = byteAccess->GetBuffer(&srcData, &capacity);
        if (FAILED(hr) || !srcData) { buffer.Close(); return RUG_ERR_CAPTURE_FAILED; }

        const int32_t w = bgra.PixelWidth();
        const int32_t h = bgra.PixelHeight();
        const auto plane = buffer.GetPlaneDescription(0);
        const int32_t stride = plane.Stride;
        const int64_t len64 = static_cast<int64_t>(stride) * h;
        const int64_t end = static_cast<int64_t>(plane.StartIndex) + (static_cast<int64_t>(h) - 1) * stride + static_cast<int64_t>(w) * 4;
        if (w <= 0 || h <= 0 || stride < static_cast<int64_t>(w) * 4 || plane.StartIndex < 0 ||
            end > capacity || len64 <= 0) { buffer.Close(); return RUG_ERR_CAPTURE_FAILED; }
        if (len64 > 64LL * 1024 * 1024) { buffer.Close(); return RUG_ERR_OUT_OF_MEMORY; }
        const auto len = static_cast<uint32_t>(len64);
        RugCaptureFacts facts{};
        facts.structSize = sizeof(facts); facts.version = 1;
        facts.systemRelative100ns = candidate.time100ns;
        LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency);
        facts.qpcFrequency = frequency.QuadPart;
        const auto after = SampleWindow(resolved);
        // Identity loss rejects pixels, unlike resize/unsupported geometry.
        if (!KnownLifetimeMatches(capturedLifetime, after.lifetime) || !WindowsStillValid()) {
            buffer.Close(); return RUG_ERR_CAPTURE_FAILED;
        }
        facts.geometryBeforeQpc = beforeQpc; facts.geometryAfterQpc = Qpc(); facts.qpcSample = Qpc();
        facts.boundWindow = reinterpret_cast<uintptr_t>(cfg.targetWindow); facts.capturedWindow = reinterpret_cast<uintptr_t>(resolved);
        facts.boundLifetime = boundLifetime; facts.capturedLifetime = capturedLifetime;
        facts.contentWidth = candidate.content.Width; facts.contentHeight = candidate.content.Height;
        facts.bufferWidth = w; facts.bufferHeight = h;
        facts.clientWidth = after.client.right; facts.clientHeight = after.client.bottom;
        facts.clientScreenX = after.origin.x; facts.clientScreenY = after.origin.y;
        facts.imageClientX = after.origin.x - after.bounds.left; facts.imageClientY = after.origin.y - after.bounds.top;
        facts.dpi = static_cast<int32_t>(after.dpi);
        const bool timeValid = ValidFrameTime(facts.systemRelative100ns, facts.qpcSample, facts.qpcFrequency);
        if (timeValid) facts.flags |= 1;
        if (resolved == cfg.targetWindow) facts.flags |= 4;
        facts.geometryStatus = 4;
        if (resolved != cfg.targetWindow) facts.geometryStatus = 3;
        else if (!SameSample(before, after)) facts.geometryStatus = 2;
        else if (!timeValid || !FrameAtOrAfter(candidate.time100ns, beforeQpc, facts.qpcFrequency)) facts.geometryStatus = 5;
        else if (boundLifetime && capturedLifetime && (after.style & WS_OVERLAPPEDWINDOW) == WS_OVERLAPPEDWINDOW && !(after.style & WS_CHILD) &&
            !(after.exStyle & (WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_LAYOUTRTL)) &&
            facts.contentWidth == w && facts.contentHeight == h &&
            w == after.bounds.right - after.bounds.left && h == after.bounds.bottom - after.bounds.top &&
            facts.clientWidth > 0 && facts.clientHeight > 0 && facts.imageClientX >= 0 && facts.imageClientY >= 0 &&
            static_cast<int64_t>(facts.imageClientX) + facts.clientWidth <= w &&
            static_cast<int64_t>(facts.imageClientY) + facts.clientHeight <= h) {
            facts.flags |= 2; facts.geometryStatus = 1;
        }
        if (len > 64u * 1024u * 1024u) { buffer.Close(); return RUG_ERR_OUT_OF_MEMORY; }
        auto dst = std::unique_ptr<uint8_t[]>(new (std::nothrow) uint8_t[len]{});
        if (!dst) { buffer.Close(); return RUG_ERR_OUT_OF_MEMORY; }
        for (int32_t row = 0; row < h; ++row)
            std::memcpy(dst.get() + static_cast<size_t>(row) * stride, srcData + plane.StartIndex + static_cast<size_t>(row) * stride, static_cast<size_t>(w) * 4);
        buffer.Close();
        if (cfg.beforeDeliveryForTesting) cfg.beforeDeliveryForTesting();
        // Last native delivery decision. No allocation or pixel copy follows it.
        // Unknown-at-start tokens stay unknown; known tokens must still match.
        if (!WindowsStillValid()) return RUG_ERR_CAPTURE_FAILED;

        out.data       = dst.release();
        out.dataLength = len;
        out.width      = w;
        out.height     = h;
        out.stride     = stride;
        out.dpiScale   = dpiScale;
        out.facts      = facts;
        return RUG_OK;
    }

    void Stop() {
        if (session)   { try { session.Close(); }   catch (...) {} session   = nullptr; }
        if (framePool) { try { framePool.Close(); } catch (...) {} framePool = nullptr; }
        item        = nullptr;
        winrtDevice = nullptr;
        d3dContext  = nullptr;
        d3dDevice   = nullptr;
        started     = false;
    }
};

// -----------------------------------------------------------------------------
// WgcCapturer
// -----------------------------------------------------------------------------
WgcCapturer::WgcCapturer(Config config)
    : m_impl(std::make_unique<Impl>()) {
    m_impl->cfg = config;
}

WgcCapturer::~WgcCapturer() {
    if (m_impl) m_impl->Stop();
}

int32_t WgcCapturer::Start()     { return m_impl->Start(); }
int32_t WgcCapturer::GrabFrame(CapturedFrame& out) { std::lock_guard lock(m_impl->gate); return m_impl->GrabFrame(out); }
void    WgcCapturer::Stop()      { m_impl->Stop(); }

HWND   WgcCapturer::ResolvedWindow() const { return m_impl->resolved; }
double WgcCapturer::DpiScale()       const { return m_impl->dpiScale; }

}  // namespace rug::core
