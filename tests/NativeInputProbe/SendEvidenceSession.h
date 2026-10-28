#pragma once
#include "../../Rug.Core/Input/InputSendEvidence.h"
#include <string>
#include <stdexcept>
#include <algorithm>
#include <vector>

namespace rug::diagnostics {
using namespace rug::core::input::diagnostics;
// Owned by the native sender before creating the real DLL input controller.
class SendEvidenceSession {
    HANDLE _mapping{};
    SendBuffer* _buffer{};
    std::wstring _previous;
    bool _hadPrevious{}, _environmentSet{};
    void Close() noexcept {
        if (_environmentSet) SetEnvironmentVariableW(ChannelEnvironment, _hadPrevious ? _previous.c_str() : nullptr);
        if (_buffer) UnmapViewOfFile(_buffer);
        if (_mapping) CloseHandle(_mapping);
        _buffer = nullptr; _mapping = nullptr; _environmentSet = false;
    }
public:
    SendEvidenceSession() {
        try {
            const DWORD length = GetEnvironmentVariableW(ChannelEnvironment, nullptr, 0);
            if (length) {
                _previous.resize(length); const auto written = GetEnvironmentVariableW(ChannelEnvironment, _previous.data(), length);
                if (!written || written >= length) throw std::runtime_error("Evidence environment changed during setup");
                _previous.resize(written); _hadPrevious = true;
            }
            LARGE_INTEGER ticks{}; QueryPerformanceCounter(&ticks);
            const auto name = L"Local\\Rug.InputEvidence." + std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(ticks.QuadPart);
            _mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(SendBuffer), name.c_str());
            const auto error = GetLastError();
            if (!_mapping || error == ERROR_ALREADY_EXISTS) throw std::runtime_error("Evidence mapping creation/conflict");
            _buffer = static_cast<SendBuffer*>(MapViewOfFile(_mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(SendBuffer)));
            if (!_buffer) throw std::runtime_error("Evidence mapping view failed");
            ZeroMemory(_buffer, sizeof(SendBuffer));
            _buffer->magic = EvidenceMagic; _buffer->version = EvidenceVersion; _buffer->ownerPid = GetCurrentProcessId();
            LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency); _buffer->frequency = frequency.QuadPart;
            if (!SetEnvironmentVariableW(ChannelEnvironment, name.c_str())) throw std::runtime_error("Evidence opt-in failed");
            _environmentSet = true;
        } catch (...) { Close(); throw; }
    }
    ~SendEvidenceSession() { Close(); }
    SendEvidenceSession(const SendEvidenceSession&) = delete;
    SendEvidenceSession& operator=(const SendEvidenceSession&) = delete;
    void BeginRound(LONG round) { if (round <= 0) throw std::invalid_argument("Invalid evidence round"); InterlockedExchange(&_buffer->round, round); }
    LONGLONG Frequency() const { return _buffer->frequency; }
    LONG Dropped() const { return Read(_buffer->dropped); }
    std::vector<SendRecord> Records(LONG round) const {
        std::vector<SendRecord> result;
        const auto count = std::min(Read(_buffer->next), EvidenceCapacity);
        for (LONG i = 0; i < count; ++i) {
            auto& record = _buffer->records[static_cast<std::size_t>(i)];
            if (Read(record.published) != i + 1) throw std::runtime_error("Send evidence record not completely published");
            if (record.round == round) result.push_back(record);
        }
        return result;
    }
};
inline bool CompleteSendEvidence(const std::vector<SendRecord>& records, LONG round, int vk, bool post, DWORD thread, LONG dropped) {
    if (dropped != 0 || records.size() != 2) return false;
    int down = 0, up = 0;
    for (const auto& r : records) {
        if (r.round != round || r.backend != (post ? 1 : 0) || r.virtualKey != vk || r.thread != thread
            || r.requested != 1 || r.returned != 1 || r.error != 0 || r.before <= 0 || r.after < r.before || r.down < 0 || r.down > 1) return false;
        if (r.down) ++down; else ++up;
    }
    return down == 1 && up == 1 && records[0].down == 1 && records[1].down == 0 && records[1].before >= records[0].after;
}
}
