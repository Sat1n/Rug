// =============================================================================
//  KmboxInputController.cpp — skeleton. All operations return RUG_ERR_UNSUPPORTED
//  until the KMBox serial/network protocol (baud handshake, heartbeat, mouse/key
//  command framing) is implemented against a vendor spec / device.
// =============================================================================

#include "pch.h"
#include "KmboxInputController.h"
#include "Humanizer.h"
#include "RugCoreAbi.h"  // RugStatus codes

namespace rug::core::input {

KmboxInputController::KmboxInputController(HWND hwnd, Connection conn)
    : m_hwnd(hwnd), m_conn(std::move(conn)) {}

bool KmboxInputController::EnsureConnected() {
    // TODO(KMBox): open serialPort/netHost, negotiate baud, verify handshake,
    // spawn a heartbeat. Not implemented -> treat as disconnected.
    return false;
}

void KmboxInputController::SetTargetWindow(HWND hwnd) { m_hwnd = hwnd; }
void KmboxInputController::SetHumanizeConfig(const HumanizeConfig& config) { m_cfg = config; }

std::vector<TrajectorySample> KmboxInputController::PlanTrajectory(
    int sx, int sy, int ex, int ey, TrajectoryType trajectory) const {
    // Planning is pure math and mode-independent, so it works even unconnected.
    return Humanizer::Plan(sx, sy, ex, ey, trajectory, m_cfg);
}

int32_t KmboxInputController::MouseMove(int, int, TrajectoryType, bool)          { return EnsureConnected() ? RUG_OK : RUG_ERR_UNSUPPORTED; }
int32_t KmboxInputController::MouseMoveRelative(int, int, TrajectoryType, bool)  { return EnsureConnected() ? RUG_OK : RUG_ERR_UNSUPPORTED; }
int32_t KmboxInputController::MouseDown(MouseButton)                            { return EnsureConnected() ? RUG_OK : RUG_ERR_UNSUPPORTED; }
int32_t KmboxInputController::MouseUp(MouseButton)                              { return EnsureConnected() ? RUG_OK : RUG_ERR_UNSUPPORTED; }
int32_t KmboxInputController::Click(MouseButton, int)                           { return EnsureConnected() ? RUG_OK : RUG_ERR_UNSUPPORTED; }
int32_t KmboxInputController::DragAndDrop(int, int, int, int, TrajectoryType, bool) { return EnsureConnected() ? RUG_OK : RUG_ERR_UNSUPPORTED; }
int32_t KmboxInputController::KeyDown(int)                                      { return EnsureConnected() ? RUG_OK : RUG_ERR_UNSUPPORTED; }
int32_t KmboxInputController::KeyUp(int)                                        { return EnsureConnected() ? RUG_OK : RUG_ERR_UNSUPPORTED; }
int32_t KmboxInputController::KeyPress(int, int)                                { return EnsureConnected() ? RUG_OK : RUG_ERR_UNSUPPORTED; }
int32_t KmboxInputController::SendText(const std::string&)                      { return EnsureConnected() ? RUG_OK : RUG_ERR_UNSUPPORTED; }

}  // namespace rug::core::input
