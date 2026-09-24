// =============================================================================
//  Win32InputController.h — software input, dual delivery to a bound window:
//    background (default) = PostMessage WM_* with client-space coords;
//    foreground           = SendInput after ClientToScreen mapping.
//  When a window is bound, mouse coords are client-space and clamped to the
//  client rect, so the cursor never leaves the target window.
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
    void SetBackgroundDelivery(bool background) override;
    void SetHumanizeConfig(const HumanizeConfig& config) override;
    std::vector<TrajectorySample> PlanTrajectory(int startX, int startY, int endX, int endY, TrajectoryType trajectory) const override;

private:
    bool UsePost() const { return m_hwnd && m_background; }   // PostMessage vs SendInput
    void ClampClient(int& x, int& y) const;                   // clamp to the bound client rect
    void SendAbsolute(int screenX, int screenY);               // foreground SendInput (screen px)
    void EmitMove(int clientX, int clientY);
    void EmitButton(bool down, MouseButton button);
    void EmitKey(bool down, int virtualKey);
    void EmitChar(wchar_t ch);
    void CurrentPos(int& x, int& y) const;

    HWND           m_hwnd;
    bool           m_background = true;  // default: PostMessage to the bound window
    HumanizeConfig m_cfg;
    mutable int    m_curX = 0;   // last commanded position (client px when bound, else screen px)
    mutable int    m_curY = 0;
};

}  // namespace rug::core::input

#endif  // WIN32_INPUT_CONTROLLER_H
