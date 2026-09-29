$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$testOutput = (Resolve-Path 'Rug.UI.Tests\bin\x64\Debug\net10.0').Path
$root = Join-Path $testOutput ('hot-ui-smoke-' + [Guid]::NewGuid().ToString('N'))
$directory = Join-Path $root 'plugin'
$previousRootEnv = $env:RUG_PLUGINS_ROOT
$appProcess = $null
try {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $directory 'manifest.json'),
        '{"id":"codex-hot-ui-smoke","name":"Smoke","version":"1","entry":"main.lua","targetProcess":"example","permissions":[]}')
    [System.IO.File]::WriteAllText((Join-Path $directory 'main.lua'), 'function on_tick() end')
    [System.IO.File]::WriteAllText((Join-Path $directory 'ui.json'),
        '{"type":"Page","id":"root","title":"BeforeReload"}')
    $env:RUG_PLUGINS_ROOT = $root
    $appPath = (Resolve-Path 'Rug.UI\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Rug.UI.exe').Path
    $appProcess = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    $appProcess.Refresh()
    if ($appProcess.HasExited) { throw "App exited: $($appProcess.ExitCode)" }
    $pidCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $appProcess.Id)
    $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $pidCondition)
    if ($null -eq $window) { throw 'UI Automation could not find the app window.' }
    $before = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'BeforeReload')
    if ($null -eq $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $before)) {
        throw 'Initial schema title was not rendered.'
    }
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    [System.IO.File]::WriteAllText((Join-Path $directory 'ui.json'),
        '{"type":"Page","id":"root","title":"AfterReload"}')
    $after = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'AfterReload')
    $found = $null
    while ($watch.Elapsed -lt [TimeSpan]::FromSeconds(3)) {
        $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $after)
        if ($null -ne $found) { break }
        Start-Sleep -Milliseconds 50
    }
    if ($null -eq $found) { throw 'Updated schema title was not rendered.' }
    Write-Output "ui-hot-reload-ok elapsedMs=$($watch.ElapsedMilliseconds)"

    [System.IO.File]::WriteAllText((Join-Path $directory 'ui.json'), '{ invalid JSON')
    $errorTitle = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, '热重载失败')
    $watch.Restart()
    $errorElement = $null
    while ($watch.Elapsed -lt [TimeSpan]::FromSeconds(3)) {
        $errorElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $errorTitle)
        if ($null -ne $errorElement) { break }
        Start-Sleep -Milliseconds 50
    }
    if ($null -eq $errorElement) { throw 'Invalid JSON did not show the reload error banner.' }
    [System.IO.File]::WriteAllText((Join-Path $directory 'ui.json'),
        '{"type":"Page","id":"root","title":"RecoveredReload"}')
    $recovered = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'RecoveredReload')
    $watch.Restart()
    $recoveredElement = $null
    while ($watch.Elapsed -lt [TimeSpan]::FromSeconds(3)) {
        $recoveredElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $recovered)
        if ($null -ne $recoveredElement) { break }
        Start-Sleep -Milliseconds 50
    }
    if ($null -eq $recoveredElement) { throw 'Corrected JSON did not recover the rendered UI.' }
    Write-Output "ui-error-recovery-ok elapsedMs=$($watch.ElapsedMilliseconds)"

    $successTitle = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, '自动更新完成')
    $watch.Restart()
    while ($watch.Elapsed -lt [TimeSpan]::FromSeconds(3) -and
        $null -eq $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $successTitle)) {
        Start-Sleep -Milliseconds 50
    }
    [System.IO.File]::WriteAllText((Join-Path $directory 'main.lua'), 'function broken(')
    $watch.Restart()
    $luaError = $null
    while ($watch.Elapsed -lt [TimeSpan]::FromSeconds(3)) {
        $luaError = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $errorTitle)
        if ($null -ne $luaError) { break }
        Start-Sleep -Milliseconds 50
    }
    if ($null -eq $luaError) { throw 'Invalid Lua did not show the reload error banner.' }
    [System.IO.File]::WriteAllText((Join-Path $directory 'main.lua'), 'function on_tick() end')
    $watch.Restart()
    $luaRecovered = $null
    while ($watch.Elapsed -lt [TimeSpan]::FromSeconds(3)) {
        $luaRecovered = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $successTitle)
        if ($null -ne $luaRecovered) { break }
        Start-Sleep -Milliseconds 50
    }
    if ($null -eq $luaRecovered) { throw 'Corrected Lua did not show the success banner.' }
    Write-Output "lua-error-recovery-ok elapsedMs=$($watch.ElapsedMilliseconds)"
}
finally {
    $env:RUG_PLUGINS_ROOT = $previousRootEnv
    if ($null -ne $appProcess) {
        $appProcess.Refresh()
        if (-not $appProcess.HasExited) {
            $null = $appProcess.CloseMainWindow()
            if (-not $appProcess.WaitForExit(3000)) {
                if (Get-Process -Id $appProcess.Id -ErrorAction SilentlyContinue) {
                    Stop-Process -Id $appProcess.Id -ErrorAction SilentlyContinue
                }
            }
        }
    }
    $full = [System.IO.Path]::GetFullPath($root)
    $allowed = [System.IO.Path]::GetFullPath($testOutput).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if (-not $full.StartsWith($allowed + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase) -or
        -not ([System.IO.Path]::GetFileName($full)).StartsWith('hot-ui-smoke-',
        [System.StringComparison]::Ordinal)) {
        throw 'Refusing to delete a path outside the dedicated plugin test directory.'
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
