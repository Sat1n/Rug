$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$testOutput = (Resolve-Path 'Rug.UI.Tests/bin/x64/Debug/net10.0').Path
$root = Join-Path $testOutput ('lifecycle-ui-' + [Guid]::NewGuid().ToString('N'))
$plugin = Join-Path $root 'lifecycle_probe'
$previous = $env:RUG_PLUGINS_ROOT
$app = $null

function Find-Name($window, [string]$name, $controlType = $null) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    if ($null -ne $controlType) {
        $condition = [System.Windows.Automation.AndCondition]::new($condition,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType))
    }
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Await-Name($window, [string]$name, $controlType = $null) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 8) {
        $found = Find-Name $window $name $controlType
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 25
    }
    throw "UI element missing: $name; after action=$script:lastAction"
}
function Invoke-Button($window, [string]$name) {
    $script:lastAction = $name
    $button = Await-Name $window $name ([System.Windows.Automation.ControlType]::Button)
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $button.Current.IsEnabled -and $watch.Elapsed.TotalSeconds -lt 8) { Start-Sleep -Milliseconds 25 }
    if (-not $button.Current.IsEnabled) { throw "Button disabled: $name" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Check-Controls($window, [bool]$paused) {
    foreach ($entry in @(@('Pause instance', -not $paused), @('Resume instance', $paused), @('Stop instance', $true))) {
        $button = Await-Name $window $entry[0] ([System.Windows.Automation.ControlType]::Button)
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        while ($button.Current.IsEnabled -ne $entry[1] -and $watch.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 25 }
        if ($button.Current.IsEnabled -ne $entry[1]) { throw "Wrong enabled state: $($entry[0])" }
    }
}

try {
    New-Item -ItemType Directory -Path $plugin -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $plugin 'manifest.json'),
        '{"id":"lifecycle_probe","name":"Lifecycle Probe","version":"1","targetProcess":"test","permissions":["timer"]}')
    [System.IO.File]::WriteAllText((Join-Path $plugin 'main.lua'), 'function on_tick() rug.sleep(60000) end')
    $schema = @'
{"type":"Page","id":"root","title":"Lifecycle before reload","children":[
 {"type":"ButtonCard","id":"start-a","title":"Start A","command":"host.start_windowless","parameter":{"lifecycle.steps":[{"Id":"a","Title":"Lifecycle A","Status":"Running"}]}},
 {"type":"ButtonCard","id":"start-b","title":"Start B","command":"host.start_windowless","parameter":{"lifecycle.steps":[{"Id":"b","Title":"Lifecycle B","Status":"Running"}]}},
 {"type":"StepFlowView","id":"flow","title":"Steps","stateKey":"lifecycle.steps","steps":[]}
]}
'@
    $ui = Join-Path $plugin 'ui.json'
    [System.IO.File]::WriteAllText($ui, $schema)
    $env:RUG_PLUGINS_ROOT = $root
    $appPath = (Resolve-Path 'Rug.UI/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/Rug.UI.exe').Path
    $app = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -WindowStyle Hidden -PassThru
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
    $window = $null
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($null -eq $window -and $watch.Elapsed.TotalSeconds -lt 12) {
        if ($app.HasExited) { throw "App exited: $($app.ExitCode)" }
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 50 }
    }
    if ($null -eq $window) { throw 'No test app window.' }
    $null = Await-Name $window 'Lifecycle before reload'
    Invoke-Button $window 'Start A'
    Invoke-Button $window 'Allow permissions'
    $null = Await-Name $window 'Lifecycle A'
    $null = Await-Name $window '运行中'
    Check-Controls $window $false
    Invoke-Button $window 'Pause instance'
    $null = Await-Name $window '已暂停'
    Check-Controls $window $true
    Invoke-Button $window 'Start B'
    Invoke-Button $window 'Next instance'
    $null = Await-Name $window 'Lifecycle B'
    $null = Await-Name $window '运行中'
    Invoke-Button $window 'Next instance'
    $null = Await-Name $window 'Lifecycle A'
    $null = Await-Name $window '已暂停'
    Check-Controls $window $true
    # Manual global takeover preserves this paused timer run and must not auto-resume it.
    Invoke-Button $window 'Manual takeover'
    $null = Await-Name $window '人工控制已生效'
    $null = Await-Name $window '已暂停'
    Invoke-Button $window 'Return control'
    $null = Await-Name $window '自动化可用'
    $null = Await-Name $window '已暂停'
    Invoke-Button $window 'Resume instance'
    $null = Await-Name $window '运行中'
    Check-Controls $window $false
    Invoke-Button $window 'Pause instance'
    $null = Await-Name $window '已暂停'
    Invoke-Button $window 'Next instance'
    $null = Await-Name $window 'Lifecycle B'
    $null = Await-Name $window '运行中'
    Invoke-Button $window 'Stop instance'
    $null = Await-Name $window 'Lifecycle A'
    $null = Await-Name $window '已暂停' # Stopping sibling B did not resume A.
    Check-Controls $window $true
    $script:lastAction = 'hot reload'
    [System.IO.File]::WriteAllText($ui, $schema.Replace('Lifecycle before reload', 'Lifecycle after reload'))
    $null = Await-Name $window 'Lifecycle after reload'
    $null = Await-Name $window '已暂停'
    $null = Await-Name $window 'Lifecycle A'
    Check-Controls $window $true # UI-only reload keeps the original paused run.
    Invoke-Button $window 'Stop instance'
    $null = Await-Name $window '无运行实例'
    foreach ($name in 'Pause instance','Resume instance','Stop instance') {
        if ((Await-Name $window $name ([System.Windows.Automation.ControlType]::Button)).Current.IsEnabled) { throw "Terminal control enabled: $name" }
    }
    Write-Output 'lifecycle-ui-ok sleepPause=confirmed instances=isolated resume=explicit refresh=current hotReload=preserved-paused-run terminalControls=disabled'
    Write-Output 'NOT RUN: real-human pause/takeover and uncancellable native pending labels; deterministic barrier tests cover those backend boundaries.'
}
finally {
    $env:RUG_PLUGINS_ROOT = $previous
    if ($null -ne $app) {
        $app.Refresh()
        if (-not $app.HasExited) {
            $null = $app.CloseMainWindow()
            if (-not $app.WaitForExit(5000)) { Stop-Process -Id $app.Id -ErrorAction SilentlyContinue }
        }
    }
    $full = [System.IO.Path]::GetFullPath($root)
    $allowed = [System.IO.Path]::GetFullPath($testOutput).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if (-not $full.StartsWith($allowed + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not ([System.IO.Path]::GetFileName($full)).StartsWith('lifecycle-ui-', [System.StringComparison]::Ordinal)) {
        throw 'Refusing unexpected cleanup path.'
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
