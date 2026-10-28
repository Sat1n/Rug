// Test-only callback comparison. Installs no Hooks, windows, receivers or input.
#include <windows.h>
#include <array>
#include <atomic>
#include <cstdint>
#include <new>

namespace {
constexpr ULONG_PTR Tag = 0x52554749;
struct Counter {
    std::array<std::atomic<std::uint64_t>, 16> counts{};
    std::atomic<std::uint64_t> thread{}, attached{}, mismatch{};
};
std::atomic<Counter*> active{};
struct Snapshot { std::array<std::uint64_t, 16> counts; std::uint64_t thread, attached, mismatch, frequency; };
static_assert(sizeof(Snapshot) == 160);
void maximum(std::atomic<std::uint64_t>& value, std::uint64_t ticks) noexcept {
    auto old = value.load();
    while (old < ticks && !value.compare_exchange_weak(old, ticks)) {}
}
void timing(Counter& c, std::uint64_t ticks, bool chain) noexcept {
    const auto index = chain ? 10 : 8;
    c.counts[index].fetch_add(ticks); maximum(c.counts[index + 1], ticks);
}
void observe(Counter& c, int code, WPARAM message, const KBDLLHOOKSTRUCT* data) noexcept {
    ++c.counts[0];
    if (code != HC_ACTION) return; // Negative codes never read the pointer.
    ++c.counts[1];
    if (!data) { ++c.counts[7]; return; }
    const bool injected = (data->flags & LLKHF_INJECTED) != 0;
    const bool tagged = data->dwExtraInfo == Tag;
    ++c.counts[injected ? 2 : 3];
    if (!tagged) return;
    ++c.counts[4]; ++c.counts[injected ? 5 : 6];
    // Only aggregate software test keys; never retain human key contents.
    if (data->vkCode != VK_F8 && data->vkCode != 'B') return;
    const bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
    const bool up = message == WM_KEYUP || message == WM_SYSKEYUP;
    if (down || up) ++c.counts[(data->vkCode == VK_F8 ? 12 : 14) + (up ? 1 : 0)];
}
// @shape valid Windows KBDLLHOOKSTRUCT lifetime only; detached aggregate counts.
// @source main.cpp#class:Counter (same classification/forwarding contract).
LRESULT CALLBACK callback(int code, WPARAM w, LPARAM l) noexcept {
    auto* c = active.load();
    if (!c) return CallNextHookEx(nullptr, code, w, l);
    LARGE_INTEGER start{}, forwarding{}, end{};
    QueryPerformanceCounter(&start);
    observe(*c, code, w, reinterpret_cast<const KBDLLHOOKSTRUCT*>(l));
    if (GetCurrentThreadId() != c->thread.load()) ++c->mismatch;
    QueryPerformanceCounter(&forwarding);
    timing(*c, static_cast<std::uint64_t>(forwarding.QuadPart - start.QuadPart), false);
    const auto result = CallNextHookEx(nullptr, code, w, l); // Always forward unchanged.
    QueryPerformanceCounter(&end);
    timing(*c, static_cast<std::uint64_t>(end.QuadPart - forwarding.QuadPart), true);
    return result;
}
}
extern "C" __declspec(dllexport) Counter* __cdecl Hook_Create() noexcept { return new (std::nothrow) Counter; }
extern "C" __declspec(dllexport) DWORD __cdecl Hook_Attach(Counter* c) noexcept {
    if (!c) return ERROR_INVALID_PARAMETER;
    if (c->attached.load() != 0) return ERROR_ALREADY_EXISTS;
    Counter* expected = nullptr;
    // Bind on the existing managed Hook thread before it installs the Hook.
    c->thread = GetCurrentThreadId();
    if (!active.compare_exchange_strong(expected, c)) return ERROR_ALREADY_EXISTS;
    c->attached = 1; return ERROR_SUCCESS;
}
extern "C" __declspec(dllexport) DWORD __cdecl Hook_Detach(Counter* c) noexcept {
    if (!c || GetCurrentThreadId() != c->thread.load()) return ERROR_INVALID_THREAD_ID;
    auto* expected = c;
    if (!active.compare_exchange_strong(expected, nullptr)) return ERROR_INVALID_STATE;
    c->attached = 0; return ERROR_SUCCESS;
}
extern "C" __declspec(dllexport) DWORD __cdecl Hook_Destroy(Counter* c) noexcept {
    if (active.load() == c) return ERROR_BUSY;
    delete c; return ERROR_SUCCESS;
}
extern "C" __declspec(dllexport) HOOKPROC __cdecl Hook_Procedure() noexcept { return callback; }
extern "C" __declspec(dllexport) void __cdecl Hook_Read(Counter* c, Snapshot* result) noexcept {
    if (!c || !result) return;
    for (std::size_t i = 0; i < c->counts.size(); ++i) result->counts[i] = c->counts[i].load();
    result->thread = c->thread.load(); result->attached = c->attached.load(); result->mismatch = c->mismatch.load();
    LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency);
    result->frequency = static_cast<std::uint64_t>(frequency.QuadPart);
}
// Parser-only deterministic test; never emits input or invokes the Hook chain.
extern "C" __declspec(dllexport) void __cdecl Hook_ObserveForTest(Counter* c, int code, WPARAM w, const KBDLLHOOKSTRUCT* data) noexcept {
    if (c) observe(*c, code, w, data);
}
