$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$testOutput = (Resolve-Path 'Rug.UI.Tests/bin/x64/Debug/net10.0').Path
$root = Join-Path $testOutput ('instance-ui-' + [Guid]::NewGuid().ToString('N'))
$plugin = Join-Path $root 'timeline_probe'
$previous = $env:RUG_PLUGINS_ROOT
$app = $null

function Find-Name($window, [string]$name, $controlType = $null) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    if ($null -ne $controlType) {
        $typeCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
        $condition = [System.Windows.Automation.AndCondition]::new($condition, $typeCondition)
    }
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Await-Name($window, [string]$name, [int]$seconds = 6, $controlType = $null) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $seconds) {
        $found = Find-Name $window $name $controlType
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 50
    }
    $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $names = @($all | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -Unique)
    throw "UI element was not found: $name; names=$($names -join ' | ')"
}
function Await-NotName($window, [string]$name, [int]$seconds = 6) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $seconds) {
        if ($null -eq (Find-Name $window $name)) { return }
        Start-Sleep -Milliseconds 50
    }
    throw "Stale UI element remains: $name"
}
function Invoke-Button($window, [string]$name) {
    $button = Await-Name $window $name 6 ([System.Windows.Automation.ControlType]::Button)
    $ready = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $button.Current.IsEnabled -and $ready.Elapsed.TotalSeconds -lt 6) { Start-Sleep -Milliseconds 25 }
    if (-not $button.Current.IsEnabled) { throw "UI button is disabled: $name callerLine=$((Get-PSCallStack)[1].ScriptLineNumber)" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Selected-Run($window) {
    $picker = Await-Name $window '当前实例' 6 ([System.Windows.Automation.ControlType]::ComboBox)
    $selection = $picker.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selection.Count -ne 1) { throw 'Expected one selected instance.' }
    return $selection[0].Current.Name
}
function Assert-RetainedPausedView($window, [string]$titleRuntimeId, [string]$selectedRun) {
    $title = Await-Name $window 'Reloaded timeline test'
    if (($title.GetRuntimeId() -join ',') -ne $titleRuntimeId) { throw 'Failed update replaced the old UI tree.' }
    $null = Await-Name $window 'Run B step'
    $null = Await-Name $window '已暂停'
    if ((Selected-Run $window) -ne $selectedRun) { throw 'Failed update changed the selected run generation.' }
}

try {
    New-Item -ItemType Directory -Path $plugin -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $plugin 'manifest.json'),
        '{"id":"timeline_probe","name":"Timeline Probe","version":"1.0","entry":"main.lua","targetProcess":"probe","permissions":["timer"]}')
    [System.IO.File]::WriteAllText((Join-Path $plugin 'main.lua'),
        'function on_tick() end')
    $schema = @'
{"type":"Page","id":"root","title":"Custom timeline test","children":[
 {"type":"ButtonCard","id":"start-a","title":"Start A","command":"host.start_windowless",
  "parameter":{"probe.timeline":[{"Id":"a","Title":"Run A step","Status":"Running","Thumbnail":null}]}},
 {"type":"ButtonCard","id":"start-b","title":"Start B","command":"host.start_windowless",
  "parameter":{"probe.timeline":[{"Id":"b","Title":"Run B step","Status":"Succeeded","Thumbnail":null}]}},
 {"type":"ButtonCard","id":"stop","title":"Stop selected","command":"host.stop_selected"},
 {"type":"StepFlowView","id":"flow","title":"Probe steps","stateKey":"probe.timeline","steps":[]},
 {"type":"TextCard","id":"setting","title":"Persisted setting","stateKey":"draft","default":"keep"}
]}
'@
    $ui = Join-Path $plugin 'ui.json'
    [System.IO.File]::WriteAllText($ui, $schema)
    $env:RUG_PLUGINS_ROOT = $root
    $appPath = (Resolve-Path 'Rug.UI/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/Rug.UI.exe').Path
    $app = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -WindowStyle Hidden -PassThru
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $window = $null
    while ($watch.Elapsed.TotalSeconds -lt 8 -and $null -eq $window) {
        $app.Refresh()
        if ($app.HasExited) { throw "App exited: $($app.ExitCode)" }
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 100 }
    }
    if ($null -eq $window) { throw 'Main window was not found.' }
    $null = Await-Name $window 'Timeline Probe'
    $null = Await-Name $window 'Custom timeline test'
    Invoke-Button $window 'Start A'
    Invoke-Button $window 'Deny permissions'
    Start-Sleep -Milliseconds 200
    if ($null -ne (Find-Name $window 'Run A step')) { throw 'Denied plugin started.' }
    Invoke-Button $window 'Start A'
    Invoke-Button $window 'Allow permissions'
    $null = Await-Name $window 'Run A step'
    Invoke-Button $window 'Start B'
    Invoke-Button $window 'Next instance'
    $null = Await-Name $window 'Run B step'
    Await-NotName $window 'Run A step'
    Invoke-Button $window 'Next instance'
    $null = Await-Name $window 'Run A step'
    Await-NotName $window 'Run B step'
    Invoke-Button $window 'Stop selected'
    $null = Await-Name $window 'Run B step'
    Await-NotName $window 'Run A step'
    Invoke-Button $window 'Pause instance'
    $null = Await-Name $window '已暂停'
    [System.IO.File]::WriteAllText($ui, $schema.Replace('Custom timeline test', 'Reloaded timeline test'))
    $null = Await-Name $window 'Reloaded timeline test'
    $null = Await-Name $window 'Run B step'
    $null = Await-Name $window '已暂停'
    $reloadedSchema = $schema.Replace('Custom timeline test', 'Reloaded timeline test')
    $titleRuntimeId = ((Await-Name $window 'Reloaded timeline test').GetRuntimeId() -join ',')
    $selectedRun = Selected-Run $window
    # A fresh key's first default must participate in later reads during this same build.
    # A title-only retry must also fail; neither failed candidate may publish its staged config.
    $conflict = '{"type":"Page","id":"staged-root","title":"Staged conflict","children":[{"type":"TextCard","id":"staged-text","title":"String default","stateKey":"staged_collision","default":"text"},{"type":"ToggleCard","id":"staged-toggle","title":"Bool default","stateKey":"staged_collision","default":false}]}'
    foreach ($conflictTitle in @('Staged conflict', 'Staged conflict retry')) {
        [System.IO.File]::WriteAllText($ui, $conflict.Replace('Staged conflict', $conflictTitle))
        $null = Await-Name $window '热重载失败'
        Assert-RetainedPausedView $window $titleRuntimeId $selectedRun
        Await-NotName $window $conflictTitle
        [System.IO.File]::WriteAllText($ui, $reloadedSchema)
        Await-NotName $window '热重载失败'
    }
    # Successfully binding that same key as bool proves the rejected string default never entered PluginConfig.
    $cleanConfig = $reloadedSchema | ConvertFrom-Json
    $cleanConfig.title = 'Verified clean config'
    $cleanConfig.children += [pscustomobject]@{
        type = 'ToggleCard'; id = 'clean-toggle'; title = 'Fresh config default'
        stateKey = 'staged_collision'; default = $true
    }
    [System.IO.File]::WriteAllText($ui, ($cleanConfig | ConvertTo-Json -Depth 30))
    $null = Await-Name $window 'Verified clean config'
    $null = Await-Name $window 'Run B step'
    $null = Await-Name $window '已暂停'
    if ((Selected-Run $window) -ne $selectedRun) { throw 'Config verification changed the run generation.' }
    $toggleCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::IsTogglePatternAvailableProperty, $true)
    $toggle = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $toggleCondition)
    if ($null -eq $toggle -or $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::On) { throw 'Rejected defaults polluted the fresh bool config.' }
    [System.IO.File]::WriteAllText($ui, $reloadedSchema)
    $null = Await-Name $window 'Reloaded timeline test'
    # Valid schema, but persisted string cannot bind to a bool control: actual UIFactory build must fail safely.
    [System.IO.File]::WriteAllText($ui,'{"type":"Page","id":"bad-root","title":"Must not replace","children":[{"type":"ToggleCard","id":"bad-toggle","title":"Bad type","stateKey":"draft","default":false}]}')
    $null = Await-Name $window '热重载失败'
    $null = Await-Name $window 'Reloaded timeline test'
    $null = Await-Name $window 'Run B step'
    $null = Await-Name $window '已暂停'
    Await-NotName $window 'Must not replace'
    [System.IO.File]::WriteAllText($ui, $schema.Replace('Custom timeline test', 'Reloaded timeline test'))
    Await-NotName $window '热重载失败'
    Invoke-Button $window 'Resume instance'
    $null = Await-Name $window '运行中'
    Invoke-Button $window 'Stop selected'
    Await-NotName $window 'Run B step'
    Invoke-Button $window 'Start A'
    $null = Await-Name $window 'Run A step'
    Invoke-Button $window 'Stop selected'
    Await-NotName $window 'Run A step'
    Invoke-Button $window 'Start A'
    $null = Await-Name $window 'Run A step'
    Invoke-Button $window 'Plugin permissions'
    Invoke-Button $window 'Revoke permissions'
    Await-NotName $window 'Run A step'
    Invoke-Button $window 'Start B'
    Invoke-Button $window 'Deny permissions'
    Start-Sleep -Milliseconds 200
    if ($null -ne (Find-Name $window 'Run B step')) { throw 'Revoked plugin restarted without approval.' }
    Invoke-Button $window 'Start B'
    Invoke-Button $window 'Allow permissions'
    $null = Await-Name $window 'Run B step'
    Invoke-Button $window 'Stop selected'
    Await-NotName $window 'Run B step'
    Write-Output 'generic-instance-ui-ok customKey=probe.timeline staged-conflict=initial/title-retry old-ui/config/paused-run=retained consent=allow/deny/revoke/regrant'
}
finally {
    $env:RUG_PLUGINS_ROOT = $previous
    if ($null -ne $app) {
        $app.Refresh()
        if (-not $app.HasExited) {
            $null = $app.CloseMainWindow()
            if (-not $app.WaitForExit(3000)) { Stop-Process -Id $app.Id -ErrorAction SilentlyContinue }
        }
    }
    $full = [System.IO.Path]::GetFullPath($root)
    $allowed = [System.IO.Path]::GetFullPath($testOutput).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if (-not $full.StartsWith($allowed + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase) -or
        -not ([System.IO.Path]::GetFileName($full)).StartsWith('instance-ui-',
        [System.StringComparison]::Ordinal)) {
        throw 'Refusing to delete an unexpected test directory.'
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
