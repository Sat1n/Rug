---
id: rug_capture_facts
type: logic_node
inputs: [rug_core]
outputs: [rug_ui_core]
tags: [capture, cabi, timing, geometry]
---

# WGC frame timing and geometry (3.5-08A-02A)

## ABI and ownership

`RugFrame`, `RugCaptureConfig` and all old exports retain their layouts/signatures.
`Rug_CreateCapturer` retains same-process largest-window selection.
`Rug_CreateCapturerExact` accepts the same config but captures **only** the given
HWND. Managed production hosts can opt into `new CaptureService(exactBoundWindow: true)`;
the parameterless path retains its selection and pixel/recognition semantics.

`Rug_GrabFrameWithFacts(handle, outFrame, facts)` returns pixels and facts from
one final selected WGC frame. Caller initializes `structSize=136`, `version=1`.
V1 uses pack 8 and fixed-width integer fields; no owned pointers. Exact size and
version are required; invalid input returns `RUG_ERR_INVALID_PARAM`, with a valid
outFrame zeroed. On other failure, ignore facts. On success, free outFrame.data
**exactly once** with `Rug_FreeBuffer`. No separate facts release is needed.
Old/new grabs work on either capturer type. Native callers serialize destroy
against grab as before; managed callers retain SafeHandle until actual native work ends.

| Fields | Units / meaning |
| --- | --- |
| structSize, version | bytes and V1 |
| flags | 0x1 valid native time; 0x2 sampled exact-window DWM mapping; 0x4 captured HWND equals bound HWND |
| geometryStatus | 0 unavailable; 1 verified sampled path; 2 window samples/lifetime changed; 3 another capture window; 4 unsupported geometry; 5 unknown time or frame predates geometry sample |
| systemRelative100ns | WGC SystemRelativeTime, **compositor render QPC time** in 100 ns units; not user action time, UTC or host receipt |
| qpcFrequency, qpcSample | native QPC ticks/second and native counter sample after surface copy/geometry sampling, before final buffer delivery |
| geometryBeforeQpc, geometryAfterQpc | native QPC brackets around the selected-frame acquisition; their frequency remains available even when frame time is unknown |
| boundWindow, capturedWindow | raw HWND diagnostic facts (uint64); stripped from public host/Lua observation metadata |
| boundLifetime, capturedLifetime | opaque window properties, zero means unavailable; destruction removes property, reused HWND does not inherit it |
| contentWidth/Height | final WGC frame ContentSize at render time |
| bufferWidth/Height | copied SoftwareBitmap pixel buffer dimensions; not inferred client dimensions |
| clientWidth/Height, clientScreenX/Y | post-sample physical client extent and screen origin; valid for mapping only with bit 2 |
| imageClientX/Y | client origin relative to full image, not a crop already applied to pixels |
| dpi, reserved | post-sample window DPI (zero unknown), reserved zero |

The final candidate holds pixels, ContentSize and SystemRelativeTime together.
Draining discards whole frame objects; black/empty retries replace the entire
candidate, including unknown metadata. Frame-pool size changes close that frame,
recreate the pool, then reacquire rather than publishing a possibly clipped surface.
BGRA plane start/stride are read from BitmapPlaneDescription; rows are validated,
padding is zeroed, and exported bytes are capped at 64 MiB before allocation.

## Time domains

[Microsoft SystemRelativeTime](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe.systemrelativetime)
defines compositor render QPC time. [ContentSize](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe.contentsize)
describes content when rendered, not a later window query.

Native QPC counter ticks convert by quotient/remainder to 100 ns; unknown,
nonpositive, future, overflow or unexplained frequency (>1 GHz) values never get
filled with the current time. Time availability has its own flag. Managed
NativeFrameTime computes age **at the native counter sample** using integer
rational conversion (Int128), and refuses another frequency/domain. This is not
age at host publication, nor an action freshness guarantee. Arbitrary TimeProvider
timestamps cannot be subtracted from these native times. Host request/receipt/
publication clocks remain separately tagged. NativeAcquiredUtc and the historical
untagged NativeAcquiredTimestamp remain null; native time lives in NativeTime.

## Supported mapping and rejection

Production mapping supports a visible, non-minimized, non-layered, non-mirrored,
standard overlapped top-level window when captured HWND is exactly the bound HWND.
PMv2 sampling obtains DWM extended frame bounds, ClientToScreen origin, client
extent, DPI, style, monitor, PID/TID and window-lifetime property. All before/after
properties must agree; valid final frame render time must lie after the first
sample, and ContentSize/buffer dimensions must agree with the DWM extent. Client
extent must lie wholly inside the image. These are **additional** checks, never
an inference from equal image/client dimensions or common PID.

The exact-window path retries boundedly for a post-sample frame; if it still has
usable pixels but cannot establish this mapping, pixels return with unavailable
geometry; if no usable final candidate remains, capture fails instead. Another
selected HWND, unsupported styles, stale/unknown time, DPI,
resize, movement, monitor or lifetime changes yield no transform. A destroyed or
reused bound/captured HWND rejects capture. Inability to install/read the lifetime
property permits capture facts but never verified geometry. Properties live with
the window so concurrent capturers do not invalidate one another on Stop.

Mapping is `client = image - imageClientOffset`, then
`screen = client + clientScreenOrigin`. Pixels retain the full window image;
no implicit crop or coordinate remapping is applied to old OCR/template outputs.
The valid image region is only the client rectangle. Recognition ROI→image offset
is applied once; a consumer may explicitly chain the new image→client mapping.
These are capture-time samples, not a guarantee that geometry remained unchanged
between samples or still holds when an action is executed. Native properties are
diagnostic lifetime guards, not an authenticated tamper-proof OS identity.

Pure `PixelTransform` expresses positive scale/crop/letterbox relations with named
spaces and half-open valid regions. Padding/borders/NaN/unknown transforms reject;
nearest pixel rounds AwayFromZero and checks bounds again, without clamp. No
preprocessing pipeline or action gate is introduced.

## Verification

See [test entry points](../tests/Rug.UI.Core.Script.Tests/README.md) and
[host observation ownership](../Rug.UI.Core/Observations.md). C-ABI `capture` tests
also exercise the small internal pure candidate/geometry/QPC helper; other native
pipeline tests continue through the C-ABI. The archived input/Hook experiments
are unchanged and were not rebuilt by this unit.

## Recorded verification (2026-10-20 / 21)

SDK 10.0.402; existing v145/Windows SDK and ONNX Runtime 1.30.0 dependencies.
Debug/x64 and Release/x64 native Core + Rug.Tests and all six managed projects
(including WinUI) build with zero warnings/errors. `Rug.Tests.exe all 0` passes in
both configurations (capture contracts, WinRT OCR correctness/stress, matching,
model discovery/creation). Full script regressions and UI-core Schema/state/hot
reload pass in both configurations: observation, Gateway, authorization,
arbitration/intervention, lifecycle, recovery/checkpoint and 07A/07B updates.

Agent-owned actual WGC tests (not synthetic visual observations) pass normal
bordered-window mapping, resize, alternate-window refusal, lifetime destruction
and old/new ABI coexistence. At DPI 144, image marker (82,115) maps to client
(80,70); frame 622×411 / client 618×364 becomes 782×521 / client 778×474 after
resize. An available second monitor permits a **real** DPI 144→96 move: image
marker (81,101) still maps to client (80,70), frame 519×346 / client 517×314.
These numbers describe this fixture, not a universal border-size formula.
WGC→WinRT OCR returns three blocks and OpenCV matching retains exact source-frame
ID/native time in the separate smoke.

Commands (repository root, matching native builds/dependency deployment first):

```powershell
x64/Debug/Rug.Tests.exe all 0
x64/Release/Rug.Tests.exe all 0
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Release -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Release -p:Platform=x64 --no-build
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --capture-geometry-native
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Release -p:Platform=x64 --no-build -- --capture-geometry-native
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --observation-native
```

C++ builds use `MSBuild tests/Rug.Tests.vcxproj /p:Platform=x64
/p:Configuration=Debug` (and Release), explicit existing ONNXRUNTIME_ROOT and
repository `x64/<Configuration>/` OutDir. Deploy existing matching vcpkg runtime
DLLs beside Core; the initial smoke failed with 0x8007007E until those dependencies
were deployed, then actual smokes passed. No package/SDK upgrades were made.

Not claimed: generalized custom/chromeless/layered/minimized window mapping,
continuous geometry invariance, action-time safety, physical clicks or new human
input acceptance. Separate Minecraft E2E was NOT RUN. No UI presentation changed,
so no new manual UI acceptance is required for this unit. Existing independent
Hook gap and earlier acceptance limits remain unchanged.
