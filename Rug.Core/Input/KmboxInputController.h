// =============================================================================
//  KmboxInputController.h — KMBox (B+/Pro/Net) hardware back-end.
//  STATUS: skeleton. The vendor serial/network command protocol, baud handshake
//  and heartbeat are NOT implemented (no vendor spec / hardware available), so
//  every operation returns RUG_ERR_UNSUPPORTED. The structure (connection info,
//  handshake/heartbeat hooks, trajectory planning) is in place to be filled in.
// =============================================================================

#ifndef KMBOX_INPUT_CONTROLLER_H
#define KMBOX_INPUT_CONTROLLER_H
#pragma once

#include "IInputController.h"
#include <string>

namespace rug::core::input {

class KmboxInputController : public IInputController {
public:
    struct Connection {
        std::string serialPort;      // e.g. "COM3"
        int         baudRate = 115200;
        std::string netHost;         // KMBox Net
        int         netPort = 0;
    };

    explicit KmboxInputController(HWND hwnd, Connection conn = {});

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
    // TODO(KMBox): open the serial/socket, perform the baud handshake, and start
    // a heartbeat thread. Returns false until the protocol is implemented.
    bool EnsureConnected();

    HWND           m_hwnd;
    HumanizeConfig m_cfg;
    Connection     m_conn;
    bool           m_connected = false;
};

}  // namespace rug::core::input

#endif  // KMBOX_INPUT_CONTROLLER_H
