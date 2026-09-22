// =============================================================================
//  main.cpp — Rug.Core native test harness entry point.
//  Usage: Rug.Tests.exe [all|ocr|stress|match]   (default: all)
// =============================================================================

#include "TestCommon.h"

#include <windows.h>
#include <winrt/base.h>

#include <cstdio>
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
    const bool all = (mode == L"all");

    std::printf("Rug.Core native test harness\n");
    std::printf("repo root : %s\n", ToUtf8(RepoRoot()).c_str());
    std::printf("images dir: %s\n", ToUtf8(TestImagesDir()).c_str());
    std::printf("mode      : %s\n\n", ToUtf8(mode).c_str());

    int rc = 0;
    if (all || mode == L"ocr") {
        std::printf("### OCR CORRECTNESS ###\n");
        rc |= RunOcrCorrectness();
        std::printf("\n");
    }
    if (all || mode == L"stress") {
        std::printf("### OCR STRESS ###\n");
        rc |= RunOcrStress();
        std::printf("\n");
    }
    if (all || mode == L"match") {
        std::printf("### TEMPLATE MATCH ###\n");
        rc |= RunTemplateMatch();
        std::printf("\n");
    }

    std::printf("done (rc=%d)\n", rc);
    return rc;
}
