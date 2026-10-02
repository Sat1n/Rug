#include <windows.h>
#include <array>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <filesystem>
#include <future>
#include <iostream>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include "../../Rug.Core/include/RugCoreAbi.h"

namespace {
constexpr ULONG_PTR Tag = 0x52554749u;
static_assert(sizeof(void*) == 8, "This diagnostic is x64 only");
static_assert(offsetof(KBDLLHOOKSTRUCT, flags) == 8 && offsetof(KBDLLHOOKSTRUCT, dwExtraInfo) == 16);
static_assert(offsetof(MSLLHOOKSTRUCT, flags) == 12 && offsetof(MSLLHOOKSTRUCT, dwExtraInfo) == 24);
using Values = std::array<long long, 12>;
const char* Names[] = {"callbackTotal", "actionableTotal", "injected", "unflagged", "taggedTotal", "taggedInjected", "taggedUnflagged", "parseErrors", "ownTicks", "ownMaxTicks", "chainTicks", "chainMaxTicks"};
long long Tick() { LARGE_INTEGER t{}; QueryPerformanceCounter(&t); return t.QuadPart; }
class Counter {
public:
    std::array<std::atomic<long long>, 12> counts{};
    void Record(int code, DWORD flags, ULONG_PTR extra, bool keyboard, bool valid, bool countEntry = true) {
        if (countEntry) ++counts[0]; if (code != HC_ACTION) return;
        ++counts[1]; if (!valid) { ++counts[7]; return; }
        bool injected = (flags & (keyboard ? LLKHF_INJECTED : LLMHF_INJECTED)) != 0;
        bool tagged = extra == Tag;
        ++counts[injected ? 2 : 3];
        if (tagged) { ++counts[4]; ++counts[injected ? 5 : 6]; }
    }
    void Time(long long ticks, bool chain) {
        size_t i = chain ? 10 : 8; counts[i] += ticks;
        auto old = counts[i + 1].load();
        while (old < ticks && !counts[i + 1].compare_exchange_weak(old, ticks)) {}
    }
    Values Read() const { Values v{}; for (size_t i = 0; i < v.size(); ++i) v[i] = counts[i].load(); return v; }
};
Values Delta(const Values& a, const Values& b) {
    Values v{}; for (size_t i = 0; i < v.size(); ++i) v[i] = (i == 9 || i == 11) ? a[i] : a[i] - b[i]; return v;
}
void Print(const std::string& label, const Values& v) {
    std::cout << label; for (size_t i = 0; i < v.size(); ++i) std::cout << ' ' << Names[i] << '=' << v[i]; std::cout << '\n';
}
void Check(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
std::runtime_error Error(const char* message, DWORD code) { return std::runtime_error(std::string(message) + "; win32Error=" + std::to_string(code)); }
std::atomic<long long> Generation{};
struct Snapshot { Values key, mouse; long long f8Down, f8Up, bDown, bUp, move, down, up, keyDown, keyUp; };
class Pump;
thread_local Pump* Current = nullptr;
// Independent native callback and message thread; never feeds managed Raw Input or filters input.
class Pump {
    bool _hooks;
    std::thread _thread;
    std::promise<HWND> _ready;
    HHOOK _keyboard{}, _mouse{};
    std::atomic<HWND> _hwnd{};
    std::atomic<bool> _exited{}, _keyInstalled{}, _mouseInstalled{};
    std::atomic<DWORD> _threadId{}, _keyError{}, _mouseError{};
    std::mutex _errorGate;
    std::string _error, _exitReason;
    static LRESULT CALLBACK Proc(HWND hwnd, UINT msg, WPARAM w, LPARAM l) {
        auto p = Current;
        if (p) {
            if (msg == WM_MOUSEMOVE) ++p->move;
            if (msg == WM_LBUTTONDOWN) ++p->down;
            if (msg == WM_LBUTTONUP) ++p->up;
            if (msg == WM_KEYDOWN) ++p->keyDown;
            if (msg == WM_KEYUP) ++p->keyUp;
        }
        if (msg == WM_APP + 1) return 1; // Pump barrier only; does not certify hook health.
        if (msg == WM_CLOSE) { DestroyWindow(hwnd); return 0; }
        if (msg == WM_DESTROY) { PostQuitMessage(0); return 0; }
        return DefWindowProcW(hwnd, msg, w, l);
    }
    static LRESULT CALLBACK Keyboard(int code, WPARAM w, LPARAM l) { return Current->Hook(code, w, l, true); }
    static LRESULT CALLBACK Mouse(int code, WPARAM w, LPARAM l) { return Current->Hook(code, w, l, false); }
    LRESULT Hook(int code, WPARAM w, LPARAM l, bool keyboard) {
        auto& c = keyboard ? key : mouse;
        ++c.counts[0]; // Entry count precedes any native structure read.
        long long started = Tick();
        DWORD flags{}; ULONG_PTR extra{}; bool valid = code != HC_ACTION || l != 0;
        if (code == HC_ACTION && l) {
            if (keyboard) {
                auto d = reinterpret_cast<const KBDLLHOOKSTRUCT*>(l); flags = d->flags; extra = d->dwExtraInfo;
                // Only aggregate the known, tagged synthetic keys, never human key sequences.
                if (extra == Tag && (d->vkCode == VK_F8 || d->vkCode == 'B')) {
                    bool released = (flags & LLKHF_UP) != 0;
                    auto& count = d->vkCode == VK_F8 ? (released ? f8Up : f8Down) : (released ? bUp : bDown); ++count;
                }
            } else { auto d = reinterpret_cast<const MSLLHOOKSTRUCT*>(l); flags = d->flags; extra = d->dwExtraInfo; }
        }
        c.Record(code, flags, extra, keyboard, valid, false);
        auto forwarding = Tick(); c.Time(forwarding - started, false);
        auto result = CallNextHookEx(nullptr, code, w, l);
        c.Time(Tick() - forwarding, true); return result;
    }
    void Fault(const std::string& message) { std::lock_guard lock(_errorGate); _error = _error.empty() ? message : _error + "; " + message; _exitReason = "Fault"; }
    void Run() noexcept {
        Current = this; _threadId = GetCurrentThreadId();
        auto module = GetModuleHandleW(nullptr);
        std::wstring name = L"RugIndependentProbe" + std::to_wstring(generation);
        bool registered = false;
        try {
            WNDCLASSW wc{}; wc.lpfnWndProc = Proc; wc.hInstance = module; wc.lpszClassName = name.c_str();
            if (!RegisterClassW(&wc)) throw Error("RegisterClass", GetLastError()); registered = true;
            auto hwnd = CreateWindowExW(0, name.c_str(), L"Rug independent native probe - isolated target", _hooks ? 0 : WS_OVERLAPPEDWINDOW,
                300, 200, 460, 300, _hooks ? HWND_MESSAGE : nullptr, nullptr, module, nullptr);
            if (!hwnd) throw Error("CreateWindow", GetLastError()); _hwnd = hwnd;
            if (_hooks) {
                _keyboard = SetWindowsHookExW(WH_KEYBOARD_LL, Keyboard, module, 0);
                _keyError = _keyboard ? 0 : GetLastError();
                if (!_keyboard) throw Error("Keyboard installation", _keyError.load()); _keyInstalled = true;
                _mouse = SetWindowsHookExW(WH_MOUSE_LL, Mouse, module, 0);
                _mouseError = _mouse ? 0 : GetLastError();
                if (!_mouse) throw Error("Mouse installation", _mouseError.load()); _mouseInstalled = true;
            }
            _ready.set_value(hwnd);
            MSG msg{}; int status;
            while ((status = GetMessageW(&msg, nullptr, 0, 0)) > 0) {
                if (msg.message == WM_APP + 2) throw std::runtime_error("Controlled thread failure");
                TranslateMessage(&msg); DispatchMessageW(&msg);
            }
            if (status < 0) throw Error("GetMessage", GetLastError());
            { std::lock_guard lock(_errorGate); _exitReason = "WM_QUIT"; }
        } catch (const std::exception& e) { Fault(e.what()); try { _ready.set_exception(std::current_exception()); } catch (...) {} }
        if (_keyboard && !UnhookWindowsHookEx(_keyboard)) Fault("Keyboard removal; win32Error=" + std::to_string(GetLastError()));
        if (_mouse && !UnhookWindowsHookEx(_mouse)) Fault("Mouse removal; win32Error=" + std::to_string(GetLastError()));
        if (_hwnd && IsWindow(_hwnd.load())) DestroyWindow(_hwnd.load());
        if (registered && !UnregisterClassW(name.c_str(), module)) Fault("Class removal failed");
        _exited = true; Current = nullptr;
    }
public:
    const long long generation = ++Generation;
    Counter key, mouse;
    std::atomic<long long> f8Down{}, f8Up{}, bDown{}, bUp{}, move{}, down{}, up{}, keyDown{}, keyUp{};
    explicit Pump(bool hooks) : _hooks(hooks) {
        auto ready = _ready.get_future(); _thread = std::thread([this] { Run(); });
        try { Check(ready.wait_for(std::chrono::seconds(5)) == std::future_status::ready, "Probe startup timed out"); ready.get(); }
        catch (...) { Stop(); throw; }
    }
    ~Pump() { Stop(); }
    HWND Hwnd() const { return _hwnd.load(); }
    bool Clean() { std::lock_guard lock(_errorGate); return _exited && _error.empty(); }
    bool Healthy() { std::lock_guard lock(_errorGate); return !_exited && _error.empty(); }
    void Stop() {
        if (!_thread.joinable()) return;
        if (_hwnd && !_exited) PostMessageW(_hwnd.load(), WM_CLOSE, 0, 0);
        _thread.join(); // Console diagnostic only; no host UI waits or runtime resources.
    }
    void Sync() {
        Check(Healthy(), "Probe thread failed/exited"); DWORD_PTR result{};
        if (!SendMessageTimeoutW(Hwnd(), WM_APP + 1, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 3000, &result)) throw Error("Probe barrier", GetLastError());
        Check(Healthy(), "Probe failed during barrier");
    }
    void FailForTest() { PostMessageW(Hwnd(), WM_APP + 2, 0, 0); }
    Snapshot Read() const { return {key.Read(), mouse.Read(), f8Down, f8Up, bDown, bUp, move, down, up, keyDown, keyUp}; }
    void Status(const std::string& label) {
        std::lock_guard lock(_errorGate);
        LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency);
        std::cout << label << " generation=" << generation << " installThreadId=" << _threadId << " architecture=X64 keyboardInstalled=" << _keyInstalled
            << " keyboardError=" << _keyError << " mouseInstalled=" << _mouseInstalled << " mouseError=" << _mouseError
            << " threadExited=" << _exited << " failure=" << (_error.empty() ? "None" : _error) << " exitReason=" << _exitReason << " frequency=" << frequency.QuadPart << '\n';
    }
};
void Evidence(Pump& probe, const std::string& label, Snapshot& previous) {
    probe.Sync(); auto v = probe.Read(); probe.Status(label);
    Print(label + " keyboard SNAPSHOT", v.key); Print(label + " mouse SNAPSHOT", v.mouse);
    Print(label + " keyboard DELTA", Delta(v.key, previous.key)); Print(label + " mouse DELTA", Delta(v.mouse, previous.mouse));
    std::cout << label << " tagged F8 down/up=" << v.f8Down - previous.f8Down << '/' << v.f8Up - previous.f8Up
        << " B down/up=" << v.bDown - previous.bDown << '/' << v.bUp - previous.bUp << '\n'; previous = v;
}
class CoreInput {
    HMODULE _library{}; RugInputControllerHandle _handle{};
    template<class T> T Load(const char* name) { auto f = GetProcAddress(_library, name); if (!f) throw Error(name, GetLastError()); return reinterpret_cast<T>(f); }
public:
    decltype(&Rug_DestroyInputController) destroy{};
    decltype(&Rug_Input_SetBackgroundDelivery) background{};
    decltype(&Rug_Input_MouseMove) move{};
    decltype(&Rug_Input_Click) click{};
    decltype(&Rug_Input_MouseUp) release{};
    decltype(&Rug_Input_KeyDown) keyDown{};
    decltype(&Rug_Input_KeyUp) keyUp{};
    explicit CoreInput(HWND hwnd) {
        wchar_t path[32768]{}; Check(GetModuleFileNameW(nullptr, path, 32768) != 0, "Executable path unavailable");
        auto dll = std::filesystem::path(path).parent_path() / L"Rug.Core.dll";
        _library = LoadLibraryExW(dll.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!_library) throw Error("Load Rug.Core", GetLastError());
        try {
            wchar_t loaded[32768]{};
            auto length = GetModuleFileNameW(_library, loaded, 32768);
            Check(length > 0 && length < 32768, "Loaded core module path unavailable");
            std::wcout << L"CORE LOADED: processId=" << GetCurrentProcessId() << L" path=" << loaded
                << L" bytes=" << std::filesystem::file_size(loaded) << L'\n';
            destroy = Load<decltype(destroy)>("Rug_DestroyInputController"); background = Load<decltype(background)>("Rug_Input_SetBackgroundDelivery");
            move = Load<decltype(move)>("Rug_Input_MouseMove"); click = Load<decltype(click)>("Rug_Input_Click"); release = Load<decltype(release)>("Rug_Input_MouseUp");
            keyDown = Load<decltype(keyDown)>("Rug_Input_KeyDown"); keyUp = Load<decltype(keyUp)>("Rug_Input_KeyUp");
            auto create = Load<decltype(&Rug_CreateInputController)>("Rug_CreateInputController");
            Check(create(0, hwnd, &_handle) == RUG_OK, "Native controller creation failed");
        } catch (...) { FreeLibrary(_library); _library = nullptr; throw; }
    }
    ~CoreInput() { if (_handle) destroy(_handle); if (_library) FreeLibrary(_library); }
    RugInputControllerHandle Handle() const { return _handle; }
    static void Ok(int32_t status) { if (status != RUG_OK) throw std::runtime_error("Rug input failed; status=" + std::to_string(status)); }
    void Round(int vk, bool post) {
        Ok(background(_handle, post ? 1 : 0));
        try {
            Ok(move(_handle, 60, 60, 0, 0)); Ok(move(_handle, 80, 80, 0, 0)); Ok(click(_handle, 0, 10)); Ok(keyDown(_handle, vk));
        } catch (...) { keyUp(_handle, vk); release(_handle, 0); throw; }
        auto keyStatus = keyUp(_handle, vk); auto mouseStatus = release(_handle, 0); Ok(keyStatus); Ok(mouseStatus);
    }
};
void Tests() {
    Counter c; c.Record(-1, 0, 0, true, false); Check(c.Read()[0] == 1 && c.Read()[1] == 0 && c.Read()[7] == 0, "Negative code classification failed");
    for (bool injected : {false, true}) for (bool tagged : {false, true}) c.Record(0, injected ? LLKHF_INJECTED : 0, tagged ? Tag : 0, true, true);
    c.Record(0, 0, 0, true, false); auto v = c.Read();
    Check(v[0] == 6 && v[1] == 5 && v[2] == 2 && v[3] == 2 && v[4] == 2 && v[5] == 1 && v[6] == 1 && v[7] == 1, "Counter intersections failed");
    Counter mouse; mouse.Record(0, LLMHF_INJECTED, Tag, false, true); mouse.Record(0, 0, Tag, false, true);
    mouse.Record(1, 0, 0, false, false); auto m = mouse.Read();
    Check(m[0] == 3 && m[1] == 2 && m[2] == 1 && m[3] == 1 && m[4] == 2 && m[5] == 1 && m[6] == 1 && m[7] == 0, "Mouse/non-action classification failed");
    c.Time(12, false); c.Time(4, false); c.Time(18, true);
    Check(c.Read()[8] == 16 && c.Read()[9] == 12 && c.Read()[10] == 18, "Counter timing failed");
    Pump probe(true); auto initial = probe.Read(); probe.Sync(); Check(Delta(probe.Read().key, initial.key)[7] == 0, "Probe parse fault");
    probe.FailForTest(); probe.Stop(); Check(!probe.Healthy(), "Post-start thread fault hidden"); probe.Status("CONTROLLED FAILURE");
    Pump next(true); Check(next.generation > probe.generation, "Generation reused"); next.Sync(); next.Stop(); next.Status("CLEANUP"); Check(next.Clean() && !IsWindow(next.Hwnd()), "Native hook/window cleanup failed");
    for (int i = 0; i < 3; ++i) { Pump receiver(true); Pump target(false); receiver.Sync(); target.Sync(); receiver.Stop(); target.Stop(); Check(receiver.Clean() && target.Clean() && !IsWindow(target.Hwnd()), "Repeated cleanup failed"); }
    std::cout << "Native counter/layout/thread lifecycle tests PASS (no synthetic input)\n";
}
int Run(bool observe, bool human) {
    Pump probe(true); Pump target(false); Snapshot previous{}; Evidence(probe, "STARTED", previous);
    if (observe) {
        std::cout << "OBSERVER ONLY: no synthesized input. Run managed diagnostics in another terminal, use baseline before its AUTO, snapshot after AUTO, then done. Keep managed isolated target foreground during AUTO.\n";
        std::string command;
        while (std::getline(std::cin, command) && command != "done") {
            if (command == "baseline" || command == "snapshot") Evidence(probe, command, previous);
            else std::cout << "Use baseline, snapshot or done.\n";
        }
        Evidence(probe, "OBSERVER END", previous); probe.Stop(); target.Stop(); probe.Status("STOPPED"); Check(probe.Clean() && target.Clean(), "Observer cleanup failed"); return 0;
    }
    ShowWindow(target.Hwnd(), SW_SHOW);
    bool humanBeforeComplete = true;
    if (human) {
        std::cout << "HUMAN BEFORE: use isolated native target, keys/mouse and another app; normal shortcuts must work. Return and type next.\n";
        std::string command; if (!std::getline(std::cin, command) || command != "next") return 2;
        Evidence(probe, "HUMAN BEFORE END", previous);
        humanBeforeComplete = previous.key[1] > 0 && previous.mouse[1] > 0;
        if (!humanBeforeComplete) std::cout << "INCONCLUSIVE: missing human keyboard/mouse hook evidence BEFORE; software results cannot accept this session\n";
        std::cout << "Countdown 5 seconds: activate native isolated target, then hands off.\n";
        std::this_thread::sleep_for(std::chrono::seconds(5));
    }
    Evidence(probe, "AUTO BEFORE", previous); CoreInput input(target.Hwnd()); bool passed = humanBeforeComplete;
    for (bool post : {false, true}) for (int vk : {VK_F8, 0x42}) {
        std::string label = std::string(post ? "PostMessage/" : "SendInput/") + (vk == VK_F8 ? "F8" : "B");
        if (!post && GetForegroundWindow() != target.Hwnd()) { std::cout << label << " NOT RUN: isolated target is not foreground\n"; passed = false; continue; }
        probe.Sync(); target.Sync(); auto before = probe.Read(); auto targetBefore = target.Read();
        input.Round(vk, post);
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5); Snapshot messages{};
        do { probe.Sync(); target.Sync(); messages = target.Read(); if (messages.keyDown > targetBefore.keyDown && messages.keyUp > targetBefore.keyUp && messages.move > targetBefore.move && messages.down > targetBefore.down && messages.up > targetBefore.up) break;
            std::this_thread::yield(); } while (std::chrono::steady_clock::now() < deadline);
        auto after = probe.Read(); auto key = Delta(after.key, before.key); auto mouse = Delta(after.mouse, before.mouse);
        Print(label + " keyboard DELTA", key); Print(label + " mouse DELTA", mouse);
        auto downs = vk == VK_F8 ? after.f8Down - before.f8Down : after.bDown - before.bDown;
        auto ups = vk == VK_F8 ? after.f8Up - before.f8Up : after.bUp - before.bUp;
        std::cout << label << " tagged key down/up=" << downs << '/' << ups << " targetKeyDown/Up=" << messages.keyDown - targetBefore.keyDown << '/' << messages.keyUp - targetBefore.keyUp << '\n';
        bool delivered = messages.keyDown > targetBefore.keyDown && messages.keyUp > targetBefore.keyUp && messages.move > targetBefore.move && messages.down > targetBefore.down && messages.up > targetBefore.up;
        bool interference = key[3] > key[6] || mouse[3] > mouse[6];
        if (interference) { std::cout << label << " INTERFERED / NOT RUN\n"; passed = false; continue; }
        bool complete = delivered && probe.Healthy() && target.Healthy() && key[7] == 0 && mouse[7] == 0 && (post ? key[4] == 0 && mouse[4] == 0 : key[5] >= 2 && mouse[5] >= 5 && downs >= 1 && ups >= 1);
        std::cout << label << (complete ? " native Hook verification PASS\n" : " INCONCLUSIVE: incomplete native hook/target evidence\n"); passed &= complete;
    }
    Evidence(probe, "AUTO AFTER", previous);
    if (human) {
        auto before = probe.Read(); std::cout << "HUMAN AFTER: repeat human keyboard/mouse checks, return and type done.\n";
        std::string command; std::getline(std::cin, command); Evidence(probe, "HUMAN AFTER END", previous);
        Check(command == "done" && previous.key[1] > before.key[1] && previous.mouse[1] > before.mouse[1], "Incomplete human AFTER evidence");
    } else std::cout << "HUMAN acceptance NOT RUN (--self-test)\n";
    probe.Stop(); target.Stop(); probe.Status("STOPPED"); Check(probe.Clean() && target.Clean(), "Probe cleanup failed"); return passed ? 0 : 2;
}
}
int wmain(int argc, wchar_t** argv) {
    try {
        bool observe = argc == 2 && std::wstring(argv[1]) == L"--observe";
        bool self = argc == 2 && std::wstring(argv[1]) == L"--self-test";
        if (argc == 2 && std::wstring(argv[1]) == L"--test") { Tests(); return 0; }
        if (argc > 1 && !observe && !self) { std::cout << "Usage: Rug.NativeInputProbe.exe [--test|--self-test|--observe]\n"; return 2; }
        return Run(observe, !self);
    } catch (const std::exception& e) { std::cerr << "NATIVE DIAGNOSTIC FAILED: " << e.what() << '\n'; return 1; }
}
