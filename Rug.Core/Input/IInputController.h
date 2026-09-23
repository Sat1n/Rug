// =============================================================================
//  IInputController.h — unified dual-mode input controller contract (Task 1.5).
//  Win32 software mode (SendInput foreground / PostMessage background) and a
//  KMBox hardware mode. Humanized trajectories + dynamic polling come from the
//  Humanizer. Methods return RugStatus codes (RUG_OK == 0).
// =============================================================================

#ifndef IINPUT_CONTROLLER_H
#define IINPUT_CONTROLLER_H
#pragma once

#include <windows.h>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace rug::core::input {

enum class InputMode : int32_t {
    Win32Software = 0,
    HardwareKMBox = 1,
};

enum class MouseButton : int32_t {
    Left   = 0,
    Right  = 1,
    Middle = 2,
};

enum class TrajectoryType : int32_t {
    Straight    = 0,
    CubicBezier = 1,  // default
};

struct HumanizeConfig {
    int   minClickHoldMs = 80;
    int   maxClickHoldMs = 120;
    int   minKeyHoldMs   = 60;
    int   maxKeyHoldMs   = 100;
    float corridorRatio  = 0.15f;  // bezier control-point perpendicular bias
    bool  enableJitter   = true;   // +-1px positional noise
};

// One planned trajectory step: an absolute point plus the wait AFTER emitting it.
struct TrajectorySample {
    int32_t x = 0;
    int32_t y = 0;
    float   delayMs = 0.f;
};

class IInputController {
public:
    virtual ~IInputController() = default;

    virtual int32_t MouseMove(int x, int y, TrajectoryType trajectory = TrajectoryType::CubicBezier, bool smooth = true) = 0;
    virtual int32_t MouseMoveRelative(int dx, int dy, TrajectoryType trajectory = TrajectoryType::CubicBezier, bool smooth = true) = 0;
    virtual int32_t MouseDown(MouseButton button) = 0;
    virtual int32_t MouseUp(MouseButton button) = 0;
    virtual int32_t Click(MouseButton button, int holdTimeMs = 0) = 0;
    virtual int32_t DragAndDrop(int startX, int startY, int endX, int endY, TrajectoryType trajectory = TrajectoryType::CubicBezier, bool smooth = true) = 0;
    virtual int32_t KeyDown(int virtualKey) = 0;
    virtual int32_t KeyUp(int virtualKey) = 0;
    virtual int32_t KeyPress(int virtualKey, int holdTimeMs = 0) = 0;
    virtual int32_t SendText(const std::string& text) = 0;

    virtual void SetTargetWindow(HWND hwnd) = 0;
    virtual void SetHumanizeConfig(const HumanizeConfig& config) = 0;

    // Pure planning hook used by tests to inspect the dynamic polling cadence.
    virtual std::vector<TrajectorySample> PlanTrajectory(int startX, int startY, int endX, int endY, TrajectoryType trajectory) const = 0;
};

// Factory: create a controller for `mode`. Returns a RugStatus code.
int32_t CreateInputController(InputMode mode, HWND hwnd, std::unique_ptr<IInputController>& out);

}  // namespace rug::core::input

#endif  // IINPUT_CONTROLLER_H
