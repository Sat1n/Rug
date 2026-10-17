---
id: rug_managed_build_baseline
type: logic_node
inputs: [rug_ui_core, rug_ui]
outputs: []
tags: [build, sdk, validation]
---

# Managed x64 build baseline — 2026-10-17

Root `global.json` pins SDK **10.0.402** with `rollForward: disable`.
`dotnet --version` from the repository root must print `10.0.402`.
Historical acceptance records retain their original SDK/evidence. This change
does not upgrade NuGet, Windows App SDK, Native dependencies or target frameworks.

Run the following for **each** project in both Debug and Release, from the root:

```powershell
dotnet restore <project> -p:Platform=x64 -p:Configuration=<configuration> --force
dotnet build <project> -c <configuration> -p:Platform=x64 --no-restore -t:Rebuild -warnaserror
```

Projects: `Rug.UI.Core/Rug.UI.Core.csproj`, `Rug.UI/Rug.UI.csproj`,
`Rug.UI.Core.Tests/Rug.UI.Core.Tests.csproj`, `Rug.UI.Tests/Rug.UI.Tests.csproj`,
`tests/Rug.Core.CSharp.Tests/Rug.Core.CSharp.Tests.csproj`, and
`tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj`.
WinUI stays `net10.0-windows10.0.22621.0` with minimum Windows `10.0.17763.0`.
The remaining projects stay `net10.0`. Native binaries are copied from existing
`x64/<configuration>`; frozen Hook comparison binaries are not rebuilt.

Using rebuilt outputs, run in both configurations:

```powershell
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <configuration> -p:Platform=x64 --no-build -- --checkpoint-tests
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <configuration> -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c <configuration> -p:Platform=x64 --no-build
```

Checkpoint regression includes blocked original-token cancellation before/after
publication, exact decimal hashes and real-file numeric tampering. The format
is now v2; legacy v1 is explicitly unsupported. See [contract](Checkpoints.md).
The full suite retains Lua/recovery/lifecycle/Gateway/grants/arbitration/
takeover/frame-cache/state/hot-reload coverage and separate child-process reads.

## Recorded verification

PASS: repository-root `dotnet --version` = `10.0.402`; exact roll-forward disabled.
PASS: all six projects restored with `--force` and rebuilt with `-t:Rebuild
-warnaserror` in Debug/x64 and Release/x64 (12 builds), including WinUI; every
build reports 0 warnings and 0 errors. No missing SDK/toolset errors occurred.
PASS: checkpoint suite, full script regression and UI core (Schema/state/hot reload/
lifecycle presentation) in BOTH configurations (6 runs), using `--no-build` only
after those rebuilds. Real filesystem and separate child-process reads passed;
the full suite also passed the self-owned native PostMessage/release fixture.
`git diff --check` passed. Ordinary commits invoked the existing Hook; its
OmniAtlas CLI was unavailable, so documentation lint was skipped by the Hook
itself. Hook configuration was not changed.

NOT RUN: real WGC checkpoint scene, Minecraft/OCR models, interactive WinUI
automation or new human-input acceptance. These are not part
of this scoped SDK/precision/cancellation revalidation. No Hook-gap resolution,
new-instance checkpoint startup or absolute power-loss safety is claimed.
