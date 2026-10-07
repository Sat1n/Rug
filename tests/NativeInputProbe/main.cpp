#include <windows.h>
#include <array>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <filesystem>
#include <future>
#include <iostream>
#include <sstream>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include "../../Rug.Core/include/RugCoreAbi.h"
#include "SendEvidenceSession.h"

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
struct Snapshot {
    Values key, mouse;
    long long f8Down, f8Up, bDown, bUp, move, down, up, keyDown, keyUp;
    long long targetF8Down, targetF8Up, targetBDown, targetBUp;
};
bool TestedKeyDelivered(const Snapshot& before, const Snapshot& after, int vk) {
    if (vk != VK_F8 && vk != 'B') return false;
    const auto down = vk == VK_F8 ? after.targetF8Down - before.targetF8Down : after.targetBDown - before.targetBDown;
    const auto up = vk == VK_F8 ? after.targetF8Up - before.targetF8Up : after.targetBUp - before.targetBUp;
    return down == 1 && up == 1; // Wrong key, missing release or duplicate message cannot stand in for delivery.
}
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
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) {
                ++p->keyDown; if (w == VK_F8) ++p->targetF8Down; if (w == 'B') ++p->targetBDown;
            }
            if (msg == WM_KEYUP || msg == WM_SYSKEYUP) {
                ++p->keyUp; if (w == VK_F8) ++p->targetF8Up; if (w == 'B') ++p->targetBUp;
            }
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
    std::atomic<long long> targetF8Down{}, targetF8Up{}, targetBDown{}, targetBUp{};
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
    Snapshot Read() const { return {key.Read(), mouse.Read(), f8Down, f8Up, bDown, bUp, move, down, up, keyDown, keyUp,
        targetF8Down, targetF8Up, targetBDown, targetBUp}; }
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
std::pair<HWND, DWORD> ParseManagedTarget(const std::string& line) {
    std::istringstream input(line); std::string handle, process, extra;
    Check(static_cast<bool>(input >> handle >> process) && !(input >> extra), "Enter HWND PID only");
    size_t end{}; auto value = std::stoull(handle, &end, 16);
    Check(end == handle.size() && value != 0, "Invalid target HWND");
    auto pid = std::stoull(process, &end, 10);
    Check(end == process.size() && pid > 0 && pid <= MAXDWORD, "Invalid target process ID");
    return {reinterpret_cast<HWND>(static_cast<uintptr_t>(value)), static_cast<DWORD>(pid)};
}
// Diagnostic HWND only, never arbitrary application input. Revalidate before each compound round.
class TargetEndpoint {
    Pump* _native;
    HWND _hwnd;
    DWORD _pid{}, _thread{};
    std::wstring _class;
    long long Metric(UINT index) const {
        DWORD_PTR value{};
        if (!SendMessageTimeoutW(_hwnd, WM_APP + 0x10 + index, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 3000, &value))
            throw Error("Managed metric query (same elevation required)", GetLastError());
        return static_cast<long long>(value);
    }
public:
    explicit TargetEndpoint(Pump& native) : _native(&native), _hwnd(native.Hwnd()) {}
    TargetEndpoint(HWND hwnd, DWORD pid) : _native(nullptr), _hwnd(hwnd), _pid(pid) {
        wchar_t name[256]{}; Check(GetClassNameW(hwnd, name, 256) > 0, "Target class unavailable"); _class = name;
        Check(_class.starts_with(L"RugRawDiagnostic"), "Only Rug managed diagnostic windows are accepted");
        DWORD actual{}; _thread = GetWindowThreadProcessId(hwnd, &actual);
        Check(_thread != 0 && actual == pid && pid != GetCurrentProcessId(), "Target process identity mismatch");
        Validate();
    }
    HWND Hwnd() const { return _hwnd; }
    void Validate() const {
        Check(IsWindow(_hwnd) != 0, "Target window was destroyed");
        if (_native) { Check(_native->Healthy(), "Native target fault"); return; }
        DWORD pid{}; auto thread = GetWindowThreadProcessId(_hwnd, &pid);
        wchar_t name[256]{}, title[256]{};
        Check(pid == _pid && thread == _thread && GetClassNameW(_hwnd, name, 256) > 0 && _class == name
            && GetWindowTextW(_hwnd, title, 256) > 0 && std::wstring(title) == L"Rug Raw Input diagnostics - isolated input target",
            "Managed diagnostic target identity changed");
        Check(Metric(0) == 0x52554744, "Target does not support diagnostic counters");
        Check(Metric(10) == 2, "Target metric protocol must be version 2; rebuild/restart B");
    }
    void Sync() const { Validate(); if (_native) _native->Sync(); }
    Snapshot Read() const {
        Validate(); if (_native) return _native->Read();
        Snapshot s{}; s.move = Metric(1); s.down = Metric(2); s.up = Metric(3); s.keyDown = Metric(4); s.keyUp = Metric(5);
        s.targetF8Down = Metric(6); s.targetF8Up = Metric(7); s.targetBDown = Metric(8); s.targetBUp = Metric(9); return s;
    }
};
void Tests() {
    {
        using namespace rug::core::input::diagnostics;
        rug::diagnostics::SendEvidenceSession session;
        InputSendEvidence producer; Check(producer.Begin().round == 0, "Unarmed evidence recorded");
        session.BeginRound(1);
        producer.End(producer.Begin(), VK_F8, true, false, 1, 1, 0);
        producer.End(producer.Begin(), VK_F8, false, false, 1, 1, 0);
        auto records = session.Records(1);
        auto complete = [&](const auto& r) { return rug::diagnostics::CompleteSendEvidence(r, 1, VK_F8, false, GetCurrentThreadId(), session.Dropped()); };
        Check(complete(records), "Complete send evidence rejected");
        auto bad = records; bad[1].returned = 0; bad[1].error = 5; Check(!complete(bad), "Failed send accepted");
        bad = records; bad[1].virtualKey = 'B'; Check(!complete(bad), "Wrong key accepted");
        bad = records; bad[1].down = 1; Check(!complete(bad), "Missing release accepted");
        bad = records; bad[0].down = 0; bad[1].down = 1; Check(!complete(bad), "Reversed key actions accepted");
        bad = records; bad[1].backend = 1; Check(!complete(bad), "Wrong backend accepted");
        bad = records; bad.pop_back(); Check(!complete(bad), "Partial evidence accepted");
        bad = records; bad[1].before = bad[0].after - 1; Check(!complete(bad), "Overlapping evidence accepted");
        session.BeginRound(2); producer.End(producer.Begin(), 'B', true, true, 1, 0, 1400);
        Check(session.Records(1).size() == 2 && records[1].returned == 1 && session.Records(2)[0].error == 1400, "Round/copy isolation failed");
        for (int i = 0; i < EvidenceCapacity; ++i) producer.End(producer.Begin(), 'B', false, true, 1, 1, 0);
        Check(session.Dropped() == 3 && session.Records(1).size() == 2 && !complete(records), "Overflow was hidden or overwritten");
        SendBuffer invalid{}; Check(!Valid(invalid), "Invalid mapping accepted");
        invalid.magic = EvidenceMagic; invalid.version = EvidenceVersion; invalid.frequency = 1; invalid.ownerPid = GetCurrentProcessId() + 1;
        Check(!Valid(invalid), "Foreign process evidence accepted");
    }
    {
        Pump target(false); auto before = target.Read();
        SendMessageW(target.Hwnd(), WM_KEYDOWN, 'C', 0); SendMessageW(target.Hwnd(), WM_KEYUP, 'C', 0);
        target.Sync(); Check(!TestedKeyDelivered(before, target.Read(), VK_F8), "Generic messages accepted as F8");
        SendMessageW(target.Hwnd(), WM_KEYDOWN, VK_F8, 0); target.Sync();
        Check(!TestedKeyDelivered(before, target.Read(), VK_F8), "Missing release accepted by target");
        SendMessageW(target.Hwnd(), WM_KEYUP, VK_F8, 0); target.Sync();
        Check(TestedKeyDelivered(before, target.Read(), VK_F8) && !TestedKeyDelivered(before, target.Read(), 'B'), "Specific key delivery incorrect");
        SendMessageW(target.Hwnd(), WM_KEYDOWN, VK_F8, 0); target.Sync();
        Check(!TestedKeyDelivered(before, target.Read(), VK_F8), "Duplicate key delivery accepted");
        before = target.Read();
        SendMessageW(target.Hwnd(), WM_SYSKEYDOWN, 'B', 0); SendMessageW(target.Hwnd(), WM_SYSKEYUP, 'B', 0);
        target.Sync(); Check(TestedKeyDelivered(before, target.Read(), 'B'), "System key delivery omitted");
        // Real Rug.Core path, isolated hidden window, background messages only.
        rug::diagnostics::SendEvidenceSession evidence; CoreInput input(target.Hwnd());
        for (int vk : {VK_F8, 0x42}) {
            before = target.Read(); evidence.BeginRound(vk); input.Round(vk, true);
            auto until = std::chrono::steady_clock::now() + std::chrono::seconds(3);
            while (!TestedKeyDelivered(before, target.Read(), vk) && std::chrono::steady_clock::now() < until) std::this_thread::yield();
            Check(TestedKeyDelivered(before, target.Read(), vk), "Real Rug.Core specific target delivery failed");
            Check(rug::diagnostics::CompleteSendEvidence(evidence.Records(vk), vk, vk, true, GetCurrentThreadId(), evidence.Dropped()), "Real EmitKey evidence absent/incomplete; rebuild Rug.Core with -BuildCore");
        }
        target.Stop(); Check(!IsWindow(target.Hwnd()), "Target did not release");
        evidence.BeginRound(999); CoreInput::Ok(input.keyDown(input.Handle(), 'B')); CoreInput::Ok(input.keyUp(input.Handle(), 'B'));
        const auto failed = evidence.Records(999);
        Check(failed.size() == 2 && failed[0].returned == 0 && failed[1].returned == 0 && failed[0].error != 0 && failed[1].error != 0,
            "Actual failed PostMessage error/result evidence missing");
    }
    auto parsed = ParseManagedTarget("0x1234 42"); Check(reinterpret_cast<uintptr_t>(parsed.first) == 0x1234 && parsed.second == 42, "Target parser failed");
    for (const auto& line : {"0 42", "1234 0", "12oops 42", "1234 42x", "1234 42 extra"}) {
        bool rejected = false; try { ParseManagedTarget(line); } catch (const std::exception&) { rejected = true; }
        Check(rejected, "Invalid managed target accepted");
    }
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
    bool rejected = false; try { TargetEndpoint invalid(next.Hwnd(), GetCurrentProcessId()); } catch (const std::exception&) { rejected = true; }
    Check(rejected, "Destroyed/arbitrary target accepted");
    for (int i = 0; i < 3; ++i) { Pump receiver(true); Pump target(false); receiver.Sync(); target.Sync(); receiver.Stop(); target.Stop(); Check(receiver.Clean() && target.Clean() && !IsWindow(target.Hwnd()), "Repeated cleanup failed"); }
    std::cout << "Native counter/layout/lifecycle/send evidence/specific target tests PASS (isolated background messages; physical SendInput NOT RUN)\n";
}
int Run(bool observe, bool human, bool managed = false) {
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
    TargetEndpoint endpoint(target);
    if (managed) {
        std::cout << "MANAGED TARGET: now start B both-raw. Paste its HWND and PID here (example: 0x1234 42); no input before identity checks.\n";
        std::string line; if (!std::getline(std::cin, line)) return 2;
        auto [hwnd, pid] = ParseManagedTarget(line); endpoint = TargetEndpoint(hwnd, pid);
        std::cout << "MANAGED TARGET bound; keep B alive through AUTO and HUMAN AFTER. Sender remains native Rug.Core calls.\n";
    } else ShowWindow(target.Hwnd(), SW_SHOW);
    bool humanBeforeComplete = true;
    if (human) {
        std::cout << "HUMAN BEFORE: use " << (managed ? "MANAGED B" : "native") << " isolated target, keys/mouse and another app; normal shortcuts must work. Return and type next.\n";
        std::string command; if (!std::getline(std::cin, command) || command != "next") return 2;
        Evidence(probe, "HUMAN BEFORE END", previous);
        humanBeforeComplete = previous.key[1] > 0 && previous.mouse[1] > 0;
        if (!humanBeforeComplete) std::cout << "INCONCLUSIVE: missing human keyboard/mouse hook evidence BEFORE; software results cannot accept this session\n";
        std::cout << "Countdown 5 seconds: activate " << (managed ? "MANAGED B" : "native") << " isolated target, then hands off.\n";
        std::this_thread::sleep_for(std::chrono::seconds(5));
    }
    Evidence(probe, "AUTO BEFORE", previous); endpoint.Validate();
    rug::diagnostics::SendEvidenceSession sendEvidence; CoreInput input(endpoint.Hwnd()); bool passed = humanBeforeComplete;
    LONG round = 0;
    for (bool post : {false, true}) for (int vk : {VK_F8, 0x42}) {
        std::string label = std::string(post ? "PostMessage/" : "SendInput/") + (vk == VK_F8 ? "F8" : "B");
        endpoint.Validate();
        if (!post && GetForegroundWindow() != endpoint.Hwnd()) { std::cout << label << " NOT RUN: isolated target is not foreground\n"; passed = false; continue; }
        probe.Sync(); endpoint.Sync(); auto before = probe.Read(); auto targetBefore = endpoint.Read();
        sendEvidence.BeginRound(++round); input.Round(vk, post);
        const auto sends = sendEvidence.Records(round);
        for (const auto& r : sends) std::cout << "SEND_CALL round=" << round << " label=" << label << " api=" << (r.backend ? "PostMessage" : "SendInput")
            << " testedKey=" << (r.virtualKey == VK_F8 ? "F8" : r.virtualKey == 'B' ? "B" : "unexpected") << " action=" << (r.down ? "Down" : "Up")
            << " requested=" << r.requested << " returned=" << r.returned << " beforeTicks=" << r.before << " afterTicks=" << r.after
            << " frequency=" << sendEvidence.Frequency() << " thread=" << r.thread << " failureError=" << r.error << " errorApplicable=" << (r.returned != r.requested) << '\n';
        const bool sendComplete = rug::diagnostics::CompleteSendEvidence(sends, round, vk, post, GetCurrentThreadId(), sendEvidence.Dropped());
        std::cout << label << " SEND_EVIDENCE=" << (sendComplete ? "COMPLETE" : "INCOMPLETE") << " records=" << sends.size() << " dropped=" << sendEvidence.Dropped()
            << "; successful insertion/posting does not prove target processing; error cannot identify UIPI\n";
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5); Snapshot messages{};
        do { probe.Sync(); endpoint.Sync(); messages = endpoint.Read(); if (TestedKeyDelivered(targetBefore, messages, vk) && messages.move > targetBefore.move && messages.down > targetBefore.down && messages.up > targetBefore.up) break;
            std::this_thread::yield(); } while (std::chrono::steady_clock::now() < deadline);
        auto after = probe.Read(); auto key = Delta(after.key, before.key); auto mouse = Delta(after.mouse, before.mouse);
        Print(label + " keyboard DELTA", key); Print(label + " mouse DELTA", mouse);
        auto downs = vk == VK_F8 ? after.f8Down - before.f8Down : after.bDown - before.bDown;
        auto ups = vk == VK_F8 ? after.f8Up - before.f8Up : after.bUp - before.bUp;
        std::cout << label << " tagged key down/up=" << downs << '/' << ups << " targetKeyDown/Up=" << messages.keyDown - targetBefore.keyDown << '/' << messages.keyUp - targetBefore.keyUp
            << " targetMove=" << messages.move - targetBefore.move << " targetMouseDown/Up=" << messages.down - targetBefore.down << '/' << messages.up - targetBefore.up << '\n';
        const auto targetDown = vk == VK_F8 ? messages.targetF8Down - targetBefore.targetF8Down : messages.targetBDown - targetBefore.targetBDown;
        const auto targetUp = vk == VK_F8 ? messages.targetF8Up - targetBefore.targetF8Up : messages.targetBUp - targetBefore.targetBUp;
        bool delivered = TestedKeyDelivered(targetBefore, messages, vk) && messages.move > targetBefore.move && messages.down > targetBefore.down && messages.up > targetBefore.up;
        std::cout << label << " TARGET_EVIDENCE=" << (delivered ? "COMPLETE" : "INCOMPLETE") << " testedKeyDown/Up=" << targetDown << '/' << targetUp << " (includes WM_SYSKEYDOWN/UP)\n";
        bool interference = key[3] > key[6] || mouse[3] > mouse[6];
        if (interference) { std::cout << label << " INTERFERED / NOT RUN\n"; passed = false; continue; }
        bool hookComplete = probe.Healthy() && target.Healthy() && key[7] == 0 && mouse[7] == 0 && (post ? key[4] == 0 && mouse[4] == 0 : key[5] >= 2 && mouse[5] >= 5 && downs >= 1 && ups >= 1);
        std::cout << label << " HOOK_EVIDENCE=" << (hookComplete ? "COMPLETE" : "INCOMPLETE") << " (A/B share Windows low-level Hook mechanism)\n";
        bool complete = sendComplete && delivered && hookComplete;
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
        bool managed = argc == 2 && std::wstring(argv[1]) == L"--managed-target";
        if (argc == 2 && std::wstring(argv[1]) == L"--test") { Tests(); return 0; }
        if (argc > 1 && !observe && !self && !managed) { std::cout << "Usage: Rug.NativeInputProbe.exe [--test|--self-test|--observe|--managed-target]\n"; return 2; }
        return Run(observe, !self, managed);
    } catch (const std::exception& e) { std::cerr << "NATIVE DIAGNOSTIC FAILED: " << e.what() << '\n'; return 1; }
}
