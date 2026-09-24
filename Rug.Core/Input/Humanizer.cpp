// =============================================================================
//  Humanizer.cpp — ease-in-out trajectory planning with a velocity-driven
//  (dynamic) polling cadence, cubic-Bezier corridor paths, and jitter.
// =============================================================================

#include "pch.h"
#include "Humanizer.h"
#include "RugCoreAbi.h"  // RugStatus codes (not strictly needed here)

#include <cmath>
#include <mutex>
#include <random>
#include <timeapi.h>

#pragma comment(lib, "winmm.lib")

namespace rug::core::input {
namespace {

std::mt19937& Rng() {
    static thread_local std::mt19937 g{ std::random_device{}() };
    return g;
}

float RandF(float lo, float hi) {
    std::uniform_real_distribution<float> d(lo, hi);
    return d(Rng());
}

// Smoothstep position easing and its normalized speed (peak 1.0 at t=0.5).
inline float EaseInOut(float t)   { return t * t * (3.f - 2.f * t); }
inline float SpeedNorm(float t)   { return 4.f * t * (1.f - t); }

inline void CubicBezier(float u,
                        float p0x, float p0y, float p1x, float p1y,
                        float p2x, float p2y, float p3x, float p3y,
                        float& ox, float& oy) {
    const float inv = 1.f - u;
    const float b0 = inv * inv * inv;
    const float b1 = 3.f * inv * inv * u;
    const float b2 = 3.f * inv * u * u;
    const float b3 = u * u * u;
    ox = b0 * p0x + b1 * p1x + b2 * p2x + b3 * p3x;
    oy = b0 * p0y + b1 * p1y + b2 * p2y + b3 * p3y;
}

}  // namespace

int Humanizer::RandomInt(int lo, int hi) {
    if (hi < lo) { int t = lo; lo = hi; hi = t; }
    std::uniform_int_distribution<int> d(lo, hi);
    return d(Rng());
}

std::vector<TrajectorySample> Humanizer::Plan(int sx, int sy, int ex, int ey,
                                              TrajectoryType trajectory,
                                              const HumanizeConfig& config) {
    std::vector<TrajectorySample> out;

    const float dx = static_cast<float>(ex - sx);
    const float dy = static_cast<float>(ey - sy);
    const float dist = std::sqrt(dx * dx + dy * dy);

    if (dist < 1.f) {  // already there
        out.push_back(TrajectorySample{ ex, ey, RandF(1.5f, 3.f) });
        return out;
    }

    int steps = static_cast<int>(std::lround(dist / 10.f));
    if (steps < 10)  steps = 10;
    if (steps > 120) steps = 120;

    const float p0x = static_cast<float>(sx), p0y = static_cast<float>(sy);
    const float p3x = static_cast<float>(ex), p3y = static_cast<float>(ey);

    // Perpendicular-corridor control points for the cubic Bezier.
    float p1x = p0x, p1y = p0y, p2x = p3x, p2y = p3y;
    if (trajectory == TrajectoryType::CubicBezier) {
        const float ux = dx / dist, uy = dy / dist;   // unit along the path
        const float px = -uy, py = ux;                // perpendicular
        const float corr = config.corridorRatio * dist;
        const float o1 = RandF(-corr, corr);
        const float o2 = RandF(-corr, corr);
        p1x = p0x + dx / 3.f + px * o1;  p1y = p0y + dy / 3.f + py * o1;
        p2x = p0x + 2.f * dx / 3.f + px * o2;  p2y = p0y + 2.f * dy / 3.f + py * o2;
    }

    out.reserve(static_cast<size_t>(steps));
    for (int i = 1; i <= steps; ++i) {
        const float t = static_cast<float>(i) / static_cast<float>(steps);
        const float u = EaseInOut(t);

        float x, y;
        if (trajectory == TrajectoryType::CubicBezier)
            CubicBezier(u, p0x, p0y, p1x, p1y, p2x, p2y, p3x, p3y, x, y);
        else { x = p0x + dx * u; y = p0y + dy * u; }

        if (config.enableJitter && i < steps) { x += RandF(-1.f, 1.f); y += RandF(-1.f, 1.f); }
        if (i == steps) { x = p3x; y = p3y; }  // land exactly on target

        const float sn = SpeedNorm(t);
        const float lo = 10.f + (1.5f - 10.f) * sn;  // 10ms (ends) -> 1.5ms (middle)
        const float hi = 25.f + (3.0f - 25.f) * sn;  // 25ms (ends) -> 3.0ms (middle)
        float delay = RandF(lo, hi) + RandF(-0.5f, 0.5f);
        if (delay < 0.5f) delay = 0.5f;

        out.push_back(TrajectorySample{ static_cast<int32_t>(std::lround(x)),
                                        static_cast<int32_t>(std::lround(y)),
                                        delay });
    }
    return out;
}

void Humanizer::Wait(float ms) {
    if (ms <= 0.f) return;
    static std::once_flag once;
    std::call_once(once, [] { timeBeginPeriod(1); });

    if (ms >= 2.f) { ::Sleep(static_cast<DWORD>(ms)); return; }

    LARGE_INTEGER freq, start, now;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&start);
    const double ticks = static_cast<double>(ms) * 0.001 * static_cast<double>(freq.QuadPart);
    do { QueryPerformanceCounter(&now); }
    while (static_cast<double>(now.QuadPart - start.QuadPart) < ticks);
}

}  // namespace rug::core::input
