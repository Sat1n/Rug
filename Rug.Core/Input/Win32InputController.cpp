// =============================================================================
//  Win32InputController.cpp
//  Coordinates are CLIENT-SPACE of the bound window and clamped to its client
//  rect on every emitted point, so the cursor never leaves the target window.
//    background (default): PostMessage WM_* with client coords.
//    foreground:           ClientToScreen -> SendInput absolute (single-monitor).
//  With no bound window, coords are absolute screen pixels (foreground only).
// =============================================================================

#include "pch.h"
#include "Win32InputController.h"
#include "Humanizer.h"
#include "RugCoreAbi.h"  // RugStatus codes

#include <algorithm>
#include <string>

namespace rug::core::input {

Win32InputController::Win32InputController(HWND hwnd) : m_hwnd(hwnd) {
    m_curX = 0; m_curY = 0;
}

void Win32InputController::SetTargetWindow(HWND hwnd) {
    m_hwnd = hwnd;
    m_curX = 0; m_curY = 0;   // restart tracking at the client origin
}

void Win32InputController::SetBackgroundDelivery(bool background) { m_background = background; }

void Win32InputController::SetHumanizeConfig(const HumanizeConfig& config) { m_cfg = config; }

std::vector<TrajectorySample> Win32InputController::PlanTrajectory(
    int sx, int sy, int ex, int ey, TrajectoryType trajectory) const {
    return Humanizer::Plan(sx, sy, ex, ey, trajectory, m_cfg);
}

void Win32InputController::CurrentPos(int& x, int& y) const {
    if (m_hwnd) { x = m_curX; y = m_curY; return; }  // bound: tracked client pos
    POINT p{};
    if (GetCursorPos(&p)) { x = p.x; y = p.y; }
    else { x = m_curX; y = m_curY; }
}

void Win32InputController::ClampClient(int& x, int& y) const {
    if (!m_hwnd) return;
    RECT rc{};
    if (!GetClientRect(m_hwnd, &rc)) return;
    const int w = rc.right - rc.left;
    const int h = rc.bottom - rc.top;
    if (w > 0) x = std::clamp(x, 0, w - 1);
    if (h > 0) y = std::clamp(y, 0, h - 1);
}

void Win32InputController::SendAbsolute(int screenX, int screenY) {
    // Normalize against the VIRTUAL screen (the bounding box of ALL monitors) so
    // windows on negative-coordinate secondary displays map correctly. Without
    // MOUSEEVENTF_VIRTUALDESK the 0..65535 range covers only the primary monitor
    // and the cursor would fly to its edge. The host process is Per-Monitor V2
    // DPI-aware, so these metrics and screenX/screenY are all physical pixels.
    const int vLeft   = GetSystemMetrics(SM_XVIRTUALSCREEN);
    const int vTop    = GetSystemMetrics(SM_YVIRTUALSCREEN);
    const int vWidth  = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    const int vHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

    INPUT in{};
    in.type = INPUT_MOUSE;
    in.mi.dx = static_cast<LONG>(static_cast<double>(screenX - vLeft) * 65535.0 / (vWidth  > 1 ? vWidth  - 1 : 1));
    in.mi.dy = static_cast<LONG>(static_cast<double>(screenY - vTop ) * 65535.0 / (vHeight > 1 ? vHeight - 1 : 1));
    in.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
    SendInput(1, &in, sizeof(INPUT));
}

// --- low-level emitters ------------------------------------------------------

void Win32InputController::EmitMove(int x, int y) {
    if (m_hwnd) {
        ClampClient(x, y);                       // confine every point to the window
        if (m_background) {
            PostMessageW(m_hwnd, WM_MOUSEMOVE, 0, MAKELPARAM(x, y));
        } else {
            POINT p{ x, y };
            ClientToScreen(m_hwnd, &p);          // client -> screen for SendInput
            SendAbsolute(p.x, p.y);
        }
    } else {
        SendAbsolute(x, y);                      // unbound: treat as screen coords
    }
    m_curX = x; m_curY = y;
}

void Win32InputController::EmitButton(bool down, MouseButton button) {
    if (UsePost()) {
        UINT msg; WPARAM wp;
        switch (button) {
            case MouseButton::Right:    msg = down ? WM_RBUTTONDOWN : WM_RBUTTONUP; wp = down ? MK_RBUTTON : 0; break;
            case MouseButton::Middle:   msg = down ? WM_MBUTTONDOWN : WM_MBUTTONUP; wp = down ? MK_MBUTTON : 0; break;
            case MouseButton::XButton1: msg = down ? WM_XBUTTONDOWN : WM_XBUTTONUP; wp = MAKEWPARAM(down ? MK_XBUTTON1 : 0, XBUTTON1); break;
            case MouseButton::XButton2: msg = down ? WM_XBUTTONDOWN : WM_XBUTTONUP; wp = MAKEWPARAM(down ? MK_XBUTTON2 : 0, XBUTTON2); break;
            default:                    msg = down ? WM_LBUTTONDOWN : WM_LBUTTONUP; wp = down ? MK_LBUTTON : 0; break;
        }
        PostMessageW(m_hwnd, msg, wp, MAKELPARAM(m_curX, m_curY));
    } else {
        INPUT in{};
        in.type = INPUT_MOUSE;
        switch (button) {
            case MouseButton::Right:    in.mi.dwFlags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
            case MouseButton::Middle:   in.mi.dwFlags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            case MouseButton::XButton1: in.mi.dwFlags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; in.mi.mouseData = XBUTTON1; break;
            case MouseButton::XButton2: in.mi.dwFlags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; in.mi.mouseData = XBUTTON2; break;
            default:                    in.mi.dwFlags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
        }
        SendInput(1, &in, sizeof(INPUT));
    }
}

void Win32InputController::EmitKey(bool down, int virtualKey) {
    if (UsePost()) {
        // lParam: repeat count 1; on key-up set prev-state (bit30) and transition (bit31).
        LPARAM lp = down ? 1 : (1 | (1L << 30) | (1L << 31));
        PostMessageW(m_hwnd, down ? WM_KEYDOWN : WM_KEYUP, static_cast<WPARAM>(virtualKey), lp);
    } else {
        INPUT in{};
        in.type = INPUT_KEYBOARD;
        in.ki.wVk = static_cast<WORD>(virtualKey);
        in.ki.dwFlags = down ? 0 : KEYEVENTF_KEYUP;
        SendInput(1, &in, sizeof(INPUT));
    }
}

void Win32InputController::EmitChar(wchar_t ch) {
    if (UsePost()) {
        PostMessageW(m_hwnd, WM_CHAR, static_cast<WPARAM>(ch), 0);
    } else {
        INPUT in{};
        in.type = INPUT_KEYBOARD;
        in.ki.wVk = 0;
        in.ki.wScan = ch;
        in.ki.dwFlags = KEYEVENTF_UNICODE;
        SendInput(1, &in, sizeof(INPUT));
    }
}

// --- high-level operations ---------------------------------------------------

int32_t Win32InputController::MouseMove(int x, int y, TrajectoryType trajectory, bool smooth) {
    try {
        if (!smooth) { EmitMove(x, y); return RUG_OK; }
        int cx, cy; CurrentPos(cx, cy);
        for (const TrajectorySample& s : Humanizer::Plan(cx, cy, x, y, trajectory, m_cfg)) {
            EmitMove(s.x, s.y);
            Humanizer::Wait(s.delayMs);
        }
        return RUG_OK;
    }
    catch (...) { return RUG_ERR_INPUT_FAILED; }
}

int32_t Win32InputController::MouseMoveRelative(int dx, int dy, TrajectoryType trajectory, bool smooth) {
    int cx, cy; CurrentPos(cx, cy);
    return MouseMove(cx + dx, cy + dy, trajectory, smooth);
}

int32_t Win32InputController::MouseDown(MouseButton button) { EmitButton(true, button);  return RUG_OK; }
int32_t Win32InputController::MouseUp(MouseButton button)   { EmitButton(false, button); return RUG_OK; }

int32_t Win32InputController::Click(MouseButton button, int holdTimeMs) {
    try {
        EmitButton(true, button);
        const int hold = holdTimeMs > 0 ? holdTimeMs : Humanizer::RandomInt(m_cfg.minClickHoldMs, m_cfg.maxClickHoldMs);
        Humanizer::Wait(static_cast<float>(hold));
        EmitButton(false, button);
        return RUG_OK;
    }
    catch (...) { return RUG_ERR_INPUT_FAILED; }
}

int32_t Win32InputController::DragAndDrop(int sx, int sy, int ex, int ey, TrajectoryType trajectory, bool smooth) {
    int32_t st = MouseMove(sx, sy, trajectory, smooth);
    if (st != RUG_OK) return st;
    EmitButton(true, MouseButton::Left);
    Humanizer::Wait(static_cast<float>(Humanizer::RandomInt(m_cfg.minClickHoldMs, m_cfg.maxClickHoldMs)));
    st = MouseMove(ex, ey, trajectory, smooth);
    EmitButton(false, MouseButton::Left);
    return st;
}

int32_t Win32InputController::KeyDown(int virtualKey) { EmitKey(true, virtualKey);  return RUG_OK; }
int32_t Win32InputController::KeyUp(int virtualKey)   { EmitKey(false, virtualKey); return RUG_OK; }

int32_t Win32InputController::KeyPress(int virtualKey, int holdTimeMs) {
    EmitKey(true, virtualKey);
    const int hold = holdTimeMs > 0 ? holdTimeMs : Humanizer::RandomInt(m_cfg.minKeyHoldMs, m_cfg.maxKeyHoldMs);
    Humanizer::Wait(static_cast<float>(hold));
    EmitKey(false, virtualKey);
    return RUG_OK;
}

int32_t Win32InputController::SendText(const std::string& text) {
    if (text.empty()) return RUG_OK;
    int n = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0);
    if (n <= 0) return RUG_ERR_INVALID_PARAM;
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), w.data(), n);
    for (wchar_t ch : w) EmitChar(ch);
    return RUG_OK;
}

}  // namespace rug::core::input
