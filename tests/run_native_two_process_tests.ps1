# No AUTO: verifies the existing two-process diagnostic contract, not Hook health.
$ErrorActionPreference = 'Stop'
$probeRoot = Split-Path -Parent $PSScriptRoot
$senderPath = Join-Path $probeRoot 'x64/Debug/Rug.NativeInputProbe.exe'
$rolePath = Join-Path $probeRoot 'x64/Debug/Rug.NativeRoleProbe.exe'
$fixedPaths = @($senderPath, $rolePath) + @('Rug.Core.dll','Rug.NativeKeyboardHook.dll','Rug.NativeRawReceiver.dll' | ForEach-Object { Join-Path $probeRoot "x64/Debug/$_" })
$beforeHashes = @{}
foreach ($path in $fixedPaths) { $beforeHashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
$bArguments = '--role-b --raw-owner B --target-owner B --core-load off --raw-registration on --device-notify off --input-sink off'
function Assert-Probe($condition, [string]$message) { if (-not $condition) { throw $message } }
function Start-Probe([string]$path, [string]$arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($path)
    $start.Arguments = $arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    return [pscustomobject]@{ Process=$process; Lines=[Collections.Generic.List[string]]::new(); Errors=$process.StandardError.ReadToEndAsync() }
}
function Read-Probe($child) {
    $pending = $child.Process.StandardOutput.ReadLineAsync()
    Assert-Probe ($pending.Wait(15000)) 'Diagnostic output timed out'
    $line = $pending.GetAwaiter().GetResult()
    if ($null -ne $line) { Assert-Probe ($child.Lines.Count -lt 400) 'Diagnostic output limit exceeded'; $child.Lines.Add($line) }
    return $line
}
function Until-Probe($child, [string]$prefix) {
    while ($true) { $line=Read-Probe $child; Assert-Probe ($null -ne $line) "Exited before $prefix"; if ($line.StartsWith($prefix)) { return $line } }
}
function Write-Probe($child, [string]$command) { $child.Process.StandardInput.WriteLine($command); $child.Process.StandardInput.Flush() }
function End-Probe($child, [switch]$Eof) {
    if ($Eof) { $child.Process.StandardInput.Close() } else { Write-Probe $child 'done' }
    while ($null -ne (Read-Probe $child)) { }
    Assert-Probe ($child.Process.WaitForExit(15000)) 'Diagnostic exit timed out'
    Assert-Probe ([string]::IsNullOrEmpty($child.Errors.GetAwaiter().GetResult())) 'Unexpected diagnostic stderr'
}
function Dispose-Probe($child) {
    if ($null -eq $child) { return }
    if (-not $child.Process.HasExited) {
        $child.Process.StandardInput.Close()
        if (-not $child.Process.WaitForExit(5000)) { $child.Process.Kill(); $child.Process.WaitForExit() }
    }
    $child.Process.Dispose()
}
function Field-Probe([string]$line, [string]$field) {
    $match=[regex]::Match($line, ('\b'+[regex]::Escape($field)+'=(\d+)'))
    Assert-Probe $match.Success "Missing $field in $line"
    return [long]::Parse($match.Groups[1].Value)
}
function Line-Probe($child, [string]$prefix) {
    $lines=@($child.Lines | Where-Object { $_.StartsWith($prefix) })
    Assert-Probe ($lines.Count -gt 0) "Missing $prefix"
    return $lines[-1]
}
# @shape child output: bounded detached strings; expected identities/call: immutable strings.
# @source NativeInputProbe/NativeRoles.cpp#function:RunRole
function Check-Phase($b, [string]$phase, $prepared, $rawIdentity, $targetIdentity, $registration) {
    $summary=Line-Probe $b "NATIVE B ${phase}:"
    Assert-Probe ($summary -match 'clrLoaded=0 target=1 rawRegistrations=1 rawOwner=B' -and $summary -match 'targetOwner=B' -and $summary -match 'coreLoad=off' -and $summary -match 'rawRegistration=on' -and $summary -match 'deviceNotify=off' -and $summary -match 'inputSink=off') 'Two-process conditions changed'
    Assert-Probe ((Field-Probe $summary 'coreLoaded') -eq 0 -and (Field-Probe $summary 'receiverReady') -eq 1) 'Unexpected Core or unready receiver'
    $raw=Line-Probe $b "$phase RAW:"
    $target=Line-Probe $b "$phase TARGET threadId="
    $call=Line-Probe $b "$phase RAW_REGISTER_CALL:"
    Assert-Probe ([regex]::Match($raw,'threadId=(\d+) hwnd=(0x[0-9a-fA-F]+)').Value -eq $rawIdentity -and [regex]::Match($target,'threadId=(\d+) hwnd=(0x[0-9a-fA-F]+) generation=(\d+)').Value -eq $targetIdentity) 'Receiver/target identity changed'
    Assert-Probe ((Field-Probe $raw 'ready') -eq 1 -and (Field-Probe $raw 'fault') -eq 0) 'Receiver fault'
    Assert-Probe ($call.Substring($call.IndexOf(':')) -eq $registration.Substring($registration.IndexOf(':'))) 'Commands re-registered or changed registration evidence'
    Assert-Probe ((Field-Probe $summary 'rawReadyObservedTicks') -eq (Field-Probe $prepared 'rawReadyObservedTicks')) 'Readiness generation changed'
}
function Check-Cleanup($b) {
    Assert-Probe ($b.Lines -match '^NATIVE B CLEANUP: threadExited=1 attached=0 targetHwnd=0 rawRegistrations=0 fault=None') 'Incomplete B cleanup'
    $raw=Line-Probe $b 'STOPPED RAW:'
    Assert-Probe ((Field-Probe $raw 'input') -eq (Field-Probe $raw 'cleanup')) 'WM_INPUT cleanup mismatch'
}
# @source NativeInputProbe/NativeRoles.cpp#function:RunRole
# Only A and B are started here; no C and no next command are issued.
foreach ($round in 1..3) {
    $a=$null; $b=$null
    try {
        $a=Start-Probe $senderPath '--managed-target'
        $null=Until-Probe $a 'MANAGED TARGET:'
        $b=Start-Probe $rolePath $bArguments
        $prepared=Until-Probe $b 'NATIVE B PREPARED:'
        $null=Until-Probe $b 'NATIVE B COMMANDS:'
        Assert-Probe ($a.Process.Id -ne $b.Process.Id -and (Field-Probe $prepared 'hookInstalled') -eq 0) 'Invalid preparation'
        Assert-Probe (-not ($b.Lines -match '^ROLE MODULE:.*Rug.Core.dll|^NATIVE C')) 'B loaded Core or C entered session'
        foreach ($name in @('Rug.NativeKeyboardHook.dll','Rug.NativeRawReceiver.dll')) {
            $matching=@($b.Lines | Where-Object { $_ -match '^ROLE MODULE:' -and $_.Contains($name) })
            Assert-Probe ($matching.Count -eq 1 -and $matching[0].Contains($beforeHashes[(Join-Path $probeRoot "x64/Debug/$name")])) 'Loaded diagnostic module hash mismatch'
        }
        $raw=Line-Probe $b 'PREPARED RAW:'
        $rawIdentity=[regex]::Match($raw,'threadId=(\d+) hwnd=(0x[0-9a-fA-F]+)').Value
        $targetIdentity=[regex]::Match((Line-Probe $b 'PREPARED TARGET threadId='),'threadId=(\d+) hwnd=(0x[0-9a-fA-F]+) generation=(\d+)').Value
        $registration=Line-Probe $b 'PREPARED RAW_REGISTER_CALL:'
        Assert-Probe ($rawIdentity -ne '' -and $targetIdentity -ne '' -and (Field-Probe $raw 'threadId') -gt 0) 'Missing native identities'
        Assert-Probe ((Field-Probe $registration 'requestedFlags') -eq 0 -and (Field-Probe $registration 'queriedFlags') -eq 0 -and (Field-Probe $registration 'succeeded') -eq 1 -and (Field-Probe $registration 'error') -eq 0 -and (Field-Probe $registration 'beforeTicks') -gt 0 -and (Field-Probe $registration 'afterTicks') -ge (Field-Probe $registration 'beforeTicks') -and [regex]::Match($registration,'threadId=(\d+) hwnd=(0x[0-9a-fA-F]+)').Value -eq $rawIdentity) 'Flags-zero registration not proven'
        Check-Phase $b 'PREPARED' $prepared $rawIdentity $targetIdentity $registration
        foreach ($command in @('baseline','snapshot')) { Write-Probe $b $command; $null=Until-Probe $b 'NATIVE NOT READY:' }
        Write-Probe $b 'install'; $installed=Until-Probe $b 'NATIVE B INSTALLED:'
        $start=Field-Probe $installed 'hookActualBeforeTicks'; $end=Field-Probe $installed 'hookActualAfterTicks'
        Assert-Probe ($start -gt (Field-Probe $prepared 'observedTicks') -and $start -gt (Field-Probe $prepared 'rawReadyObservedTicks') -and $end -ge $start -and (Field-Probe $installed 'hookInstalled') -eq 1) 'Hook installed before B preparation or failed'
        Write-Probe $b 'install'; $repeat=Until-Probe $b 'NATIVE B INSTALLED:'
        Assert-Probe ((Field-Probe $repeat 'hookActualBeforeTicks') -eq $start -and (Field-Probe $repeat 'hookActualAfterTicks') -eq $end) 'Repeated command reinstalled Hook'
        foreach ($phase in @('baseline','snapshot')) {
            Write-Probe $b $phase; $null=Until-Probe $b "$phase TARGET KEYS:"
            Check-Phase $b $phase $prepared $rawIdentity $targetIdentity $registration
            $hook=Line-Probe $b "$phase CALLBACK:"
            $owner=[regex]::Match($hook,'callbackModule=((?:0x)?[0-9a-fA-F]+)').Groups[1].Value
            $hmod=[regex]::Match($hook,'hookInstallHmod=((?:0x)?[0-9a-fA-F]+)').Groups[1].Value
            Assert-Probe ($hook -match 'attached=1 threadMismatch=0' -and $owner -ne '' -and [Convert]::ToUInt64($owner.Replace('0x',''),16) -gt 0 -and $owner -eq $hmod -and (Field-Probe $hook 'boundThreadId') -gt 0) 'Hook association or thread fault'
        }
        $target=[regex]::Match((Line-Probe $b 'NATIVE TARGET:'),'hwnd=(0x[0-9a-fA-F]+) processId=(\d+)')
        Assert-Probe ($target.Success -and [int]$target.Groups[2].Value -eq $b.Process.Id) 'Target not owned by B'
        Write-Probe $a ($target.Groups[1].Value+' '+$target.Groups[2].Value)
        $null=Until-Probe $a 'MANAGED TARGET bound;'; $null=Until-Probe $a 'HUMAN BEFORE:'
        End-Probe $a -Eof
        Assert-Probe ($a.Process.ExitCode -eq 2 -and -not ($a.Lines -match '^SEND_CALL|^CORE LOADED:')) 'Incomplete A accepted or emitted input'
        End-Probe $b
        Assert-Probe ($b.Process.ExitCode -eq 0) 'B diagnostic lifecycle failed'
        Check-Cleanup $b
        Write-Output "A+B round $round PASS: no C created, flags=0 registered, one-shot Hook, stable target/receiver, A bound without AUTO, complete cleanup"
    } finally { Dispose-Probe $a; Dispose-Probe $b }
}
foreach ($case in @('done-uninstalled','eof-uninstalled','eof-installed')) {
    $b=$null
    try {
        $b=Start-Probe $rolePath $bArguments; $null=Until-Probe $b 'NATIVE B COMMANDS:'
        if ($case -eq 'eof-installed') { Write-Probe $b 'install'; $null=Until-Probe $b 'NATIVE B INSTALLED:' }
        End-Probe $b -Eof:($case.StartsWith('eof'))
        Assert-Probe ($b.Process.ExitCode -eq 2 -and $b.Lines -match '^NOT RUN:') 'Incomplete session accepted'
        Check-Cleanup $b
    } finally { Dispose-Probe $b }
}
foreach ($path in $fixedPaths) { Assert-Probe ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $beforeHashes[$path]) "Binary changed: $path" }
Write-Output 'PASS: three A+B sessions, three incomplete B cleanups, five binaries unchanged. Physical AUTO/HUMAN and Hook observation NOT RUN; Raw classification NOT COLLECTED.'
