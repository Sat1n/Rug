# Diagnostic subprocess contract tests only: no SendInput and no human acceptance.
param([ValidateSet('B','C')][string]$RawOwner = 'B', [ValidateSet('B','C')][string]$TargetOwner = 'B', [ValidateSet('on','off')][string]$CoreLoad = 'on')
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
function TargetIdentity-Role($child, [string]$phase) {
    $line = @($child.Lines | Where-Object { $_.StartsWith("$phase TARGET threadId=") })[-1]
    $match = [regex]::Match($line, 'threadId=(\d+) hwnd=(0x[0-9a-fA-F]+) generation=(\d+)')
    Assert-Role $match.Success 'Missing target identity'; return $match.Value
}
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
        $b = Start-Role "--role-b --raw-owner $RawOwner --target-owner $TargetOwner --core-load $CoreLoad"
        $bPrepared = Until-Role $b 'NATIVE B PREPARED:'
        $null = Until-Role $b 'NATIVE B COMMANDS:'
        $c = Start-Role "--role-c --raw-owner $RawOwner --target-owner $TargetOwner --core-load $CoreLoad"
        $cPrepared = Until-Role $c 'NATIVE C PREPARED:'
        $null = Until-Role $c 'NATIVE C COMMANDS:'
        $rawChild = if ($RawOwner -eq 'B') { $b } else { $c }
        $targetChild = if ($TargetOwner -eq 'B') { $b } else { $c }
        $nonTargetChild = if ($TargetOwner -eq 'B') { $c } else { $b }
        $targetIdentity = TargetIdentity-Role $targetChild 'PREPARED'
        $rawIdentity = RawIdentity-Role $rawChild 'PREPARED'
        $bRegistrations = if ($RawOwner -eq 'B') { 1 } else { 0 }
        $cRegistrations = 1 - $bRegistrations
        $bTarget = if ($TargetOwner -eq 'B') { 1 } else { 0 }
        $cTarget = 1 - $bTarget
        Assert-Role ($bPrepared -match "clrLoaded=0 target=$bTarget rawRegistrations=$bRegistrations rawOwner=$RawOwner" -and $cPrepared -match "clrLoaded=0 target=$cTarget rawRegistrations=$cRegistrations rawOwner=$RawOwner" -and $bPrepared -match "targetOwner=$TargetOwner" -and $cPrepared -match "targetOwner=$TargetOwner") 'Role isolation/registration incorrect'
        $rawPrepared = if ($RawOwner -eq 'B') { $bPrepared } else { $cPrepared }
        Assert-Role ((Field-Role $rawPrepared 'rawReadyObservedTicks') -gt 0) 'Raw ownership did not await readiness'
        Assert-Role (@($rawChild.Lines | Where-Object { $_ -match '^ROLE MODULE:.*Rug.NativeRawReceiver.dll' }).Count -eq 1) 'Raw owner module evidence missing'
        $nonRawChild = if ($RawOwner -eq 'B') { $c } else { $b }
        Assert-Role (-not ($nonRawChild.Lines -match '^ROLE MODULE:.*Rug.NativeRawReceiver.dll')) 'Nonowner loaded Raw module'
        $expectedCore = [int]($CoreLoad -eq 'on')
        Assert-Role (@($b.Lines | Where-Object { $_ -match '^ROLE MODULE:.*Rug.Core.dll' }).Count -eq $expectedCore -and -not ($c.Lines -match '^ROLE MODULE:.*Rug.Core.dll') -and (Field-Role $bPrepared 'coreLoaded') -eq $expectedCore -and (Field-Role $cPrepared 'coreLoaded') -eq 0 -and $bPrepared -match "coreLoad=$CoreLoad" -and $cPrepared -match "coreLoad=$CoreLoad") 'Actual Core presence disagrees with mode/role'
        Assert-Role (@($targetChild.Lines | Where-Object { $_ -match '^NATIVE TARGET:' }).Count -eq 1 -and -not ($nonTargetChild.Lines -match '^NATIVE TARGET:|^PREPARED TARGET')) 'Target created in wrong role'
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
        Write-Role $b 'snapshot'; $null = Until-Role $b 'NATIVE B snapshot:'
        if ($RawOwner -eq 'B') { $null = Until-Role $b 'snapshot RAW:' }
        if ($TargetOwner -eq 'B') { $null = Until-Role $b 'snapshot TARGET KEYS:' }
        Write-Role $c 'snapshot'; $null = Until-Role $c 'NATIVE C snapshot:'
        if ($RawOwner -eq 'C') { $null = Until-Role $c 'snapshot RAW:' }
        if ($TargetOwner -eq 'C') { $null = Until-Role $c 'snapshot TARGET KEYS:' }
        Assert-Role ((RawIdentity-Role $rawChild 'snapshot') -eq $rawIdentity) 'Hook installation changed Raw thread/window'
        Assert-Role ((TargetIdentity-Role $targetChild 'snapshot') -eq $targetIdentity) 'Hook installation changed target thread/window/generation'
        foreach ($roleChild in @($b,$c)) {
            $expected = if ($roleChild -eq $b) { $expectedCore } else { 0 }
            $phaseLines = @($roleChild.Lines | Where-Object { $_ -match '^NATIVE [BC] (PREPARED|INSTALLED|snapshot):' })
            foreach ($phaseLine in $phaseLines) { Assert-Role ((Field-Role $phaseLine 'coreLoaded') -eq $expected -and $phaseLine -match "coreLoad=$CoreLoad") 'Core mode changed after preparation' }
        }
        # Bind the UNCHANGED A safety/metric protocol without ever issuing next/AUTO.
        $target = @($targetChild.Lines | Where-Object { $_.StartsWith('NATIVE TARGET:') })[0]
        $targetMatch = [regex]::Match($target, 'hwnd=(0x[0-9a-fA-F]+) processId=(\d+)')
        Assert-Role $targetMatch.Success 'Missing selected target identity'
        Write-Role $a ($targetMatch.Groups[1].Value + ' ' + $targetMatch.Groups[2].Value)
        $null = Until-Role $a 'MANAGED TARGET bound;'
        $null = Until-Role $a 'HUMAN BEFORE:'
        End-Role $a -Eof
        Assert-Role ($a.Process.ExitCode -eq 2 -and -not ($a.Lines -match '^SEND_CALL|^CORE LOADED:')) 'A emitted input or accepted incomplete HUMAN'
        End-Role $b; End-Role $c
        Assert-Role ($b.Process.ExitCode -eq 0 -and $c.Process.ExitCode -eq 0) 'Native roles failed'
        Cleanup-Role $b 'B'; Cleanup-Role $c 'C'
        Write-Output "Native pair $order RawOwner=$RawOwner TargetOwner=$TargetOwner CoreLoad=$CoreLoad PASS: both prepared, exact one-shot order, Raw/target identities preserved, actual Core presence verified, unchanged A bound to selected target without AUTO, clean teardown"
    } finally { Dispose-Role $a; Dispose-Role $b; Dispose-Role $c }
}
foreach ($role in @('B','C')) {
    foreach ($case in @('done-uninstalled','eof-uninstalled','eof-installed')) {
        $child = $null
        try {
            $child = Start-Role "--role-$($role.ToLowerInvariant()) --raw-owner $RawOwner --target-owner $TargetOwner --core-load $CoreLoad"
            $null = Until-Role $child "NATIVE $role COMMANDS:"
            if ($case -eq 'eof-installed') { $null = Install-Role $child $role }
            End-Role $child -Eof:($case.StartsWith('eof'))
            Assert-Role ($child.Process.ExitCode -eq 2 -and $child.Lines -match '^NOT RUN:') 'Incomplete role session accepted'
            Cleanup-Role $child $role
        } finally { Dispose-Role $child }
    }
}
$invalidArguments = @('','--invalid','--role-b --role-c','--role-b extra', '--test --raw-owner C', '--role-b --raw-owner Z', '--role-c --raw-owner', '--role-b --wrong C', '--role-b --raw-owner C extra', '--test --target-owner C', '--role-b --target-owner Z', '--role-c --target-owner', '--role-b --target-owner B --target-owner C', '--role-c --raw-owner B --raw-owner C', '--role-b --raw-owner B --target-owner Z', '--role-b --target-owner C --wrong B', '--role-b --core-load bad', '--role-c --core-load', '--test --core-load off', '--role-b --core-load on --core-load off', '--role-b --raw-owner B --target-owner B --wrong off', '--role-c --raw-owner B --core-load off extra')
foreach ($arguments in $invalidArguments) {
    $child = $null
    try {
        $child = Start-Role $arguments
        End-Role $child -Eof
        Assert-Role ($child.Process.ExitCode -eq 1 -and -not ($child.Lines -match '^ROLE MODULE:|^NATIVE TARGET:')) 'Invalid CLI entered startup'
    } finally { Dispose-Role $child }
}
$validArguments = @(
    @{ Args='--role-b'; Role='B'; Raw='B'; Target='B'; Core='on' },
    @{ Args='--role-c'; Role='C'; Raw='B'; Target='B'; Core='on' },
    @{ Args='--role-b --raw-owner C'; Role='B'; Raw='C'; Target='B'; Core='on' },
    @{ Args='--role-c --target-owner C'; Role='C'; Raw='B'; Target='C'; Core='on' },
    @{ Args='--role-c --target-owner C --raw-owner B'; Role='C'; Raw='B'; Target='C'; Core='on' },
    @{ Args='--role-b --core-load off'; Role='B'; Raw='B'; Target='B'; Core='off' },
    @{ Args='--role-c --core-load on'; Role='C'; Raw='B'; Target='B'; Core='on' },
    @{ Args='--role-b --core-load off --target-owner C --raw-owner B'; Role='B'; Raw='B'; Target='C'; Core='off' }
)
foreach ($case in $validArguments) {
    $child = $null
    try {
        $child = Start-Role $case.Args
        $prepared = Until-Role $child "NATIVE $($case.Role) PREPARED:"
        $null = Until-Role $child "NATIVE $($case.Role) COMMANDS:"
        Assert-Role ($prepared -match "rawOwner=$($case.Raw)" -and $prepared -match "targetOwner=$($case.Target)" -and (Field-Role $prepared 'target') -eq [int]($case.Role -eq $case.Target) -and (Field-Role $prepared 'rawRegistrations') -eq [int]($case.Role -eq $case.Raw)) 'Default/standalone/reversed owner flags changed ownership'
        $expected = [int]($case.Core -eq 'on' -and $case.Role -eq 'B')
        Assert-Role ($prepared -match "coreLoad=$($case.Core)" -and (Field-Role $prepared 'coreLoaded') -eq $expected -and @($child.Lines | Where-Object { $_ -match '^ROLE MODULE:.*Rug.Core.dll' }).Count -eq $expected) 'Default/optional Core flag changed actual module presence'
        End-Role $child -Eof
        Assert-Role ($child.Process.ExitCode -eq 2 -and $child.Lines -match '^NOT RUN:' -and $child.Lines -match "NATIVE $($case.Role) CLEANUP: threadExited=1 attached=0 targetHwnd=0 rawRegistrations=0 fault=None") 'Legacy/optional CLI did not clean up incomplete session'
    } finally { Dispose-Role $child }
}
Write-Output "Native subprocess contract RawOwner=$RawOwner TargetOwner=$TargetOwner CoreLoad=$CoreLoad PASS: three A/B/C order pairs, six incomplete-session cleanups, $($invalidArguments.Count) pre-start rejections, $($validArguments.Count) default/optional CLI checks; physical SendInput/HUMAN NOT RUN"
