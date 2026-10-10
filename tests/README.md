---
id: rug_native_tests
type: logic_node
inputs: [rug_core]
outputs: []
tags: [tests, native, cabi]
---

# Rug.Core Native Tests

A console harness that exercises **Rug.Core through its public C-ABI only**
(`RugCoreAbi.h`) — the same boundary the C# host will use. It validates OCR
correctness, OCR memory/stability under repetition, and OpenCV template matching.

## Independent input Hook comparison

The standalone [x64 native probe](NativeInputProbe/README.md) compares F8/B with
real Rug.Core input and optionally observes managed diagnostic input in another
process. Run `./tests/run_native_input_probe.ps1 -Mode Test` or `-Mode Human`.
It builds separately, changes no C-ABI, stores no human key sequences and does
not integrate takeover. User runs now include a passing paired session and a
failing opposite order; see [evidence and remaining limits](NativeInputProbe/HookComparison.md).
The [controlled stages](NativeInputProbe/ControlledStages.md) retain native sending
while comparing threads, managed Hooks and [device-class registration/packet processing/delivery](NativeInputProbe/ReceiverRegistrationControl.md).
The runner also builds a [test-only same-process native receiver](NativeInputProbe/NativeReceiverControl.md),
replacing B's keyboard count receiver without changing Rug.Core or the sender.
Its no-managed-Hook control retains B's idle receiver thread and uses A's observers.
The [native callback control](NativeInputProbe/NativeHookCallbackControl.md) retains
B's thread/receiver while selecting a separate callback DLL. The next
[module/send evidence repair](NativeInputProbe/InputEvidenceControl.md) verifies its
owning hmod, native send returns and specific F8/B target down/up before new
order/role controls. Original integrated acceptance remains open.
The next [startup-order control](NativeInputProbe/StartupOrderControl.md) fixes
A-first process order and compares awaited Hook installation against native
receiver startup/registration readiness, without modifying the native binaries.
All three ordered pairs reproduced the observation gap. The next
[separate observer process C](NativeInputProbe/ExternalObserverControl.md) retains
A's Core sender and B's target/native count receiver. Real child-process lifecycle
tests preserve parent registrations; manual three-process acceptance remains pending.
The next [manual B/C Hook order](NativeInputProbe/ManualHookOrderControl.md) holds
process/Raw preparation fixed before the one-shot install commands; real paired
subprocess tests verify clocks, unchanged ownership and clean disposal without AUTO.
The user's nine-log order reversal recovered/missed/recovered all observers.
The next [native B/C hosts](NativeInputProbe/NativeRolesControl.md) preserve A's
sender and existing callback/receiver DLLs. Build with `-Mode Build`, then run
`tests/run_native_role_tests.ps1` for actual no-AUTO A/B/C contract checks.
Original integrated acceptance remains open and automatic takeover stays closed.
That native user run also recovered/missed/recovered without CLR. The next
[Raw owner control](NativeInputProbe/RawOwnerControl.md) fixes Hook order and
compares owner B/C/B while B retains the target/Core. Subprocess tests accept
`-RawOwner B|C`. The user's fixed B-C ownership run was complete in all three
sessions; fixed C-B/owner B-C-B reproduced missing/complete/missing. The next
[target owner control](NativeInputProbe/TargetOwnerControl.md) fixes Core/Raw B,
Hook C-B and moves target B/C/B. Subprocess tests additionally accept
-TargetOwner B|C. The user reproduced missing/complete/missing with target B/C/B.
The next [B Core load control](NativeInputProbe/CoreLoadControl.md) fixes target/
Raw B and Hook C-B, comparing --core-load on/off/on without altering A's sender.
Tests accept -CoreLoad on|off. The user reproduced missing/missing/missing with
Core on/off/on; B's diagnostic load is not necessary. The next
[registration control](NativeInputProbe/RawRegistrationControl.md) fixes B Core
off and compares only keyboard registration on/off/on, preserving the receiver
DLL/thread/window/timer. Tests additionally accept -RawRegistration on|off.
The user reproduced missing/complete/missing with registration on/off/on. The
[DEVNOTIFY control](NativeInputProbe/DeviceNotifyControl.md) kept registration and
INPUTSINK, adding actual registration-call evidence; notification on/off/on
missed in all three sessions. The next
[INPUTSINK control](NativeInputProbe/InputSinkControl.md) keeps registration on
and DEVNOTIFY off, compares sink on/off/on and preserves previous exports/layouts.
Tests accept -InputSink on|off; all 64 subprocess configurations passed. Manual
sink reversal reproduced the gap in all three sessions, including flags zero
with successful registration. The next [native A+B reduction](NativeInputProbe/TwoProcessControl.md)
removes C, retains all five binaries and checks the no-AUTO contract with
`tests/run_native_two_process_tests.ps1`. Manual verification requires two full
A/B logs; the clean user retry reproduced the gap without AUTO interference.
The next [A+B registration control and thread audit](NativeInputProbe/TwoProcessRegistrationControl.md)
retains binaries and receiver infrastructure; the runner accepts
-RawRegistration on|off. First return two OFF logs, restore ON if recovery is
observed. Raw attribution is NOT COLLECTED and takeover stays closed.

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
