@echo off
setlocal
cd /d "%~dp0.."

echo [1/2] Building Rug.Tests (Debug^|x64) via solution...
msbuild Rug.slnx /t:Rug_Tests /p:Configuration=Debug /p:Platform=x64 /m /nologo /v:minimal
if errorlevel 1 (
  echo.
  echo BUILD FAILED.
  echo Fallback: open Rug.slnx in Visual Studio, select Debug/x64,
  echo   right-click Rug.Tests -^> Build, then run: x64\Debug\Rug.Tests.exe
  exit /b 1
)

echo [2/2] Running tests...
echo.
"x64\Debug\Rug.Tests.exe" %*
endlocal
