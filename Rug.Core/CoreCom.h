// =============================================================================
//  CoreCom.h — shared COM apartment helper for Rug.Core native modules.
//  WinRT async calls require an initialized apartment on the calling thread.
//  The host should invoke capture/OCR from a background MTA thread; this also
//  tolerates an apartment the host already established (e.g. STA UI thread).
// =============================================================================

#ifndef CORE_COM_H
#define CORE_COM_H
#pragma once

#include <windows.h>
#include <objbase.h>   // CoInitializeEx / COINIT_MULTITHREADED (excluded by WIN32_LEAN_AND_MEAN)

namespace rug::core {

inline void EnsureApartment() {
    static thread_local bool s_done = [] {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        (void)hr;  // S_FALSE (already init) and RPC_E_CHANGED_MODE are both usable
        return true;
    }();
    (void)s_done;
}

}  // namespace rug::core

#endif  // CORE_COM_H
