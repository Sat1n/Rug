# Lua coroutine bridge tests

Run `dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj`.
The console test uses fake capture, OCR and input services, so no native Rug.Core
DLL or target window is required. It verifies that sleep and capture yield without
blocking, resume carries frame/OCR values into Lua, stop cancels pending sleep,
pause preserves completed work, a bound input target receives click/key arguments,
and manifest permissions reject capture.

The same executable creates temporary plugin directories to check valid
`manifest.json`/`config.json` parsing, all four UI control types and typed defaults,
malformed JSON tolerance, entry path traversal rejection, sandbox global removal,
and audited `PermissionDeniedException` behavior.

Scheduler integration tests use real NLua with fake capture/input services to check
capture startup before `on_init`, periodic `on_tick`, `on_stop` cleanup, independent
pause/stop across three instances, and watchdog interruption of tight loops in
both `on_init` and `on_tick`. The script also attempts to replace the cancellation
callback, confirming the watchdog uses its private reference. A pending capture
startup is canceled and cleaned up when the scheduler is disposed.

Anomaly tests check `agent` permission denial, a Lua-triggered yield and task
fault, the per-instance warning and cleanup, UTF-8 reason/context plus Lua
traceback in JSON, a null `agent_resolution` placeholder, and the PNG's actual
RGBA pixel data decoded from its compressed image payload.

Task 2.2 tests use a real Win32 test HWND to compare client-to-screen mapping
against `MapWindowPoints`, and fake input services to verify permission denial,
client-coordinate forwarding, per-call SendInput/PostMessage mode selection,
manifest defaults, button selection, asynchronous key duration, cancellation
KeyUp, and rejection when foreground activation fails.

Task 2.3 tests use generated BGRA frames and fake OCR/template engines to verify
Lua hit coordinates, confidence, region offsets, OCR text/language, permission
denial, missing/invalid/escaping template paths, and coroutine polling for both
image appearance and timeout. A 1,000-cycle capture/match test confirms only the
current frame remains cached and older frames can be collected. The native
`TemplateMatchService.MatchFrameAsync` path pins the managed frame only for its
Rug.Core call. The fake-based test does not measure native heap usage; run the
`--visual-only` mode in [Rug.Core.CSharp.Tests](../Rug.Core.CSharp.Tests/README.md)
for a synthetic-BMP OpenCV match and 1,000 native calls with memory/handle checks.
