# Diagnostic subprocess contract tests only: no SendInput and no human acceptance.
param([ValidateSet('B','C')][string]$RawOwner = 'B')
$ErrorActionPreference = 'Stop'
$roleRoot = Split-Path -Parent $PSScriptRoot
$roleExe = Join-Path $roleRoot 'x64/Debug/Rug.NativeRoleProbe.exe'
$senderExe = Join-Path $roleRoot 'x64/Debug/Rug.NativeInputProbe.exe'
foreach ($path in @($roleExe, $senderExe)) { if (-not (Test-Path -LiteralPath $path)) { throw "Build first: tests/run_native_input_probe.ps1 -Mode Build; missing $path" } }
function Assert-Role($condition, [string]$message) { if (-not $condition) { throw $message } }
function Start-Role([string]$argument, [string]$executable = $roleExe) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.Arguments = $argument
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    return [pscustomobject]@{ Process = $process; Lines = [Collections.Generic.List[string]]::new(); Errors = $process.StandardError.ReadToEndAsync() }
}
function Read-Role($child) {
    $pending = $child.Process.StandardOutput.ReadLineAsync()
    Assert-Role ($pending.Wait(15000)) 'Child output timed out'
    $line = $pending.GetAwaiter().GetResult()
    if ($null -ne $line) { Assert-Role ($child.Lines.Count -lt 400) 'Unbounded child output'; $child.Lines.Add($line) }
    return $line
}
function Until-Role($child, [string]$prefix) {
    while ($true) { $line = Read-Role $child; Assert-Role ($null -ne $line) "Child exited before $prefix"; if ($line.StartsWith($prefix)) { return $line } }
}
function Write-Role($child, [string]$command) { $child.Process.StandardInput.WriteLine($command); $child.Process.StandardInput.Flush() }
function End-Role($child, [switch]$Eof) {
    if ($Eof) { $child.Process.StandardInput.Close() } else { Write-Role $child 'done' }
    while ($null -ne (Read-Role $child)) { }
    Assert-Role ($child.Process.WaitForExit(15000)) 'Child exit timed out'
    Assert-Role ([string]::IsNullOrEmpty($child.Errors.GetAwaiter().GetResult())) 'Unexpected child stderr'
}
function Dispose-Role($child) {
    if ($null -eq $child) { return }
    if (-not $child.Process.HasExited) {
        $child.Process.StandardInput.Close()
        if (-not $child.Process.WaitForExit(5000)) { $child.Process.Kill(); $child.Process.WaitForExit() }
    }
    $child.Process.Dispose()
}
function Field-Role([string]$line, [string]$field) {
    $match = [regex]::Match($line, ('\b' + [regex]::Escape($field) + '=(\d+)'))
    Assert-Role $match.Success "Missing $field in $line"
    return [long]::Parse($match.Groups[1].Value)
}
function RawIdentity-Role($child, [string]$phase) {
    $line = @($child.Lines | Where-Object { $_.StartsWith("$phase RAW:") })[-1]
    $match = [regex]::Match($line, 'threadId=(\d+) hwnd=(0x[0-9a-fA-F]+)')
    Assert-Role $match.Success 'Missing Raw identity'; return $match.Value
}
function Install-Role($child, [string]$role) { Write-Role $child 'install'; return Until-Role $child "NATIVE $role INSTALLED:" }
function Cleanup-Role($child, [string]$role) {
    Assert-Role ($child.Lines -match "NATIVE $role CLEANUP: threadExited=1 attached=0 targetHwnd=0 rawRegistrations=0 fault=None") "Incomplete $role cleanup"
    if ($role -eq $RawOwner) {
        $raw = @($child.Lines | Where-Object { $_.StartsWith('STOPPED RAW:') })[-1]
        Assert-Role ((Field-Role $raw 'input') -eq (Field-Role $raw 'cleanup')) 'WM_INPUT cleanup mismatch'
    }
}
foreach ($order in @('B-C','C-B','B-C')) {
    $a = $null; $b = $null; $c = $null
    try {
        $a = Start-Role '--managed-target' $senderExe
        $null = Until-Role $a 'MANAGED TARGET:'
        $b = Start-Role "--role-b --raw-owner $RawOwner"
        $bPrepared = Until-Role $b 'NATIVE B PREPARED:'
        $null = Until-Role $b 'NATIVE B COMMANDS:'
        $c = Start-Role "--role-c --raw-owner $RawOwner"
        $cPrepared = Until-Role $c 'NATIVE C PREPARED:'
        $null = Until-Role $c 'NATIVE C COMMANDS:'
        $rawChild = if ($RawOwner -eq 'B') { $b } else { $c }
        $rawIdentity = RawIdentity-Role $rawChild 'PREPARED'
        $bRegistrations = if ($RawOwner -eq 'B') { 1 } else { 0 }
        $cRegistrations = 1 - $bRegistrations
        Assert-Role ($bPrepared -match "clrLoaded=0 target=1 rawRegistrations=$bRegistrations rawOwner=$RawOwner" -and $cPrepared -match "clrLoaded=0 target=0 rawRegistrations=$cRegistrations rawOwner=$RawOwner") 'Role isolation/registration incorrect'
        $rawPrepared = if ($RawOwner -eq 'B') { $bPrepared } else { $cPrepared }
        Assert-Role ((Field-Role $rawPrepared 'rawReadyObservedTicks') -gt 0) 'Raw ownership did not await readiness'
        Assert-Role (@($rawChild.Lines | Where-Object { $_ -match '^ROLE MODULE:.*Rug.NativeRawReceiver.dll' }).Count -eq 1) 'Raw owner module evidence missing'
        $nonRawChild = if ($RawOwner -eq 'B') { $c } else { $b }
        Assert-Role (-not ($nonRawChild.Lines -match '^ROLE MODULE:.*Rug.NativeRawReceiver.dll')) 'Nonowner loaded Raw module'
        Assert-Role (@($b.Lines | Where-Object { $_ -match '^ROLE MODULE:.*Rug.Core.dll' }).Count -eq 1 -and -not ($c.Lines -match '^ROLE MODULE:.*Rug.Core.dll|^NATIVE TARGET:')) 'Core/target moved away from B'
        Assert-Role ((Field-Role $bPrepared 'hookInstalled') -eq 0 -and (Field-Role $cPrepared 'hookInstalled') -eq 0) 'Hook installed before preparation'
        Write-Role $b 'baseline'; $null = Until-Role $b 'NATIVE NOT READY:'
        Write-Role $c 'snapshot'; $null = Until-Role $c 'NATIVE NOT READY:'
        if ($order -eq 'B-C') { $bInstalled = Install-Role $b 'B'; $cInstalled = Install-Role $c 'C' }
        else { $cInstalled = Install-Role $c 'C'; $bInstalled = Install-Role $b 'B' }
        $allReady = [Math]::Max((Field-Role $bPrepared 'observedTicks'), (Field-Role $cPrepared 'observedTicks'))
        $bStart = Field-Role $bInstalled 'hookActualBeforeTicks'; $bEnd = Field-Role $bInstalled 'hookActualAfterTicks'
        $cStart = Field-Role $cInstalled 'hookActualBeforeTicks'; $cEnd = Field-Role $cInstalled 'hookActualAfterTicks'
        Assert-Role ($bStart -gt $allReady -and $cStart -gt $allReady -and $bEnd -ge $bStart -and $cEnd -ge $cStart) 'Hook installed before both roles prepared'
        Assert-Role $(if ($order -eq 'B-C') { $bEnd -lt $cStart } else { $cEnd -lt $bStart }) 'Install order not verified by QPC'
        $bRepeat = Install-Role $b 'B'; $cRepeat = Install-Role $c 'C'
        Assert-Role ((Field-Role $bRepeat 'hookActualBeforeTicks') -eq $bStart -and (Field-Role $bRepeat 'hookActualAfterTicks') -eq $bEnd -and (Field-Role $cRepeat 'hookActualBeforeTicks') -eq $cStart -and (Field-Role $cRepeat 'hookActualAfterTicks') -eq $cEnd) 'Duplicate command reinstalled Hook'
        Write-Role $b 'snapshot'; $null = Until-Role $b 'snapshot TARGET KEYS:'
        if ($RawOwner -eq 'C') { Write-Role $c 'snapshot'; $null = Until-Role $c 'snapshot RAW:' }
        Assert-Role ((RawIdentity-Role $rawChild 'snapshot') -eq $rawIdentity) 'Hook installation changed Raw thread/window'
        # Bind the UNCHANGED A safety/metric protocol without ever issuing next/AUTO.
        $target = @($b.Lines | Where-Object { $_.StartsWith('NATIVE TARGET:') })[0]
        $targetMatch = [regex]::Match($target, 'hwnd=(0x[0-9a-fA-F]+) processId=(\d+)')
        Assert-Role $targetMatch.Success 'Missing B target identity'
        Write-Role $a ($targetMatch.Groups[1].Value + ' ' + $targetMatch.Groups[2].Value)
        $null = Until-Role $a 'MANAGED TARGET bound;'
        $null = Until-Role $a 'HUMAN BEFORE:'
        End-Role $a -Eof
        Assert-Role ($a.Process.ExitCode -eq 2 -and -not ($a.Lines -match '^SEND_CALL|^CORE LOADED:')) 'A emitted input or accepted incomplete HUMAN'
        End-Role $b; End-Role $c
        Assert-Role ($b.Process.ExitCode -eq 0 -and $c.Process.ExitCode -eq 0) 'Native roles failed'
        Cleanup-Role $b 'B'; Cleanup-Role $c 'C'
        Write-Output "Native pair $order RawOwner=$RawOwner PASS: both prepared, exact one-shot order, Raw preserved, Core/target retained in B, unchanged A bound without AUTO, clean teardown"
    } finally { Dispose-Role $a; Dispose-Role $b; Dispose-Role $c }
}
foreach ($role in @('B','C')) {
    foreach ($case in @('done-uninstalled','eof-uninstalled','eof-installed')) {
        $child = $null
        try {
            $child = Start-Role "--role-$($role.ToLowerInvariant()) --raw-owner $RawOwner"
            $null = Until-Role $child "NATIVE $role COMMANDS:"
            if ($case -eq 'eof-installed') { $null = Install-Role $child $role }
            End-Role $child -Eof:($case.StartsWith('eof'))
            Assert-Role ($child.Process.ExitCode -eq 2 -and $child.Lines -match '^NOT RUN:') 'Incomplete role session accepted'
            Cleanup-Role $child $role
        } finally { Dispose-Role $child }
    }
}
foreach ($arguments in @('','--invalid','--role-b --role-c','--role-b extra', '--test --raw-owner C', '--role-b --raw-owner Z', '--role-c --raw-owner', '--role-b --wrong C', '--role-b --raw-owner C extra')) {
    $child = $null
    try {
        $child = Start-Role $arguments
        End-Role $child -Eof
        Assert-Role ($child.Process.ExitCode -eq 1 -and -not ($child.Lines -match '^ROLE MODULE:|^NATIVE TARGET:')) 'Invalid CLI entered startup'
    } finally { Dispose-Role $child }
}
Write-Output "Native subprocess contract RawOwner=$RawOwner PASS: three A/B/C order pairs, six incomplete-session cleanups, nine pre-start rejections; physical SendInput/HUMAN NOT RUN"
