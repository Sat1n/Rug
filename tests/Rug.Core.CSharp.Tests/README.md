# Rug.Core.CSharp.Tests

Managed test harness for the C# facade over `Rug.Core.dll` (Subtask 1.4.4). It
exercises `Rug.UI.Core`'s `OcrService` / `TemplateMatchService`, which call the
native C-ABI through the single P/Invoke boundary in `Rug.UI.Core/Native/`.

## What it checks

- `OcrService.RecognizeAsync` on `tests/test_images/ocr_test_1.*` with **WinRT** and
  with **every discovered PP-OCR model** (`models/ocr/*`).
- UTF-8 correctness (no U+FFFD mojibake) and plausible bounding boxes.
- 10 consecutive recognitions + explicit `GC.Collect`/`WaitForPendingFinalizers` to
  prove the `SafeHandle`s release native resources without a crash.
- Optional `TemplateMatchService` check when `tmpl_search`/`tmpl_target` exist.
- `--visual-only` generates a patterned BMP and managed BGRA frame, checks the
  native in-memory match coordinate, then runs 1,000 matches while measuring
  private memory and handle growth; it needs no external OCR sample images.

Exit code is `0` on success, `1` if any check failed, `2` if no test image is found.

## Prerequisites

1. **Build the native `Rug.Core` (x64) first** — including the `Rug_LoadImageFile`
   export. The csproj copies `..\..\x64\$(Configuration)\*.dll` (Rug.Core.dll +
   opencv_world / yaml-cpp / onnxruntime) next to the test exe after build.
2. Put an `ocr_test_1.png`/`.jpg` in `tests/test_images/` (see that folder's README).
3. For PP-OCR: `ONNXRUNTIME_ROOT` set at native build time and `models/ocr/<bundle>`
   populated (`det.onnx`/`rec.onnx`/`det.yml`/`rec.yml` + `model.json`).

## Run

```bat
dotnet build ..\..\Rug.slnx -c Debug            :: or build Rug.Core in VS first
dotnet run --project Rug.Core.CSharp.Tests.csproj -c Debug
dotnet run --project Rug.Core.CSharp.Tests.csproj -c Debug -- --visual-only
```

The process is x64 (`PlatformTarget=x64`) to match the native DLL. Paths are
resolved by walking up to the directory containing `Rug.slnx`, so the working
directory does not matter.
