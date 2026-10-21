#pragma once
#include <cstdint>
#include <climits>
#include <windows.h>

namespace rug::core {
struct WindowSample {
    RECT bounds{}, client{};
    POINT origin{};
    DWORD pid = 0, tid = 0;
    UINT dpi = 0;
    LONG_PTR style = 0, exStyle = 0;
    HMONITOR monitor = nullptr;
    uint64_t lifetime = 0;
    bool valid = false;
};
inline bool SameSample(const WindowSample& a, const WindowSample& b) {
    return a.valid && b.valid && a.pid == b.pid && a.tid == b.tid && a.lifetime == b.lifetime &&
        a.dpi == b.dpi && a.style == b.style && a.exStyle == b.exStyle && a.monitor == b.monitor &&
        EqualRect(&a.bounds, &b.bounds) && EqualRect(&a.client, &b.client) &&
        a.origin.x == b.origin.x && a.origin.y == b.origin.y;
}

// Entire candidate (pixels + time + content) is replaced on every retry.
template<class Candidate, class Read, class Accept, class Wait>
Candidate ReadCaptureCandidate(Read read, Accept accept, Wait wait, int retries) {
    auto c = read();
    for (int i = 0; !accept(c) && i < retries; ++i) { wait(); c = read(); }
    return c;
}
// Exact quotient/remainder conversion. Reject unexplained frequency/overflow.
inline int64_t Qpc100ns(int64_t ticks, int64_t frequency, bool ceil = false) {
    if (ticks <= 0 || frequency <= 0 || frequency > 1000000000 || ticks / frequency > (INT64_MAX - 10000000) / 10000000) return 0;
    int64_t numerator = (ticks % frequency) * 10000000;
    return (ticks / frequency) * 10000000 + numerator / frequency + (ceil && numerator % frequency ? 1 : 0);
}
inline bool ValidFrameTime(int64_t time, int64_t sample, int64_t frequency) {
    const auto converted = Qpc100ns(sample, frequency);
    return time > 0 && converted > 0 && time <= converted;
}
inline bool FrameAtOrAfter(int64_t time, int64_t before, int64_t frequency) {
    const auto converted = Qpc100ns(before, frequency, true);
    return converted > 0 && time >= converted;
}
}
