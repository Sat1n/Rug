// Test-only same-process count receiver; never linked into Rug.Core or activated by DllMain.
#include <windows.h>
#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>
#include <stdexcept>

struct Snapshot {
    uint64_t threadId{}, hwnd{}, ready{}, exited{}, timer{}, input{}, device{}, cleanup{}, barrier{}, fault{}, error{}, cleanupError{};
};
static_assert(sizeof(Snapshot) == 96 && sizeof(RAWINPUTDEVICE) == 16);
struct RegistrationCall {
    uint64_t flags{}, beforeTicks{}, afterTicks{}, succeeded{}, error{}, threadId{}, hwnd{};
};
static_assert(sizeof(RegistrationCall) == 56);
uint64_t RegistrationTick() { LARGE_INTEGER t{}; QueryPerformanceCounter(&t); return static_cast<uint64_t>(t.QuadPart); }
struct Failure { uint64_t fault; DWORD error; };

// @shape detached 96-byte snapshot; default usage 6/flags 0x2100, optional test-only no-registration mode.
// @source managed comparison: Rug.UI.Core/Native/WindowsRawInputReceiver.cs#class:WindowsRawInputReceiver
class Receiver {
    static constexpr UINT StopMessage = WM_APP + 1, BarrierMessage = WM_APP + 2;
    static inline std::mutex RegistrationGate;
    static inline std::atomic_uint64_t NextClass{};
    std::mutex _gate;
    Snapshot _snapshot;
    RegistrationCall _registration;
    std::atomic_bool _stop{};
    std::atomic_uint64_t _nextBarrier{};
    const bool _registerKeyboard;
    const bool _deviceNotify;
    const bool _inputSink;
    static bool Relevant(const RAWINPUTDEVICE& d) { return d.usUsagePage == 1 && (d.usUsage == 0 || d.usUsage == 2 || d.usUsage == 6); }
    static std::vector<RAWINPUTDEVICE> Registrations() {
        UINT count = 0;
        if (GetRegisteredRawInputDevices(nullptr, &count, sizeof(RAWINPUTDEVICE)) == UINT_MAX) throw Failure{3, GetLastError()};
        if (count > 1024) throw Failure{3, ERROR_INVALID_DATA};
        if (!count) return {};
        std::vector<RAWINPUTDEVICE> devices(count);
        UINT result = GetRegisteredRawInputDevices(devices.data(), &count, sizeof(RAWINPUTDEVICE));
        if (result == UINT_MAX) throw Failure{3, GetLastError()};
        if (result > devices.size()) throw Failure{3, ERROR_INVALID_DATA};
        devices.resize(result); return devices;
    }
    static void Verify(HWND hwnd, bool enabled, bool inputSink) {
        bool own = false;
        for (auto& d : Registrations()) {
            if (!Relevant(d)) continue;
            if (!enabled) throw Failure{2, ERROR_INVALID_DATA};
            if (d.usUsage != 6 || d.hwndTarget != hwnd || (d.dwFlags & ~RIDEV_DEVNOTIFY) != (inputSink ? RIDEV_INPUTSINK : 0u))
                throw Failure{2, ERROR_INVALID_DATA};
            own = true;
        }
        if (enabled && !own) throw Failure{2, ERROR_INVALID_DATA};
    }
    void Fault(Failure f) noexcept {
        std::lock_guard lock(_gate);
        if (!_snapshot.fault) { _snapshot.fault = f.fault; _snapshot.error = f.error; }
        _snapshot.ready = 0;
    }
    void CleanupError(DWORD error) noexcept {
        std::lock_guard lock(_gate);
        _snapshot.cleanupError = error;
        if (!_snapshot.fault) { _snapshot.fault = 4; _snapshot.error = error; }
    }
    static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM w, LPARAM l) noexcept {
        auto self = reinterpret_cast<Receiver*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            self = static_cast<Receiver*>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        if (self) return self->Handle(hwnd, message, w, l);
        return DefWindowProcW(hwnd, message, w, l);
    }
    // @shape Win32 message -> scalar counters/ownership fault, WM_INPUT always returns zero after cleanup.
    // @source WM_INPUT / RegisterRawInputDevices; no packet pointers retained or managed callbacks.
    LRESULT Handle(HWND hwnd, UINT message, WPARAM w, LPARAM l) noexcept {
        try {
            if (message == StopMessage) { PostQuitMessage(0); return 0; }
            if (message == BarrierMessage) { std::lock_guard lock(_gate); _snapshot.barrier = w; return 0; }
            if (message == WM_TIMER || message == WM_INPUT || message == WM_INPUT_DEVICE_CHANGE) {
                { std::lock_guard lock(_gate);
                    if (message == WM_TIMER) ++_snapshot.timer;
                    if (message == WM_INPUT) ++_snapshot.input;
                    if (message == WM_INPUT_DEVICE_CHANGE) ++_snapshot.device;
                }
                std::lock_guard registrationLock(RegistrationGate); Verify(hwnd, _registerKeyboard, _inputSink);
            }
        } catch (Failure f) { Fault(f); PostQuitMessage(0); }
        catch (...) { Fault({5, ERROR_UNHANDLED_EXCEPTION}); PostQuitMessage(0); }
        // Same count-only semantics as managed control: no GetRawInputData or classification.
        if (message == WM_INPUT) {
            DefWindowProcW(hwnd, message, w, l);
            std::lock_guard lock(_gate); ++_snapshot.cleanup; return 0;
        }
        return DefWindowProcW(hwnd, message, w, l);
    }
public:
    explicit Receiver(bool registerKeyboard = true, bool deviceNotify = true, bool inputSink = true)
        : _registerKeyboard(registerKeyboard), _deviceNotify(deviceNotify), _inputSink(inputSink) {}
    Snapshot Read() { std::lock_guard lock(_gate); return _snapshot; }
    RegistrationCall ReadRegistration() { std::lock_guard lock(_gate); return _registration; }
    // @shape host-owned thread; ready after window/timer and selected registration contract (off is NOT device readiness).
    // @source counterpart: Rug.UI.Core/Native/WindowsRawInputReceiver.cs#class:WindowsRawInputReceiver
    void Run() noexcept {
        const auto instance = GetModuleHandleW(nullptr);
        std::wstring name;
        HWND hwnd = nullptr;
        bool registeredClass = false;
        { std::lock_guard lock(_gate); _snapshot.threadId = GetCurrentThreadId(); }
        try {
            name = L"RugNativeRaw" + std::to_wstring(GetCurrentProcessId()) + L"_" + std::to_wstring(++NextClass);
            WNDCLASSW wc{}; wc.lpfnWndProc = WindowProc; wc.hInstance = instance; wc.lpszClassName = name.c_str();
            if (!RegisterClassW(&wc)) throw Failure{3, GetLastError()};
            registeredClass = true;
            hwnd = CreateWindowExW(0, name.c_str(), name.c_str(), 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, instance, this);
            if (!hwnd) throw Failure{3, GetLastError()};
            { std::lock_guard lock(_gate); _snapshot.hwnd = reinterpret_cast<uint64_t>(hwnd); }
            if (!_stop) {
                std::lock_guard registrationLock(RegistrationGate);
                for (auto& d : Registrations()) if (Relevant(d)) throw Failure{1, ERROR_ALREADY_EXISTS};
                if (_registerKeyboard) {
                    RAWINPUTDEVICE device{1, 6, (_inputSink ? RIDEV_INPUTSINK : 0u) | (_deviceNotify ? RIDEV_DEVNOTIFY : 0u), hwnd};
                    RegistrationCall call{}; call.flags = device.dwFlags; call.threadId = GetCurrentThreadId(); call.hwnd = reinterpret_cast<uint64_t>(hwnd);
                    call.beforeTicks = RegistrationTick();
                    const auto success = RegisterRawInputDevices(&device, 1, sizeof(device));
                    const auto error = success ? ERROR_SUCCESS : GetLastError();
                    call.afterTicks = RegistrationTick(); call.succeeded = success != 0; call.error = error;
                    { std::lock_guard lock(_gate); _registration = call; }
                    if (!success) throw Failure{1, error};
                }
                Verify(hwnd, _registerKeyboard, _inputSink);
            }
            if (!_stop) {
                if (!SetTimer(hwnd, 1, 250, nullptr)) throw Failure{3, GetLastError()};
                { std::lock_guard lock(_gate); _snapshot.ready = 1; }
                if (_stop) PostMessageW(hwnd, StopMessage, 0, 0);
                MSG message{}; int result;
                while ((result = GetMessageW(&message, nullptr, 0, 0)) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
                if (result < 0) throw Failure{3, GetLastError()};
                if (!_stop && !Read().fault) throw Failure{6, ERROR_OPERATION_ABORTED};
            }
        } catch (Failure f) { Fault(f); }
        catch (...) { Fault({5, ERROR_UNHANDLED_EXCEPTION}); }
        if (hwnd) {
            try {
                std::lock_guard registrationLock(RegistrationGate);
                for (auto& d : Registrations()) {
                    if (_registerKeyboard && d.usUsagePage == 1 && d.usUsage == 6 && d.hwndTarget == hwnd) {
                        RAWINPUTDEVICE remove{1, 6, RIDEV_REMOVE, nullptr};
                        if (!RegisterRawInputDevices(&remove, 1, sizeof(remove))) throw Failure{4, GetLastError()};
                    }
                }
            } catch (Failure f) { CleanupError(f.error); }
            catch (...) { CleanupError(ERROR_UNHANDLED_EXCEPTION); }
            KillTimer(hwnd, 1);
            if (!DestroyWindow(hwnd)) CleanupError(GetLastError());
        }
        if (registeredClass && !UnregisterClassW(name.c_str(), instance)) CleanupError(GetLastError());
        { std::lock_guard lock(_gate); _snapshot.hwnd = 0; _snapshot.ready = 0; _snapshot.exited = 1; }
    }
    void Stop() noexcept {
        _stop = true;
        std::lock_guard lock(_gate);
        if (_snapshot.hwnd && !PostMessageW(reinterpret_cast<HWND>(_snapshot.hwnd), StopMessage, 0, 0)) {
            if (!_snapshot.fault) { _snapshot.fault = 3; _snapshot.error = GetLastError(); }
        }
    }
    uint64_t Barrier() noexcept {
        std::lock_guard lock(_gate);
        if (!_snapshot.ready || !_snapshot.hwnd || _stop) return 0;
        const auto sequence = ++_nextBarrier;
        if (!PostMessageW(reinterpret_cast<HWND>(_snapshot.hwnd), BarrierMessage, static_cast<WPARAM>(sequence), 0)) {
            if (!_snapshot.fault) { _snapshot.fault = 3; _snapshot.error = GetLastError(); }
            return 0;
        }
        return sequence;
    }
};

// Test ABI only; not Rug.Core C-ABI. Host creates once, joins Run before destroying/unloading.
extern "C" __declspec(dllexport) void* __cdecl Probe_Create() noexcept { try { return new Receiver(); } catch (...) { return nullptr; } }
// Optional test export; legacy Probe_Create and 96-byte snapshot layout are unchanged.
extern "C" __declspec(dllexport) void* __cdecl Probe_CreateUnregistered() noexcept { try { return new Receiver(false); } catch (...) { return nullptr; } }
// Additional TEST ABI, no change to legacy constructors or snapshots. Modes fixed for receiver lifetime.
extern "C" __declspec(dllexport) void* __cdecl Probe_CreateConfigured(uint32_t registration, uint32_t deviceNotify) noexcept {
    if (registration > 1 || deviceNotify > 1) return nullptr;
    try { return new Receiver(registration != 0, deviceNotify != 0); } catch (...) { return nullptr; }
}
// Test-only foreground/background flag control; previous exports and fixed snapshots remain unchanged.
extern "C" __declspec(dllexport) void* __cdecl Probe_CreateWithFlags(uint32_t registration, uint32_t deviceNotify, uint32_t inputSink) noexcept {
    if (registration > 1 || deviceNotify > 1 || inputSink > 1) return nullptr;
    try { return new Receiver(registration != 0, deviceNotify != 0, inputSink != 0); } catch (...) { return nullptr; }
}
extern "C" __declspec(dllexport) void __cdecl Probe_ReadRegistration(Receiver* p, RegistrationCall* result) noexcept { *result = p->ReadRegistration(); }
extern "C" __declspec(dllexport) void __cdecl Probe_Run(Receiver* p) noexcept { p->Run(); }
extern "C" __declspec(dllexport) void __cdecl Probe_Read(Receiver* p, Snapshot* result) noexcept { *result = p->Read(); }
extern "C" __declspec(dllexport) void __cdecl Probe_Stop(Receiver* p) noexcept { p->Stop(); }
extern "C" __declspec(dllexport) uint64_t __cdecl Probe_Barrier(Receiver* p) noexcept { return p->Barrier(); }
extern "C" __declspec(dllexport) void __cdecl Probe_Destroy(Receiver* p) noexcept { delete p; }
