param([ValidateSet('Debug','Release')][string]$Configuration='Debug',[ValidateRange(1,10)][int]$Count=1,[switch]$Viewer,[switch]$RepeatClose,[string]$OutputRoot)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
$repo=Split-Path $PSScriptRoot -Parent
if(-not $OutputRoot){$OutputRoot=Join-Path $repo ('artifacts/host-exit/'+$Configuration+'-'+[guid]::NewGuid().ToString('N'))}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
$null=New-Item -ItemType Directory -Force $OutputRoot
$results=@()
for($round=1;$round -le $Count;$round++){
 $area=Join-Path $OutputRoot "round-$round";$null=New-Item -ItemType Directory -Force "$area/plugins"
 $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $repo "Rug.UI/bin/x64/$Configuration/net10.0-windows10.0.22621.0/win-x64/Rug.UI.exe"))
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($entry in @(@('RUG_PLUGINS_ROOT','plugins'),@('RUG_TRACE_ROOT','traces'),@('RUG_ANOMALY_ROOT','incidents'),@('RUG_CHECKPOINT_ROOT','checkpoints'))){$start.Environment[$entry[0]]=Join-Path $area $entry[1]}
 $app=[Diagnostics.Process]::Start($start);$stdout=$app.StandardOutput.ReadToEndAsync();$stderr=$app.StandardError.ReadToEndAsync()
 $phases=@('started');$forced=$false;$failure=$null;$modules=@();$main=[IntPtr]::Zero
 try {
  $watch=[Diagnostics.Stopwatch]::StartNew()
  while($true){
   $app.Refresh();if($app.HasExited){throw 'Exited before ready'}
   if($app.MainWindowHandle -ne [IntPtr]::Zero){
    $window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    $ready=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Open trace viewer'))
    if($ready){$main=$app.MainWindowHandle;break}
   }
   if($watch.Elapsed.TotalSeconds -gt 30){throw 'Startup timeout'};Start-Sleep -Milliseconds 40
  }
  $phases+='ready-empty-host'
  $modules=@($app.Modules | Where-Object {$_.ModuleName -match 'coreclr|clrjit|hostpolicy|WindowsAppRuntime|Microsoft.UI.Xaml|WinUIEx|AppNotifications|Rug.UI|WinRT.Runtime|CommunityToolkit'} | ForEach-Object {@{name=$_.ModuleName;path=$_.FileName;fileVersion=$_.FileVersionInfo.FileVersion;productVersion=$_.FileVersionInfo.ProductVersion}})
  if($Viewer){
   $ready.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();$viewerWatch=[Diagnostics.Stopwatch]::StartNew();$viewerWindow=$null
   while(-not $viewerWindow){
    foreach($candidate in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id))){if($candidate.Current.Name -like 'Rug · *'){$viewerWindow=$candidate;break}}
    if($viewerWatch.Elapsed.TotalSeconds -gt 15){throw 'Viewer startup timeout'};if(-not $viewerWindow){Start-Sleep -Milliseconds 30}
   }
   $viewerWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close();$phases+='viewer-opened-and-closed'
  }
  $null=$app.CloseMainWindow();if($RepeatClose -and -not $app.HasExited){$null=$app.CloseMainWindow()};$phases+='close-requested'
  if(-not $app.WaitForExit(30000)){throw 'Natural close timeout'}
  $phases+='process-exited'
 } catch {$failure=$_.ToString();if(-not $app.HasExited){$forced=$true;$app.Kill($true);$app.WaitForExit()}}
 $out=$stdout.GetAwaiter().GetResult();$err=$stderr.GetAwaiter().GetResult()
 [IO.File]::WriteAllText("$area/stdout.txt",$out);[IO.File]::WriteAllText("$area/stderr.txt",$err)
 $result=@{configuration=$Configuration;round=$round;viewer=[bool]$Viewer;repeatClose=[bool]$RepeatClose;pid=$app.Id;phases=$phases;modules=$modules;exitCode=$app.ExitCode;forced=$forced;failure=$failure;assertion=$err.Contains('runtime thread state has been destroyed');passed=(!$forced -and !$failure -and $app.ExitCode -eq 0 -and !$err.Contains('runtime thread state has been destroyed'))}
 $results+=,$result;Write-Output "$Configuration round=$round exit=$($app.ExitCode) forced=$forced assertion=$($result.assertion)"
 $app.Dispose()
}
$results | ConvertTo-Json -Depth 12 | Set-Content "$OutputRoot/report.json" -Encoding utf8
Write-Output "EXIT REPORT $OutputRoot/report.json"
if(@($results | Where-Object {-not $_.passed}).Count){exit 1}
