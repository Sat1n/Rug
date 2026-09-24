# Lua coroutine bridge tests

Run `dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj`.
The console test uses fake capture, OCR and input services, so no native Rug.Core
DLL or target window is required. It verifies that sleep and capture yield without
blocking, resume carries frame/OCR values into Lua, stop cancels pending sleep,
pause preserves completed work, a bound input target receives click/key arguments,
and manifest permissions reject capture.
