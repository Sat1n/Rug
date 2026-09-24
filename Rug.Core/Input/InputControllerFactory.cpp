// =============================================================================
//  InputControllerFactory.cpp — CreateInputController(mode, hwnd).
// =============================================================================

#include "pch.h"
#include "IInputController.h"
#include "Win32InputController.h"
#include "KmboxInputController.h"
#include "RugCoreAbi.h"  // RugStatus codes

namespace rug::core::input {

int32_t CreateInputController(InputMode mode, HWND hwnd, std::unique_ptr<IInputController>& out) {
    try {
        switch (mode) {
            case InputMode::Win32Software:
                out = std::make_unique<Win32InputController>(hwnd);
                return RUG_OK;
            case InputMode::HardwareKMBox:
                // Skeleton: constructs, but operations report RUG_ERR_UNSUPPORTED
                // until the KMBox protocol is implemented.
                out = std::make_unique<KmboxInputController>(hwnd);
                return RUG_OK;
            default:
                return RUG_ERR_INVALID_PARAM;
        }
    }
    catch (...) {
        return RUG_ERR_INPUT_FAILED;
    }
}

}  // namespace rug::core::input
