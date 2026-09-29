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
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
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
 {"type":"StepFlowView","id":"flow","title":"Probe steps","stateKey":"probe.timeline","steps":[]}
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
    [System.IO.File]::WriteAllText($ui, $schema.Replace('Custom timeline test', 'Reloaded timeline test'))
    $null = Await-Name $window 'Reloaded timeline test'
    Await-NotName $window 'Run B step'
    Invoke-Button $window 'Start A'
    Invoke-Button $window 'Next instance'
    $null = Await-Name $window 'Run A step'
    Invoke-Button $window 'Stop selected'
    Await-NotName $window 'Run A step'
    Write-Output 'generic-instance-ui-ok customKey=probe.timeline'
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
