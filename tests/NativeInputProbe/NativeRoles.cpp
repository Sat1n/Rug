// Test-only native B/C hosts. No production code, input emission or Raw packet parsing.
#include <windows.h>
#include <bcrypt.h>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <future>
#include <iomanip>
#include <iostream>
#include <memory>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {
static_assert(sizeof(void*) == 8 && sizeof(RAWINPUTDEVICE) == 16);
long long Tick() { LARGE_INTEGER t{}; QueryPerformanceCounter(&t); return t.QuadPart; }
long long Frequency() { LARGE_INTEGER f{}; QueryPerformanceFrequency(&f); return f.QuadPart; }
void Check(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
std::runtime_error WinError(const char* message, DWORD error) { return std::runtime_error(std::string(message) + "; win32Error=" + std::to_string(error)); }
std::filesystem::path ExecutablePath() {
    wchar_t path[32768]{}; const auto n = GetModuleFileNameW(nullptr, path, 32768);
    Check(n > 0 && n < 32768, "Executable path unavailable"); return path;
}
std::string Sha256(const std::filesystem::path& path) {
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{};
    Check(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0, "SHA256 provider failed");
    try {
        Check(BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0, "SHA256 allocation failed");
        std::ifstream input(path, std::ios::binary); Check(input.good(), "SHA256 file unavailable");
        std::array<char, 65536> data{};
        while (input.read(data.data(), data.size()) || input.gcount())
            Check(BCryptHashData(hash, reinterpret_cast<PUCHAR>(data.data()), static_cast<ULONG>(input.gcount()), 0) >= 0, "SHA256 update failed");
        Check(input.eof(), "SHA256 file read failed"); std::array<unsigned char, 32> digest{};
        Check(BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) >= 0, "SHA256 finish failed");
        BCryptDestroyHash(hash); hash = nullptr; BCryptCloseAlgorithmProvider(algorithm, 0); algorithm = nullptr;
        std::ostringstream result; result << std::hex << std::uppercase << std::setfill('0');
        for (auto byte : digest) result << std::setw(2) << static_cast<unsigned>(byte); return result.str();
    } catch (...) { if (hash) BCryptDestroyHash(hash); if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0); throw; }
}
class Module {
    HMODULE _handle{};
public:
    explicit Module(const wchar_t* name) {
        auto path = ExecutablePath().parent_path() / name;
        _handle = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!_handle) throw WinError("Diagnostic DLL load", GetLastError());
        try {
            wchar_t actual[32768]{}; auto n = GetModuleFileNameW(_handle, actual, 32768);
            Check(n > 0 && n < 32768, "Loaded module path unavailable");
            auto hash = Sha256(actual);
            std::wcout << L"ROLE MODULE: processId=" << GetCurrentProcessId() << L" path=" << actual << L" bytes=" << std::filesystem::file_size(actual);
            std::cout << " sha256=" << hash << '\n';
        } catch (...) { FreeLibrary(_handle); _handle = nullptr; throw; }
    }
    ~Module() { if (_handle) FreeLibrary(_handle); }
    Module(const Module&) = delete; Module& operator=(const Module&) = delete;
    HMODULE Handle() const { return _handle; }
    void Retain() { _handle = nullptr; } // Failed cleanup: keep callback code mapped until process exit.
    template<class T> T Export(const char* name) {
        auto address = GetProcAddress(_handle, name); if (!address) throw WinError(name, GetLastError()); return reinterpret_cast<T>(address);
    }
};
struct HookSnapshot { std::array<uint64_t, 16> counts{}; uint64_t thread{}, attached{}, mismatch{}, frequency{}; };
struct RawSnapshot { uint64_t thread{}, hwnd{}, ready{}, exited{}, timer{}, input{}, device{}, cleanup{}, barrier{}, fault{}, error{}, cleanupError{}; };
static_assert(sizeof(HookSnapshot) == 160 && sizeof(RawSnapshot) == 96);
class HookApi {
    Module _module{L"Rug.NativeKeyboardHook.dll"}; void* _counter{};
    using Change = DWORD(__cdecl*)(void*);
    Change _attach = _module.Export<Change>("Hook_Attach"), _detach = _module.Export<Change>("Hook_Detach"), _destroy = _module.Export<Change>("Hook_Destroy");
    using ReadFn = void(__cdecl*)(void*, HookSnapshot*);
    ReadFn _read = _module.Export<ReadFn>("Hook_Read");
public:
    const HOOKPROC procedure = _module.Export<HOOKPROC(__cdecl*)()>("Hook_Procedure")();
    HMODULE owner{};
    HookApi() {
        Check(procedure != nullptr, "Hook procedure absent");
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(procedure), &owner)) throw WinError("Hook module lookup", GetLastError());
        Check(owner == _module.Handle(), "Hook procedure is not owned by callback DLL");
        _counter = _module.Export<void*(__cdecl*)()>("Hook_Create")(); Check(_counter != nullptr, "Hook counter allocation failed");
    }
    ~HookApi() { if (_counter && _destroy(_counter) != ERROR_SUCCESS) _module.Retain(); }
    HookSnapshot Read() const { HookSnapshot s; _read(_counter, &s); return s; }
    void Attach() { const auto e = _attach(_counter); if (e) throw WinError("Hook_Attach", e); }
    void Detach() { const auto e = _detach(_counter); if (e) throw WinError("Hook_Detach", e); }
};
std::vector<RAWINPUTDEVICE> Registrations() {
    UINT count{}; if (GetRegisteredRawInputDevices(nullptr, &count, sizeof(RAWINPUTDEVICE)) == UINT_MAX) throw WinError("Raw registration query", GetLastError());
    Check(count <= 1024, "Raw registration count invalid"); if (!count) return {};
    std::vector<RAWINPUTDEVICE> result(count); const auto n = GetRegisteredRawInputDevices(result.data(), &count, sizeof(RAWINPUTDEVICE));
    if (n == UINT_MAX) throw WinError("Raw registration list", GetLastError()); Check(n <= result.size(), "Raw registration list invalid"); result.resize(n); return result;
}
bool Relevant(const RAWINPUTDEVICE& d) { return d.usUsagePage == 1 && (d.usUsage == 0 || d.usUsage == 2 || d.usUsage == 6); }
unsigned VerifyRegistrations(HWND raw = nullptr) {
    unsigned count{}; for (const auto& d : Registrations()) {
        if (!Relevant(d)) continue; ++count;
        // Match the unchanged receiver's ownership rule; query results need not echo DEVNOTIFY.
        Check(raw && d.usUsage == 6 && d.hwndTarget == raw && (d.dwFlags & ~RIDEV_DEVNOTIFY) == RIDEV_INPUTSINK, "Foreign/missing Raw ownership");
    }
    Check(count == (raw ? 1u : 0u), "Raw registration count mismatch"); return count;
}
// @shape host-owned native thread calls unchanged 96-byte receiver ABI; packet parsing remains disabled.
// @source RawReceiver.cpp#class:Receiver
class RawHost {
    Module _module{L"Rug.NativeRawReceiver.dll"}; void* _receiver{}; std::thread _thread;
    using Action = void(__cdecl*)(void*); using ReadFn = void(__cdecl*)(void*, RawSnapshot*);
    Action _run = _module.Export<Action>("Probe_Run"), _stop = _module.Export<Action>("Probe_Stop"), _destroy = _module.Export<Action>("Probe_Destroy");
    ReadFn _read = _module.Export<ReadFn>("Probe_Read");
    auto BarrierFunction() { return _module.Export<uint64_t(__cdecl*)(void*)>("Probe_Barrier"); }
public:
    RawHost() {
        _receiver = _module.Export<void*(__cdecl*)()>("Probe_Create")(); Check(_receiver != nullptr, "Raw receiver allocation failed");
        try {
            _thread = std::thread([this] { _run(_receiver); });
            const auto until = std::chrono::steady_clock::now() + std::chrono::seconds(5);
            while (true) {
                auto s = Read(); Check(!s.fault && !s.cleanupError && !s.exited, "Raw start failed");
                if (s.ready) { VerifyRegistrations(reinterpret_cast<HWND>(s.hwnd)); break; }
                Check(std::chrono::steady_clock::now() < until, "Raw start timed out"); std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
        } catch (...) { Stop(); _destroy(_receiver); _receiver = nullptr; throw; }
    }
    ~RawHost() { Stop(); if (_receiver) _destroy(_receiver); }
    RawSnapshot Read() const { RawSnapshot s; _read(_receiver, &s); return s; }
    void Stop() { if (_thread.joinable()) { _stop(_receiver); _thread.join(); } }
    void Sync() {
        const auto barrier = BarrierFunction()(_receiver); Check(barrier != 0, "Raw barrier refused");
        const auto until = std::chrono::steady_clock::now() + std::chrono::seconds(3);
        while (true) { auto s = Read(); Check(s.ready && !s.fault && !s.exited && !s.cleanupError, "Raw receiver fault");
            if (s.barrier >= barrier) break; Check(std::chrono::steady_clock::now() < until, "Raw barrier timed out"); std::this_thread::sleep_for(std::chrono::milliseconds(1)); }
        VerifyRegistrations(reinterpret_cast<HWND>(Read().hwnd));
    }
};
std::atomic_uint64_t WindowGeneration{};
// @shape target/Hook are separate native threads; one Hook install command, DLL hmod, same target protocol v2.
// @source main.cpp#class:TargetEndpoint; KeyboardHook.cpp#function:callback
class RoleWindow {
    HookApi* _api; std::thread _thread; std::promise<void> _ready;
    std::atomic<HWND> _hwnd{}; std::atomic<DWORD> _threadId{}, _installError{}; std::atomic_bool _exited{}, _installed{};
    std::atomic<long long> _installBefore{}, _installAfter{};
    std::array<std::atomic<long long>, 11> _metrics{};
    std::mutex _gate; std::string _failure; HHOOK _hook{};
    const uint64_t _generation = ++WindowGeneration;
    static LRESULT CALLBACK Proc(HWND hwnd, UINT msg, WPARAM w, LPARAM l) noexcept {
        auto self = reinterpret_cast<RoleWindow*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (msg == WM_NCCREATE) { self = static_cast<RoleWindow*>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams); SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self)); }
        if (self) {
            if (msg >= WM_APP + 0x10 && msg <= WM_APP + 0x1A) return static_cast<LRESULT>(self->_metrics[msg - WM_APP - 0x10].load());
            if (msg == WM_MOUSEMOVE) ++self->_metrics[1]; if (msg == WM_LBUTTONDOWN) ++self->_metrics[2]; if (msg == WM_LBUTTONUP) ++self->_metrics[3];
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) { ++self->_metrics[4]; if (w == VK_F8) ++self->_metrics[6]; if (w == 'B') ++self->_metrics[8]; }
            if (msg == WM_KEYUP || msg == WM_SYSKEYUP) { ++self->_metrics[5]; if (w == VK_F8) ++self->_metrics[7]; if (w == 'B') ++self->_metrics[9]; }
            if (msg == WM_APP + 5) { try { self->InstallOnThread(); return 1; } catch (const std::exception& e) { self->Fault(e.what()); PostQuitMessage(0); return 0; } }
            if (msg == WM_APP + 1) return 1;
            if (msg == WM_CLOSE) { DestroyWindow(hwnd); return 0; }
            if (msg == WM_DESTROY) { PostQuitMessage(0); return 0; }
        }
        return DefWindowProcW(hwnd, msg, w, l);
    }
    void Fault(const std::string& error) { std::lock_guard lock(_gate); _failure = _failure.empty() ? error : _failure + "; " + error; }
    void InstallOnThread() {
        Check(_api != nullptr && !_exited, "Not a live Hook receiver"); if (_installed) return;
        _api->Attach(); _installBefore = Tick();
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _api->procedure, _api->owner, 0);
        const auto error = _hook ? 0 : GetLastError(); _installAfter = Tick(); _installError = error;
        if (!_hook) { _api->Detach(); throw WinError("Keyboard installation", error); } _installed = true;
    }
    void Run() noexcept {
        _threadId = GetCurrentThreadId(); const auto module = GetModuleHandleW(nullptr);
        const auto name = std::wstring(_api ? L"RugNativeRoleHook" : L"RugRawDiagnosticNativeRole") + std::to_wstring(_generation);
        bool registered{};
        try {
            WNDCLASSW wc{}; wc.lpfnWndProc = Proc; wc.hInstance = module; wc.lpszClassName = name.c_str();
            if (!RegisterClassW(&wc)) throw WinError("Role window class", GetLastError()); registered = true;
            _hwnd = CreateWindowExW(0, name.c_str(), L"Rug Raw Input diagnostics - isolated input target", _api ? 0 : WS_OVERLAPPEDWINDOW,
                400, 300, 640, 400, _api ? HWND_MESSAGE : nullptr, nullptr, module, this);
            if (!_hwnd) throw WinError("Role window creation", GetLastError()); _ready.set_value();
            MSG msg{}; int result;
            while ((result = GetMessageW(&msg, nullptr, 0, 0)) > 0) {
                if (msg.message == WM_APP + 9) throw std::runtime_error("Controlled role thread fault"); TranslateMessage(&msg); DispatchMessageW(&msg);
            }
            if (result < 0) throw WinError("Role GetMessage", GetLastError());
        } catch (const std::exception& e) { Fault(e.what()); try { _ready.set_exception(std::current_exception()); } catch (...) {} }
        if (_hook) {
            if (!UnhookWindowsHookEx(_hook)) Fault(WinError("Keyboard removal", GetLastError()).what());
            else { _hook = nullptr; try { _api->Detach(); } catch (const std::exception& e) { Fault(e.what()); } }
        }
        if (_hwnd && IsWindow(_hwnd.load()) && !DestroyWindow(_hwnd.load())) Fault("Role window cleanup failed"); _hwnd = nullptr;
        if (registered && !UnregisterClassW(name.c_str(), module)) Fault("Role class cleanup failed"); _exited = true;
    }
public:
    explicit RoleWindow(HookApi* api = nullptr) : _api(api) {
        _metrics[0] = 0x52554744; _metrics[10] = 2;
        auto ready = _ready.get_future(); _thread = std::thread([this] { Run(); });
        try { Check(ready.wait_for(std::chrono::seconds(5)) == std::future_status::ready, "Role window startup timeout"); ready.get(); }
        catch (...) { Stop(); throw; }
    }
    ~RoleWindow() { Stop(); }
    HWND Hwnd() const { return _hwnd.load(); }
    DWORD ThreadId() const { return _threadId; }
    bool Installed() const { return _installed; }
    bool Exited() const { return _exited; }
    long long Before() const { return _installBefore; } long long After() const { return _installAfter; }
    std::string Failure() { std::lock_guard lock(_gate); return _failure; }
    void Stop() { if (_thread.joinable()) { if (_hwnd && !_exited && !PostMessageW(_hwnd, WM_CLOSE, 0, 0)) Fault("Role stop post failed"); _thread.join(); } }
    void Sync(UINT message = WM_APP + 1) {
        Check(!_exited && Failure().empty(), "Role window fault/exited"); DWORD_PTR result{};
        if (!SendMessageTimeoutW(_hwnd, message, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 3000, &result)) throw WinError("Role command barrier", GetLastError());
        Check(result == 1 && !_exited && Failure().empty(), "Role command failed");
    }
    void Install() { Sync(WM_APP + 5); }
    void FailForTest() { Check(PostThreadMessageW(_threadId, WM_APP + 9, 0, 0) != 0, "Fault post failed"); }
    long long Metric(unsigned index) const { return _metrics.at(index); }
    void Print(const std::string& label) {
        std::cout << label << " threadId=" << ThreadId() << " hwnd=0x" << std::hex << reinterpret_cast<uintptr_t>(Hwnd()) << std::dec
            << " generation=" << _generation << " installed=" << Installed() << " installError=" << _installError << " exited=" << Exited()
            << " fault=" << (Failure().empty() ? "None" : Failure()) << " hookActualBeforeTicks=" << Before() << " hookActualAfterTicks=" << After() << '\n';
    }
};
const char* CountNames[] = {"callbackTotal", "actionableTotal", "injected", "unflagged", "taggedTotal", "taggedInjected", "taggedUnflagged", "parseErrors", "ownTicks", "ownMaxTicks", "chainTicks", "chainMaxTicks"};
void PrintHook(const std::string& label, const HookSnapshot& current, const HookSnapshot& previous) {
    std::cout << label << " keyboard SNAPSHOT:";
    for (size_t i = 0; i < 12; ++i) std::cout << ' ' << CountNames[i] << '=' << current.counts[i];
    std::cout << '\n';
    std::cout << label << " keyboard DELTA:";
    for (size_t i = 0; i < 12; ++i) std::cout << ' ' << CountNames[i] << '=' << ((i == 9 || i == 11) ? current.counts[i] : current.counts[i] - previous.counts[i]);
    std::cout << '\n' << label << " tagged-key DELTA: F8 down/up=" << current.counts[12] - previous.counts[12] << '/' << current.counts[13] - previous.counts[13]
        << " B down/up=" << current.counts[14] - previous.counts[14] << '/' << current.counts[15] - previous.counts[15] << "; raw software/candidate NOT COLLECTED\n";
    std::cout << label << " tagged-key SNAPSHOT: F8 down/up=" << current.counts[12] << '/' << current.counts[13]
        << " B down/up=" << current.counts[14] << '/' << current.counts[15] << '\n';
}
void PrintRaw(const std::string& label, const RawSnapshot& raw) {
    std::cout << label << " RAW: threadId=" << raw.thread << " hwnd=0x" << std::hex << raw.hwnd << std::dec << " ready=" << raw.ready << " exited=" << raw.exited
        << " timer=" << raw.timer << " input=" << raw.input << " deviceChanges=" << raw.device << " cleanup=" << raw.cleanup << " fault=" << raw.fault
        << " error=" << raw.error << " cleanupError=" << raw.cleanupError << " packetReading=False\n";
}
// @shape B always owns Core; independent B/C owners select target and unchanged Raw DLL/thread/registration.
// @source RawReceiver.cpp#class:Receiver; KeyboardHook.cpp#function:callback
int RunRole(bool isB, char rawOwner = 'B', char targetOwner = 'B') {
    const std::string role = isB ? "B" : "C";
    const bool ownsRaw = (isB ? 'B' : 'C') == rawOwner;
    const bool ownsTarget = (isB ? 'B' : 'C') == targetOwner;
    std::unique_ptr<Module> core; if (isB) core = std::make_unique<Module>(L"Rug.Core.dll");
    std::unique_ptr<RoleWindow> target; if (ownsTarget) target = std::make_unique<RoleWindow>();
    HookApi api; RoleWindow hook(&api);
    std::unique_ptr<RawHost> raw; if (ownsRaw) raw = std::make_unique<RawHost>();
    const auto rawReady = ownsRaw ? Tick() : 0; HookSnapshot previous{};
    if (target) { ShowWindow(target->Hwnd(), SW_SHOW); std::cout << "NATIVE TARGET: hwnd=0x" << std::hex << reinterpret_cast<uintptr_t>(target->Hwnd()) << std::dec << " processId=" << GetCurrentProcessId() << '\n'; }
    auto snapshot = [&](const std::string& phase) {
        Check(!GetModuleHandleW(L"coreclr.dll"), "CLR unexpectedly loaded; native comparison invalid");
        hook.Sync(); if (target) target->Sync(); if (raw) raw->Sync();
        auto h = api.Read(); Check(!h.mismatch && !h.counts[7] && h.attached == (hook.Installed() ? 1u : 0u) && (!hook.Installed() || h.thread == hook.ThreadId()), "Hook binding/parse fault");
        auto r = raw ? raw->Read() : RawSnapshot{}; const auto registrations = VerifyRegistrations(reinterpret_cast<HWND>(r.hwnd));
        if (target) Check(target->ThreadId() != hook.ThreadId(), "Target and Hook threads not separate");
        if (raw) Check(hook.ThreadId() != r.thread && (!target || target->ThreadId() != r.thread), "Raw and window threads not separate");
        Check(isB == (GetModuleHandleW(L"Rug.Core.dll") != nullptr), "Core module loaded in wrong role");
        Check(ownsTarget == (target && IsWindow(target->Hwnd())), "Target missing or created in wrong role");
        Check(ownsRaw == (GetModuleHandleW(L"Rug.NativeRawReceiver.dll") != nullptr), "Raw module loaded in wrong role");
        std::cout << "NATIVE " << role << ' ' << phase << ": processId=" << GetCurrentProcessId() << " architecture=X64 clrLoaded=" << (GetModuleHandleW(L"coreclr.dll") != nullptr)
            << " target=" << ownsTarget << " rawRegistrations=" << registrations << " rawOwner=" << rawOwner << " rawReadyObservedTicks=" << rawReady << " hookInstalled=" << hook.Installed()
            << " hookActualBeforeTicks=" << hook.Before() << " hookActualAfterTicks=" << hook.After() << " observedTicks=" << Tick() << " frequency=" << Frequency() << " emitsInput=False targetOwner=" << targetOwner << '\n';
        hook.Print(phase + " HOOK");
        std::cout << phase << " CALLBACK: boundThreadId=" << h.thread << " attached=" << h.attached << " threadMismatch=" << h.mismatch
            << " callbackAddress=" << reinterpret_cast<void*>(api.procedure) << " callbackModule=" << api.owner << " hookInstallHmod=" << api.owner
            << " windowInstance=" << GetModuleHandleW(nullptr) << " hookType=13 hookThreadParameter=0\n";
        PrintHook(phase, h, previous); previous = h;
        if (raw) PrintRaw(phase, r);
        if (target) { target->Print(phase + " TARGET");
            std::cout << phase << " TARGET KEYS: protocol=" << target->Metric(10) << " F8 down/up=" << target->Metric(6) << '/' << target->Metric(7) << " B down/up=" << target->Metric(8) << '/' << target->Metric(9) << '\n'; }
        std::cout << std::flush;
    };
    snapshot("PREPARED");
    std::cout << "NATIVE " << role << " COMMANDS: install once AFTER both B/C PREPARED; baseline, snapshot, done. No input/reinstallation; message health does not prove Hook observation.\n" << std::flush;
    std::string command; bool done{};
    while (std::getline(std::cin, command)) {
        if (command == "done") { done = true; break; }
        if (command == "install") { snapshot("INSTALL ADMISSION"); hook.Install(); Check(!raw || hook.Before() > rawReady, "Hook installed before Raw readiness"); snapshot("INSTALLED"); }
        else if (command == "baseline" || command == "snapshot") {
            if (!hook.Installed()) std::cout << "NATIVE NOT READY: type install before baseline/snapshot\n" << std::flush; else snapshot(command);
        } else std::cout << "Use install, baseline, snapshot or done.\n" << std::flush;
    }
    const bool complete = done && hook.Installed(); if (!complete) std::cout << "NOT RUN: missing done or native Hook installation\n";
    snapshot("END"); if (raw) raw->Stop(); hook.Stop(); if (target) target->Stop();
    auto h = api.Read(); Check(h.attached == 0 && h.mismatch == 0 && hook.Exited() && hook.Failure().empty(), "Native Hook cleanup fault");
    if (raw) { auto r = raw->Read(); PrintRaw("STOPPED", r); Check(r.exited && !r.hwnd && !r.fault && !r.cleanupError && r.input == r.cleanup, "Native Raw cleanup fault"); }
    Check(!target || (target->Exited() && !target->Hwnd() && target->Failure().empty()), "Native target cleanup fault");
    std::cout << "NATIVE " << role << " CLEANUP: threadExited=1 attached=" << h.attached << " targetHwnd=0 rawRegistrations=" << VerifyRegistrations() << " fault=None\n";
    return complete ? 0 : 2;
}
void Tests() {
    Check(sizeof(HookSnapshot) == 160 && sizeof(RawSnapshot) == 96, "Test DLL layout mismatch");
    for (int i = 0; i < 3; ++i) {
        RoleWindow target; HookApi api; RoleWindow hook(&api); RawHost raw;
        const auto original = raw.Read(); Check(!hook.Installed() && api.Read().attached == 0, "Hook installed before command"); raw.Sync();
        hook.Install(); const auto start = hook.Before(), end = hook.After(); hook.Install();
        Check(hook.Before() == start && hook.After() == end && api.Read().attached == 1 && api.Read().thread == hook.ThreadId(), "Hook repeat/binding failed");
        raw.Sync(); Check(raw.Read().hwnd == original.hwnd && raw.Read().thread == original.thread, "Hook changed Raw identity");
        for (const auto key : {VK_F8, 0x42}) {
            SendMessageW(target.Hwnd(), WM_KEYDOWN, static_cast<WPARAM>(key), 0); SendMessageW(target.Hwnd(), WM_KEYUP, static_cast<WPARAM>(key), 0);
        }
        Check(SendMessageW(target.Hwnd(), WM_APP + 0x10, 0, 0) == 0x52554744 && SendMessageW(target.Hwnd(), WM_APP + 0x1A, 0, 0) == 2
            && target.Metric(6) == 1 && target.Metric(7) == 1 && target.Metric(8) == 1 && target.Metric(9) == 1, "Native target protocol/specific key counters failed");
        // Second receiver must fail without stealing the first registration.
        bool rejected{}; try { RawHost conflict; } catch (const std::exception&) { rejected = true; }
        Check(rejected, "Conflicting Raw receiver accepted"); raw.Sync(); Check(raw.Read().hwnd == original.hwnd, "Raw conflict stole ownership");
        raw.Stop(); hook.Stop(); target.Stop(); auto r = raw.Read();
        Check(r.exited && !r.fault && r.input == r.cleanup && api.Read().attached == 0 && hook.Failure().empty() && target.Failure().empty(), "Native repeated cleanup failed"); VerifyRegistrations();
    }
    { HookApi api; RoleWindow hook(&api); hook.FailForTest(); hook.Stop(); Check(hook.Exited() && !hook.Failure().empty() && api.Read().attached == 0, "Post-ready native fault hidden"); }
    Check(!GetModuleHandleW(L"coreclr.dll"), "CLR unexpectedly loaded");
    std::cout << "Native roles layout/target/one-shot install/Raw conflict/identity/fault/repeated cleanup PASS; physical SendInput and human acceptance NOT RUN\n";
}
}
int wmain(int argc, wchar_t** argv) {
    try {
        if (argc != 2 && argc != 4 && argc != 6) { std::cout << "Usage: Rug.NativeRoleProbe.exe --role-b|--role-c [--raw-owner B|C] [--target-owner B|C], or --test\n"; return 1; }
        const std::wstring argument = argv[1];
        char rawOwner = 'B', targetOwner = 'B'; bool rawSpecified{}, targetSpecified{};
        // Reject unsupported/mixed roles before DLL, thread, window or registration side effects.
        for (int i = 2; i < argc; i += 2) {
            const std::wstring option = argv[i], value = argv[i + 1];
            Check((argument == L"--role-b" || argument == L"--role-c") && (value == L"B" || value == L"C"), "Invalid owner arguments; no startup performed");
            if (option == L"--raw-owner") {
                Check(!rawSpecified, "Duplicate Raw owner; no startup performed"); rawSpecified = true; rawOwner = value == L"B" ? 'B' : 'C';
            } else if (option == L"--target-owner") {
                Check(!targetSpecified, "Duplicate target owner; no startup performed"); targetSpecified = true; targetOwner = value == L"B" ? 'B' : 'C';
            } else { throw std::runtime_error("Invalid owner option; no startup performed"); }
        }
        if (argument == L"--role-b") return RunRole(true, rawOwner, targetOwner);
        if (argument == L"--role-c") return RunRole(false, rawOwner, targetOwner);
        if (argument == L"--test" && argc == 2) { Tests(); return 0; }
        std::cout << "Invalid native role; no startup performed\n"; return 1;
    } catch (const std::exception& e) { std::cout << "NATIVE ROLE FAILED: " << e.what() << '\n'; return 1; }
}
