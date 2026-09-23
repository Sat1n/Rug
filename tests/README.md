# Rug.Core Native Tests

A console harness that exercises **Rug.Core through its public C-ABI only**
(`RugCoreAbi.h`) — the same boundary the C# host will use. It validates OCR
correctness, OCR memory/stability under repetition, and OpenCV template matching.

## Layout

```
tests/
├── Rug.Tests.vcxproj        # console exe (Debug/Release × Win32/x64)
├── TestCommon.h / .cpp      # PNG→RugFrame (WinRT), repo/image/model path resolution
├── test_ocr_correctness.cpp # Task 2: dual-engine structured comparison over ocr_*.png
├── test_ocr_stress.cpp      # Task 3: 20× recognize/free per engine + timing + leak check
├── test_template_match.cpp  # Task 4: Rug_MatchTemplate (skips if images absent)
├── test_model_discovery.cpp # model auto-discovery + Rug_CreateOcrEngineById
├── main.cpp                 # dispatch: all | ocr | stress | match | models
├── run_tests.cmd            # one-key build + run
└── test_images/             # put ocr_*.png, tmpl_search.png, tmpl_target.png here
```

## Build

The test project has a **ProjectReference to Rug.Core**, so building it builds the
DLL first and auto-links `Rug.Core.lib`. No OpenCV/ONNX wiring is needed in the
test itself — it only includes `RugCoreAbi.h`.

Both projects use the default `OutDir = $(SolutionDir)$(Platform)\$(Configuration)\`,
so `Rug.Tests.exe` lands in `x64\Debug\` **next to** `Rug.Core.dll` and the DLLs
vcpkg/ONNX deployed there (opencv_world, yaml-cpp, onnxruntime). That is the
"auto-associated DLL" path — no manual copying.

- **In Visual Studio:** open `Rug.slnx`, select **Debug / x64**, right-click
  **Rug.Tests → Build**.
- **One-key CLI:** run `tests\run_tests.cmd` (builds `Rug_Tests` via MSBuild, then
  runs the exe). Pass a mode argument through, e.g. `run_tests.cmd stress`.

## Run

```
x64\Debug\Rug.Tests.exe                    # runs all tests
x64\Debug\Rug.Tests.exe ocr                # correctness only
x64\Debug\Rug.Tests.exe stress             # stress only
x64\Debug\Rug.Tests.exe match              # template match only
x64\Debug\Rug.Tests.exe models             # model discovery + create-by-id
x64\Debug\Rug.Tests.exe ocr 2              # correctness with engine #2 (skip menu)
```

**Engine selection:** `ocr` and `stress` first scan `models/ocr` and print a
numbered menu — `[0] WinRT OCR (system)` plus one entry per discovered Paddle
model — then ask which to use. Pass a second argument (the index) to skip the
prompt; with no console input (piped/CI) it defaults to `[0]` WinRT. In `all`
mode the menu is shown once and reused for both tests.

Paths are resolved from the executable location (it walks up to the directory
containing `Rug.slnx`), so the working directory does not matter.

## Prerequisites

- **Images:** see [test_images/README.md](test_images/README.md). Without `ocr_*.png`
  the OCR tests error out; without `tmpl_*.png` the match test prints `[SKIP]`.
- **PP-OCRv6:** requires `Rug.Core` built with `RUG_HAS_ONNX` (i.e. `ONNXRUNTIME_ROOT`
  set at build time) and the model directory `models/ocr/ppocr_v6` populated
  (`det.onnx`, `rec.onnx`, `det.yml`, `rec.yml`). Otherwise the PP-OCR engine
  reports unavailable and the harness still runs WinRT OCR.
- **Template match:** requires `Rug.Core` built with `RUG_HAS_OPENCV` (vcpkg OpenCV);
  otherwise it returns `RUG_ERR_UNSUPPORTED` and the test prints `[SKIP]`.

## What "pass" looks like

- **Correctness:** aligned per-image blocks for the selected engine with
  per-line `Score`, `Box [x,y,w,h]`, and `Text`.
- **Stress:** avg/min/max latency over 20 iterations for the selected engine,
  clean `Rug_DestroyOcrEngine`, no crash (an access violation aborts the run), and
  the CRT leak dump at exit is empty (Debug).
- **Match:** best match `(x, y, w, h)` + `Score` + timing, or `[SKIP]`.
- **Discovery:** lists every bundle under `models/ocr` (id/engine/version),
  creates the first by id, and confirms an unknown id reports model-not-found.
