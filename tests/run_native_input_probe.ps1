param([ValidateSet('Human','Observe','Test','SelfTest','Build')][string]$Mode = 'Human')
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
& $probeMsbuild $probeProject /p:Configuration=Debug /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Mode -eq 'Build') { exit 0 }
$probeExecutable = Join-Path $probeRoot 'x64/Debug/Rug.NativeInputProbe.exe'
[string[]]$probeArgs = @(switch ($Mode) {
    'Observe' { '--observe' }
    'Test' { '--test' }
    'SelfTest' { '--self-test' }
    default { @() }
})
& $probeExecutable @probeArgs
exit $LASTEXITCODE

