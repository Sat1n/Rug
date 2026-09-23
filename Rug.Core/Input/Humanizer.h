// =============================================================================
//  Humanizer.h — humanized trajectory planning + dynamic polling cadence.
// =============================================================================

#ifndef HUMANIZER_H
#define HUMANIZER_H
#pragma once

#include "IInputController.h"

namespace rug::core::input {

class Humanizer {
public:
    // Plan a humanized path from (sx,sy) to (ex,ey): absolute points plus a
    // per-step delay derived from an ease-in-out velocity profile (slow at the
    // ends, fast in the middle). Pure — no system state, safe to call from tests.
    static std::vector<TrajectorySample> Plan(int sx, int sy, int ex, int ey,
                                              TrajectoryType trajectory,
                                              const HumanizeConfig& config);

    // Best-effort wait. Uses timeBeginPeriod(1) + Sleep for coarse delays and a
    // QPC spin for sub-2ms delays. Sub-3ms precision is approximate on Windows.
    static void Wait(float ms);

    // Random integer in [lo, hi] (inclusive).
    static int RandomInt(int lo, int hi);
};

}  // namespace rug::core::input

#endif  // HUMANIZER_H
