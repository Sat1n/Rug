param([ValidateSet('Debug','Release')][string]$Configuration='Debug',[string]$OutputRoot)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(-not $OutputRoot){$OutputRoot=Join-Path $repo "artifacts/controlled-visual-$Configuration"}
if(-not (Test-Path -LiteralPath (Join-Path $repo "x64/$Configuration/Rug.Core.dll"))){throw "Missing matching Native build: x64/$Configuration/Rug.Core.dll"}
Push-Location $repo
try {
 dotnet build Rug.UI/Rug.UI.csproj -c $Configuration -p:Platform=x64 --no-restore
 if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
 dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c $Configuration -p:Platform=x64 -- --controlled-visual-sample $OutputRoot
 # Preserve functional failures AND the known WinUI CLR shutdown failure.
 exit $LASTEXITCODE
}
finally {Pop-Location}
