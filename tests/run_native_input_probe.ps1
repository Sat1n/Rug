param([ValidateSet('Human','Observe','Test','SelfTest','Build','ManagedTarget')][string]$Mode = 'Human', [switch]$BuildCore)
$ErrorActionPreference = 'Stop'
$probeRoot = Split-Path -Parent $PSScriptRoot
$probeProject = Join-Path $PSScriptRoot 'NativeInputProbe/NativeInputProbe.vcxproj'
$probeVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$probeMsbuild = $null
if (Test-Path -LiteralPath $probeVswhere) {
    $probeMsbuild = & $probeVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $probeMsbuild) {
    $probeCommand = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($probeCommand) { $probeMsbuild = $probeCommand.Source }
}
if (-not $probeMsbuild) { throw 'Visual Studio C++ v145 tools and Windows SDK are required; MSBuild was not found.' }
if ($BuildCore) {
    & $probeMsbuild (Join-Path $probeRoot 'Rug.Core/Rug.Core.vcxproj') /p:Configuration=Debug /p:Platform=x64 /p:TreatWarningAsError=true /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # This standalone vcxproj emits under Rug.Core/x64/Debug; the probe loads next to its EXE.
    $probeBuiltCore = Join-Path $probeRoot 'Rug.Core/x64/Debug/Rug.Core.dll'
    $probeDeployedCore = Join-Path $probeRoot 'x64/Debug/Rug.Core.dll'
    if (-not (Test-Path -LiteralPath $probeBuiltCore)) { throw "Built native core not found: $probeBuiltCore" }
    New-Item -ItemType Directory -Path (Split-Path -Parent $probeDeployedCore) -Force | Out-Null
    Copy-Item -LiteralPath $probeBuiltCore -Destination $probeDeployedCore -Force
    Write-Host "CORE DEPLOYED: path=$probeDeployedCore sha256=$((Get-FileHash -LiteralPath $probeDeployedCore -Algorithm SHA256).Hash)"
}
& $probeMsbuild $probeProject /p:Configuration=Debug /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$probeReceiverProject = Join-Path $PSScriptRoot 'NativeInputProbe/NativeRawReceiver.vcxproj'
& $probeMsbuild $probeReceiverProject /p:Configuration=Debug /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$probeHookProject = Join-Path $PSScriptRoot 'NativeInputProbe/NativeKeyboardHook.vcxproj'
& $probeMsbuild $probeHookProject /p:Configuration=Debug /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Mode -eq 'Build') { exit 0 }
if ($Mode -in @('Human', 'SelfTest', 'ManagedTarget')) {
    $probeCore = Join-Path $probeRoot 'x64/Debug/Rug.Core.dll'
    if (Test-Path -LiteralPath $probeCore) {
        Write-Host "CORE FILE (pre-run, not loaded-module evidence): path=$probeCore sha256=$((Get-FileHash -LiteralPath $probeCore -Algorithm SHA256).Hash)"
    }
}
$probeExecutable = Join-Path $probeRoot 'x64/Debug/Rug.NativeInputProbe.exe'
[string[]]$probeArgs = @(switch ($Mode) {
    'Observe' { '--observe' }
    'Test' { '--test' }
    'SelfTest' { '--self-test' }
    'ManagedTarget' { '--managed-target' }
    default { @() }
})
& $probeExecutable @probeArgs
exit $LASTEXITCODE

