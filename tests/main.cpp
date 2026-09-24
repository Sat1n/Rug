// =============================================================================
//  main.cpp — Rug.Core native test harness entry point.
//  Usage: Rug.Tests.exe [all|ocr|stress|match|models] [engineIndex]
//    engineIndex: preselect the OCR engine (skips the interactive menu).
//                 0 = WinRT, 1.. = discovered Paddle models. Default: prompt.
// =============================================================================

#include "TestCommon.h"

#include <windows.h>
#include <winrt/base.h>

#include <cstdio>
#include <cstdlib>
#include <string>

#ifdef _DEBUG
#include <crtdbg.h>
#endif

using namespace rugtest;

int wmain(int argc, wchar_t** argv) {
#ifdef _DEBUG
    // Dump any CRT-detected leaks at process exit (stress-test validation).
    _CrtSetDbgFlag(_CRTDBG_ALLOC_MEM_DF | _CRTDBG_LEAK_CHECK_DF);
#endif

    SetupConsole();
    winrt::init_apartment();  // returns void in this cppwinrt version; COM stays initialized

    const std::wstring mode = (argc > 1) ? argv[1] : L"all";
    const int preset = (argc > 2) ? _wtoi(argv[2]) : -1;
    const bool all = (mode == L"all");
    const bool needEngine = all || mode == L"ocr" || mode == L"stress";

    std::printf("Rug.Core native test harness\n");
    std::printf("repo root : %s\n", ToUtf8(RepoRoot()).c_str());
    std::printf("images dir: %s\n", ToUtf8(TestImagesDir()).c_str());
    std::printf("mode      : %s\n\n", ToUtf8(mode).c_str());

    // Resolve the OCR engine selection once (shared by correctness + stress).
    int engineIndex = preset;
    if (needEngine && engineIndex < 0)
        engineIndex = ChooseEngine(BuildEngineChoices(), -1);
    if (needEngine) std::printf("\n");

    int rc = 0;
    if (all || mode == L"ocr") {
        std::printf("### OCR CORRECTNESS ###\n");
        rc |= RunOcrCorrectness(engineIndex);
        std::printf("\n");
    }
    if (all || mode == L"stress") {
        std::printf("### OCR STRESS ###\n");
        rc |= RunOcrStress(engineIndex);
        std::printf("\n");
    }
    if (all || mode == L"match") {
        std::printf("### TEMPLATE MATCH ###\n");
        rc |= RunTemplateMatch();
        std::printf("\n");
    }
    if (all || mode == L"models") {
        std::printf("### MODEL DISCOVERY ###\n");
        rc |= RunModelDiscovery();
        std::printf("\n");
    }

    std::printf("done (rc=%d)\n", rc);
    return rc;
}
