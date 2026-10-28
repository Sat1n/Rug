$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$testOutput = (Resolve-Path 'Rug.UI.Tests/bin/x64/Debug/net10.0').Path
$root = Join-Path $testOutput ('phase3-e2e-' + [Guid]::NewGuid().ToString('N'))
$plugin = Join-Path $root 'demo_arknights'
$app = $null
$previous = $env:RUG_PLUGINS_ROOT
function Find-Name($window, [string]$name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Await-Name($window, [string]$name, [int]$seconds = 5) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $seconds) {
        $found = Find-Name $window $name
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 50
    }
    $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $names = @($all | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -Unique)
    throw "UI element was not found: $name; names=$($names -join ' | ')"
}
try {
    New-Item -ItemType Directory -Path $plugin -Force | Out-Null
    foreach ($file in @('manifest.json', 'ui.json', 'main.lua')) {
        Copy-Item -LiteralPath (Join-Path 'plugins/demo_arknights' $file) -Destination (Join-Path $plugin $file)
    }
    $env:RUG_PLUGINS_ROOT = $root
    $appPath = (Resolve-Path 'Rug.UI/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/Rug.UI.exe').Path
    $app = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2
    $app.Refresh()
    if ($app.HasExited) { throw "App exited: $($app.ExitCode)" }
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
    $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $condition)
    if ($null -eq $window) { throw 'Main window was not found.' }
    $null = Await-Name $window '明日方舟 · MVP 演示'
    $null = Await-Name $window '明日方舟自动化演示'
    $checkbox = Await-Name $window '模拟刷图'
    $toggle = $checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
    $button = Await-Name $window '开始执行'
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $allow = Await-Name $window 'Allow permissions'
    $allow.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Await-Name $window '刷图步骤完成（模拟）' 8
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $ui = Join-Path $plugin 'ui.json'
    $source = [System.IO.File]::ReadAllText($ui)
    [System.IO.File]::WriteAllText($ui, $source.Replace('明日方舟自动化演示', '热重载后的明日方舟演示'))
    $null = Await-Name $window '热重载后的明日方舟演示' 3
    if ($watch.ElapsedMilliseconds -gt 1000) { throw "Hot reload exceeded 1s: $($watch.ElapsedMilliseconds)ms" }
    $checkbox = Await-Name $window '模拟刷图'
    $toggle = $checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        throw 'Selected task state was lost after hot reload.'
    }
    $null = Await-Name $window '刷图步骤完成（模拟）'
    Write-Output "phase3-e2e-ok reloadMs=$($watch.ElapsedMilliseconds)"
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
        -not ([System.IO.Path]::GetFileName($full)).StartsWith('phase3-e2e-',
        [System.StringComparison]::Ordinal)) {
        throw 'Refusing to delete an unexpected test directory.'
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
