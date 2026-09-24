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

        resolved = ResolveRenderWindow(cfg.targetWindow);
        if (!resolved) return RUG_ERR_INVALID_PARAM;

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

    int32_t GrabFrame(CapturedFrame& out) {
        if (!started) return RUG_ERR_NOT_INITIALIZED;

        dpiScale = WindowDpiScale(resolved);

        Direct3D11CaptureFrame frame = TryGetLatestFrame();
        if (!frame) return RUG_ERR_CAPTURE_FAILED;

        SoftwareBitmap raw = SoftwareBitmap::CreateCopyFromSurfaceAsync(frame.Surface()).get();
        frame.Close();

        for (int retry = 0; raw && IsAllBlack(raw) && retry < kBlackFrameMaxRetries; ++retry) {
            Sleep(100);
            Direct3D11CaptureFrame next = TryGetLatestFrame();
            if (!next) break;
            raw = SoftwareBitmap::CreateCopyFromSurfaceAsync(next.Surface()).get();
            next.Close();
        }

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
        // Bgra8 is single-plane; derive the row stride from the buffer capacity
        // (capacity == stride * height) rather than BitmapBuffer::GetPlaneDescriptor,
        // which is not projected in every Windows SDK.
        const int32_t stride = (h > 0) ? static_cast<int32_t>(capacity / static_cast<uint32_t>(h))
                                       : (w * 4);
        const uint32_t len   = static_cast<uint32_t>(stride) * static_cast<uint32_t>(h);
        if (stride < w * 4 || len == 0 || len > capacity) { buffer.Close(); return RUG_ERR_CAPTURE_FAILED; }

        uint8_t* dst = new (std::nothrow) uint8_t[len];
        if (!dst) { buffer.Close(); return RUG_ERR_OUT_OF_MEMORY; }
        std::memcpy(dst, srcData, len);
        buffer.Close();

        out.data       = dst;
        out.dataLength = len;
        out.width      = w;
        out.height     = h;
        out.stride     = stride;
        out.dpiScale   = dpiScale;
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
int32_t WgcCapturer::GrabFrame(CapturedFrame& out) { return m_impl->GrabFrame(out); }
void    WgcCapturer::Stop()      { m_impl->Stop(); }

HWND   WgcCapturer::ResolvedWindow() const { return m_impl->resolved; }
double WgcCapturer::DpiScale()       const { return m_impl->dpiScale; }

}  // namespace rug::core
