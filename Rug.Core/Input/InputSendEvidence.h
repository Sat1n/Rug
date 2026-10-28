#pragma once
#include <windows.h>
#include <array>

namespace rug::core::input::diagnostics {
// Private opt-in instrumentation between a native diagnostic EXE and its DLL in
// the SAME process. No C-ABI exports, external input authority or callbacks.
inline constexpr wchar_t ChannelEnvironment[] = L"RUG_INPUT_EVIDENCE_CHANNEL";
inline constexpr DWORD EvidenceMagic = 0x52554745, EvidenceVersion = 1;
inline constexpr LONG EvidenceCapacity = 16;
struct SendRecord {
    volatile LONG published;
    LONG round, backend, virtualKey, down;
    DWORD requested, returned, error, thread;
    LONGLONG before, after;
};
struct SendBuffer {
    DWORD magic, version, ownerPid;
    volatile LONG round, next, dropped;
    LONGLONG frequency;
    std::array<SendRecord, EvidenceCapacity> records;
};
static_assert(sizeof(SendRecord) == 56 && sizeof(SendBuffer) == 928);
inline LONG Read(volatile LONG& value) noexcept { return InterlockedCompareExchange(&value, 0, 0); }
inline bool Valid(const SendBuffer& b) noexcept {
    return b.magic == EvidenceMagic && b.version == EvidenceVersion && b.ownerPid == GetCurrentProcessId() && b.frequency > 0;
}
struct SendStart { LONG round{}; LONGLONG ticks{}; };

class InputSendEvidence {
    HANDLE _mapping{};
    SendBuffer* _buffer{};
public:
    InputSendEvidence() noexcept {
        wchar_t name[128]{};
        const auto length = GetEnvironmentVariableW(ChannelEnvironment, name, 128);
        if (!length || length >= 128 || wcsncmp(name, L"Local\\Rug.InputEvidence.", 24) != 0) return;
        _mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
        if (!_mapping) return;
        _buffer = static_cast<SendBuffer*>(MapViewOfFile(_mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(SendBuffer)));
        if (_buffer && !Valid(*_buffer)) { UnmapViewOfFile(_buffer); _buffer = nullptr; }
    }
    ~InputSendEvidence() { if (_buffer) UnmapViewOfFile(_buffer); if (_mapping) CloseHandle(_mapping); }
    InputSendEvidence(const InputSendEvidence&) = delete;
    InputSendEvidence& operator=(const InputSendEvidence&) = delete;
    SendStart Begin() const noexcept {
        if (!_buffer) return {};
        const auto round = Read(_buffer->round);
        if (round <= 0) return {};
        LARGE_INTEGER ticks{}; QueryPerformanceCounter(&ticks); return {round, ticks.QuadPart};
    }
    // @shape bounded 16 published primitive records; overflow observable, no overwrite.
    // @source Win32InputController.cpp#function:EmitKey (actual native call/return).
    void End(SendStart start, int vk, bool down, bool post, DWORD requested, DWORD returned, DWORD error) noexcept {
        if (!_buffer || start.round <= 0) return;
        LARGE_INTEGER end{}; QueryPerformanceCounter(&end);
        const auto index = InterlockedIncrement(&_buffer->next) - 1;
        if (index < 0 || index >= EvidenceCapacity) { InterlockedIncrement(&_buffer->dropped); return; }
        auto& r = _buffer->records[static_cast<std::size_t>(index)];
        r.round = start.round; r.backend = post ? 1 : 0; r.virtualKey = vk; r.down = down ? 1 : 0;
        r.requested = requested; r.returned = returned; r.error = error; r.thread = GetCurrentThreadId();
        r.before = start.ticks; r.after = end.QuadPart;
        InterlockedExchange(&r.published, index + 1); // Release only after copying all detached fields.
    }
};
}
