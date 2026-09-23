// =============================================================================
//  Win32InputController.h — software input via SendInput (foreground) or
//  PostMessage (background, when a target HWND is bound).
// =============================================================================

#ifndef WIN32_INPUT_CONTROLLER_H
#define WIN32_INPUT_CONTROLLER_H
#pragma once

#include "IInputController.h"

namespace rug::core::input {

class Win32InputController : public IInputController {
public:
    explicit Win32InputController(HWND hwnd);

    int32_t MouseMove(int x, int y, TrajectoryType trajectory, bool smooth) override;
    int32_t MouseMoveRelative(int dx, int dy, TrajectoryType trajectory, bool smooth) override;
    int32_t MouseDown(MouseButton button) override;
    int32_t MouseUp(MouseButton button) override;
    int32_t Click(MouseButton button, int holdTimeMs) override;
    int32_t DragAndDrop(int startX, int startY, int endX, int endY, TrajectoryType trajectory, bool smooth) override;
    int32_t KeyDown(int virtualKey) override;
    int32_t KeyUp(int virtualKey) override;
    int32_t KeyPress(int virtualKey, int holdTimeMs) override;
    int32_t SendText(const std::string& text) override;

    void SetTargetWindow(HWND hwnd) override;
    void SetHumanizeConfig(const HumanizeConfig& config) override;
    std::vector<TrajectorySample> PlanTrajectory(int startX, int startY, int endX, int endY, TrajectoryType trajectory) const override;

private:
    void EmitMove(int x, int y);
    void EmitButton(bool down, MouseButton button);
    void EmitKey(bool down, int virtualKey);
    void EmitChar(wchar_t ch);
    void CurrentPos(int& x, int& y) const;

    HWND           m_hwnd;
    HumanizeConfig m_cfg;
    mutable int    m_curX = 0;   // last known position (screen px foreground / client px background)
    mutable int    m_curY = 0;
};

}  // namespace rug::core::input

#endif  // WIN32_INPUT_CONTROLLER_H
