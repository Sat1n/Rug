# Minecraft Chinese-menu E2E

This executable test uses the real `Rug.Core.dll` and native dependencies, a
visible Minecraft window, WGC, WinRT OCR, PaddleOCR Medium, OpenCV, Win32
SendInput, Lua, `TaskScheduler`, and `AnomalyLogger`. It has no fake services and
is intentionally run by hand because it clicks the game.

Build the native x64 project first and place the `ppocr_v6_medium` model bundle
under `models/ocr/`. Open the Chinese Minecraft main menu and keep it visible.
Then run:

```powershell
dotnet build Rug.UI.Core.Tests/Rug.UI.Core.Tests.csproj -c Debug -p:Platform=x64 -warnaserror
dotnet run --no-build --project Rug.UI.Core.Tests/Rug.UI.Core.Tests.csproj -c Debug -p:Platform=x64
```

The harness auto-discovers one visible window with `Minecraft` or `我的世界` in its
title. For an ambiguous desktop, add `-- --hwnd 0x...`. `-- --probe` only finds
the window, `-- --preflight` captures and checks the main menu without input,
and `-- --escape` sends Esc to the bound window. The game must be foreground
for `Win32SendInput`; if Windows denies focus activation, focus it manually and
rerun. No input is sent unless a real frame, Chinese labels, and a matching
OpenCV template are confirmed first.

The test runs `E2E/Scripts/minecraft_cn_e2e_loop.lua` as a plugin. WinRT and
Paddle OCR are combined because the 1.12.2 pixel font can make each engine miss
different glyphs. If OCR misses `选项` during Lua execution, the script falls
back to the coordinates recognized from the preflight frame after confirming
the template. It polls WGC after click and Esc to avoid reading a stale frame.

On success it prints `PASS` and paths to a real PNG/JSON anomaly pair under the
ignored `bin/x64/Debug/net9.0/e2e-run/` directory. The anomaly is intentional;
the task must finish `Faulted` after `on_stop`, with its capture session closed.
