param([ValidateSet('Debug','Release')][string]$Configuration='Debug',[string]$OutputRoot)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(-not $OutputRoot){$OutputRoot=Join-Path $repo "artifacts/controlled-recovery-$Configuration"}
Push-Location $repo
try {
 if(-not (Test-Path "x64/$Configuration/Rug.Core.dll")){throw 'Build the matching x64 native core first'}
 dotnet build Rug.UI/Rug.UI.csproj -c $Configuration -p:Platform=x64 --no-restore
 if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
 dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c $Configuration -p:Platform=x64 -- --controlled-recovery-sample $OutputRoot
 exit $LASTEXITCODE # Preserve aggregate failure, including known CLR shutdown assertion.
} finally {Pop-Location}
