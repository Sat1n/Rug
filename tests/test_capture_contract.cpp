#include "TestCommon.h"
#include "../Rug.Core/CaptureContract.h"
#include <cstdio>
#include <cstddef>

int RunCaptureContract() {
    using namespace rug::core;
    struct Candidate { int pixel, time, width; };
    int read = 0, waits = 0;
    auto c = ReadCaptureCandidate<Candidate>([&] { ++read; return Candidate{read == 1 ? 0 : read, read * 100, read * 10}; },
        [](auto f) { return f.pixel != 0; }, [&] { ++waits; }, 5);
    bool ok = c.pixel == 2 && c.time == 200 && c.width == 20 && waits == 1;
    ok &= KnownLifetimeMatches(0, 0) && KnownLifetimeMatches(0, 42) && KnownLifetimeMatches(42, 42) &&
        !KnownLifetimeMatches(42, 0) && !KnownLifetimeMatches(42, 43);
    auto missing = ReadCaptureCandidate<Candidate>([] { return Candidate{}; }, [](auto f) { return f.pixel != 0; }, [] {}, 2);
    ok &= missing.time == 0 && missing.width == 0;
    ok &= ValidFrameTime(100, 200, 10000000) && !ValidFrameTime(0, 200, 10000000) &&
        !ValidFrameTime(201, 200, 10000000) && !ValidFrameTime(1, 2, 0) &&
        FrameAtOrAfter(100, 10, 1000000) && !FrameAtOrAfter(99, 10, 1000000);
    ok &= Qpc100ns(1, 3000000) == 3 && Qpc100ns(1, 3000000, true) == 4 &&
        Qpc100ns(INT64_MAX, 1) == 0 && Qpc100ns(10, INT64_MAX) == 0;
    WindowSample before{}, after{};
    before.valid = after.valid = true; before.lifetime = after.lifetime = 1;
    ok &= SameSample(before, after);
    after.dpi = 144; ok &= !SameSample(before, after); after = before;
    after.client.right++; ok &= !SameSample(before, after); after = before;
    after.monitor = reinterpret_cast<HMONITOR>(1); ok &= !SameSample(before, after); after = before;
    after.lifetime++; ok &= !SameSample(before, after); after = before;
    after.origin.x++; ok &= !SameSample(before, after);
    static_assert(sizeof(RugCaptureFacts) == 136 && offsetof(RugCaptureFacts, systemRelative100ns) == 16);
    static_assert(sizeof(RugFrame) == (sizeof(void*) == 8 ? 32 : 24));
    RugFrame frame{};
    RugCaptureFacts facts{}; facts.structSize = sizeof(facts); facts.version = 2;
    ok &= Rug_GrabFrameWithFacts(nullptr, &frame, &facts) == RUG_ERR_INVALID_PARAM && frame.data == nullptr;
    ok &= Rug_GrabFrame(nullptr, &frame) == RUG_ERR_INVALID_PARAM;
    RugCapturerHandle h = nullptr;
    ok &= Rug_CreateCapturerExact(nullptr, &h) == RUG_ERR_INVALID_PARAM && h == nullptr;
    std::printf("Capture contract %s: complete retry candidate, QPC validation, unchanged RugFrame, additive ABI layout/errors\n", ok ? "PASS" : "FAIL");
    return ok ? 0 : 1;
}
