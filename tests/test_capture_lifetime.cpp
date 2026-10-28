#include "../Rug.Core/WgcCapturer.h"
#include <winrt/base.h>
#include <future>
#include <thread>
#include <chrono>
#include <cstdio>
#include <stdexcept>

namespace {
constexpr wchar_t property[] = L"Rug.Capture.WindowLifetime.v1";
LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_TIMER) { InvalidateRect(hwnd, nullptr, TRUE); UpdateWindow(hwnd); return 0; }
    if (message == WM_ERASEBKGND) {
        RECT r{}; GetClientRect(hwnd, &r);
        auto brush = CreateSolidBrush(RGB(30, 120 + (GetTickCount64() % 80), 40));
        FillRect(reinterpret_cast<HDC>(w), &r, brush); DeleteObject(brush); return 1;
    }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(hwnd, message, w, l);
}
struct Target {
    std::promise<HWND> ready;
    HWND hwnd = nullptr;
    std::thread thread;
    Target(int width, int height) : thread([this, width, height] {
        const auto previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        const auto instance = GetModuleHandleW(nullptr);
        const auto name = L"RugCaptureLifetime" + std::to_wstring(GetCurrentThreadId());
        WNDCLASSW wc{}; wc.lpfnWndProc = WindowProc; wc.hInstance = instance; wc.lpszClassName = name.c_str();
        const bool registered = RegisterClassW(&wc) != 0;
        HWND window = registered ? CreateWindowExW(0, name.c_str(), L"Rug capture lifetime barrier", WS_OVERLAPPEDWINDOW,
            100, 100, width, height, nullptr, nullptr, instance, nullptr) : nullptr;
        if (window) { ShowWindow(window, SW_SHOWNOACTIVATE); UpdateWindow(window); SetTimer(window, 1, 33, nullptr); }
        ready.set_value(window);
        if (window) {
            MSG msg{};
            while (GetMessageW(&msg, nullptr, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessageW(&msg); }
            KillTimer(window, 1);
        }
        if (registered) UnregisterClassW(name.c_str(), instance);
        if (previous) SetThreadDpiAwarenessContext(previous);
    }) { hwnd = ready.get_future().get(); }
    ~Target() { if (hwnd && IsWindow(hwnd)) PostMessageW(hwnd, WM_CLOSE, 0, 0); thread.join(); }
};

bool LifetimeCase(bool exact, bool bound, bool remove) {
    Target boundTarget(500, 380), renderTarget(900, 660);
    if (!boundTarget.hwnd || !renderTarget.hwnd) throw std::runtime_error("controlled capture window creation failed");
    std::promise<void> entered, release;
    auto enteredFuture = entered.get_future(); auto released = release.get_future().share();
    rug::core::WgcCapturer::Config config;
    config.targetWindow = boundTarget.hwnd; config.exactWindow = exact;
    config.beforeDeliveryForTesting = [&] {
        entered.set_value();
        if (released.wait_for(std::chrono::seconds(8)) != std::future_status::ready)
            throw std::runtime_error("capture delivery barrier timed out");
    };
    rug::core::WgcCapturer capture(config);
    if (capture.Start() != RUG_OK) throw std::runtime_error("real WGC start failed");
    if (capture.ResolvedWindow() != (exact ? boundTarget.hwnd : renderTarget.hwnd)) throw std::runtime_error("wrong controlled capture selection");
    const HWND changed = bound ? boundTarget.hwnd : capture.ResolvedWindow();
    const auto original = GetPropW(changed, property);
    if (!original) throw std::runtime_error("controlled lifetime unavailable");
    auto work = std::async(std::launch::async, [&] {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        rug::core::CapturedFrame frame;
        const auto status = capture.GrabFrame(frame);
        const bool rejected = status == RUG_ERR_CAPTURE_FAILED && frame.data == nullptr && frame.facts.boundLifetime == 0;
        delete[] frame.data;
        winrt::uninit_apartment(); return rejected;
    });
    // This is after the real WGC surface AND output buffer have been copied,
    // before delivery. No elapsed-time assumption determines mutation ordering.
    const bool atBarrier = enteredFuture.wait_for(std::chrono::seconds(8)) == std::future_status::ready;
    bool changedToken = false;
    if (atBarrier) {
        if (remove) changedToken = RemovePropW(changed, property) == original;
        else changedToken = SetPropW(changed, property, reinterpret_cast<HANDLE>(reinterpret_cast<uintptr_t>(original) ^ 2)) != FALSE;
    }
    release.set_value();
    const bool rejected = work.get();
    std::printf("Capture in-flight lifetime %s: exact=%d changed=%s mutation=%s (controlled token injection, not HWND reuse)\n",
        atBarrier && changedToken && rejected ? "PASS" : "FAIL", exact, bound ? "bound" : "captured", remove ? "remove" : "replace");
    return atBarrier && changedToken && rejected;
}
}

int RunCaptureLifetime() {
    try {
        bool ok = true;
        for (bool remove : {false, true}) {
            ok &= LifetimeCase(true, true, remove);
            ok &= LifetimeCase(false, true, remove);
            ok &= LifetimeCase(false, false, remove);
        }
        return ok ? 0 : 1;
    } catch (const std::exception& e) { std::printf("Capture lifetime FAIL: %s\n", e.what()); return 1; }
}
