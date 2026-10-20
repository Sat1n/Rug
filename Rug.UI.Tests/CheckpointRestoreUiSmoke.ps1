param([int]$AppProcessId, [long]$TargetHwnd, [ValidateSet("Save","Restore","Update","Switch","SwitchPause","SwitchReject")][string]$Phase, [string]$PluginDirectory, [string]$TargetTemplate)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RecoveryUiNative {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L,T,R,B; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x,int y,int w,int h,uint flags);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
 public static void MoveSoftware(int x,int y) {
  int left=GetSystemMetrics(76), top=GetSystemMetrics(77), width=GetSystemMetrics(78), height=GetSystemMetrics(79);
  mouse_event(0xC001,(uint)((long)(x-left)*65535/(width-1)),(uint)((long)(y-top)*65535/(height-1)),0,(UIntPtr)0x52554749);
 }
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder text, int count);
 [DllImport("user32.dll",SetLastError=true)] public static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, UIntPtr w, IntPtr l);
}
'@
function Find($name) {
 $roots=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$AppProcessId))
 foreach($root in $roots) {
  $found=$root.FindFirst([System.Windows.Automation.TreeScope]::Subtree,
   [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
  if($null -ne $found){return $found}
 }
 return $null
}
function Await($name) {
 $watch=[Diagnostics.Stopwatch]::StartNew()
 while($watch.Elapsed.TotalSeconds -lt 12) { $e=Find $name; if($null -ne $e){return $e}; Start-Sleep -Milliseconds 25 }
 $names=@($script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -Unique)
 throw "Recovery UI missing $name; names=$($names -join ' | ')"
}
function Invoke($name) {
 $e=Await $name
 $watch=[Diagnostics.Stopwatch]::StartNew()
 while(-not $e.Current.IsEnabled -and $watch.Elapsed.TotalSeconds -lt 5){Start-Sleep -Milliseconds 25}
 if(-not $e.Current.IsEnabled){throw "Recovery UI disabled $name"}
 $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Select-Item($name) { (Await $name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
try {
 $app=Get-Process -Id $AppProcessId
 $script:window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
 $pluginName=if($Phase -eq 'Update' -or $Phase.StartsWith('Switch')){'Update freeze probe'}else{'Checkpoint controlled panel'}
 $null=Await ('监控中 · '+$pluginName) # Let initial asynchronous plugin load finish before changing navigation.
 Select-Item '开发工具'
 $null=[RecoveryUiNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
 $screen=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
 $null=[RecoveryUiNative]::SetWindowPos([IntPtr]$TargetHwnd,[IntPtr](-1),$screen.Right-620,$screen.Top+80,600,400,0x10)
 $crosshair=Await 'Window crosshair'
 # Setup mouse actions are software-tagged and occur before any input run/session. This is UI automation, never human acceptance.
 $null=[RecoveryUiNative]::SetForegroundWindow($app.MainWindowHandle)
 $rect=$crosshair.Current.BoundingRectangle
 Write-Output "binding setup crosshair=$rect foreground=$([RecoveryUiNative]::GetForegroundWindow()) ui=$($app.MainWindowHandle)"
 [RecoveryUiNative]::MoveSoftware([int]($rect.X+$rect.Width/2),[int]($rect.Y+$rect.Height/2))
 [RecoveryUiNative]::mouse_event(2,0,0,0,[UIntPtr]0x52554749)
 Start-Sleep -Milliseconds 100
 $targetRect=[RecoveryUiNative+Rect]::new()
 if(-not [RecoveryUiNative]::GetWindowRect([IntPtr]$TargetHwnd,[ref]$targetRect)){throw 'Target unavailable'}
 [RecoveryUiNative]::MoveSoftware($targetRect.L+100,$targetRect.T+100)
 Start-Sleep -Milliseconds 100
 [RecoveryUiNative]::mouse_event(4,0,0,0,[UIntPtr]0x52554749)
 $null=Await '已绑定，见上方窗口信息卡片'
 $hex='0x'+$TargetHwnd.ToString('X')
 $null=Await $hex # Prove the actual selected HWND, not merely successful selection of some window.
 Select-Item $pluginName
 function AwaitText($pattern) {
  $watch=[Diagnostics.Stopwatch]::StartNew()
  while($watch.Elapsed.TotalSeconds -lt 15) {
   $all=$script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
   foreach($element in $all){if($element.Current.Name -match $pattern){return $element.Current.Name}}
   Start-Sleep -Milliseconds 40
  }
  $names=@($script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -Unique)
  throw "Checkpoint UI missing pattern: $pattern; names=$($names -join ' | ')"
 }
 function CheckChoice($pattern) {
  $watch=[Diagnostics.Stopwatch]::StartNew()
  while($watch.Elapsed.TotalSeconds -lt 12) {
   $picker=Await 'Checkpoint selection'
   $selected=$picker.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
   foreach($item in $selected) {
    if($item.Current.Name -match $pattern){
     $picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
     Write-Output "checkpoint selected: $($item.Current.Name)"
     return
    }
   }
   Start-Sleep -Milliseconds 50
  }
  throw "Checkpoint dropdown missing: $pattern"
 }
 if($Phase -eq 'Update' -or $Phase.StartsWith('Switch')) {
  Invoke 'Start bound task'
  Invoke 'Allow permissions'
  $null=Await '运行中'
  function CurrentRun {
   $conditions=[System.Windows.Automation.AndCondition]::new(
    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'当前实例'),
    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ComboBox))
   $picker=$script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$conditions)
   return $picker.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name
  }
  $identity=CurrentRun
  if($Phase.StartsWith('Switch')) {
   # Fixed shipped source/target bytes; no production auto-approval or arbitrary compatibility mapping.
   Copy-Item -LiteralPath (Join-Path $TargetTemplate 'main.lua') -Destination (Join-Path $PluginDirectory 'main.lua')
   Copy-Item -LiteralPath (Join-Path $TargetTemplate 'manifest.json') -Destination (Join-Path $PluginDirectory 'manifest.json')
   $null=AwaitText '更新待应用'
   Invoke 'Prepare runtime update'
   $null=AwaitText '^已冻结，尚未切换 · 1/1'
   if($Phase -eq 'SwitchReject') {
    $null=[RecoveryUiNative]::SendMessageW([IntPtr]$TargetHwnd,0x8001,[UIntPtr]0,[IntPtr]::Zero)
   }
   if($Phase -eq 'SwitchPause') {
    Invoke 'Pause instance'
    $null=Await '已暂停'
   }
   Invoke 'Switch runtime update'
   Invoke '确认切换'
   if($Phase -eq 'SwitchReject') {
    $null=AwaitText '^更新准备失败.*SceneRejected'
    $null=[RecoveryUiNative]::SendMessageW([IntPtr]$TargetHwnd,0x8001,[UIntPtr]1,[IntPtr]::Zero)
    Invoke 'Stop instance'
    $null=Await '无运行实例'
    Write-Output 'runtime-switch-reject-ui-ok fresh red scene rejected; source not stopped by update; no replacement business'
    return
   }
   if($Phase -eq 'SwitchPause') {
    $null=AwaitText '^切换已提交，等待恢复钩子完成'
    $null=Await '已暂停'
    Invoke 'Resume instance'
   }
   $completed=AwaitText '^新实例恢复完成'
   if($completed -notmatch '→[a-f0-9]{8} g[2-9]'){throw 'Missing new identity/generation in switch summary'}
   $null=Await '运行中'
   Invoke 'Stop instance'
   $null=Await '无运行实例'
   Write-Output "runtime-switch-ui-ok $Phase explicit confirmation/new generation/fresh WGC/AFTER-only; $completed"
   return
  }
  # Only captured runtime code changes: the currently loaded trusted Lua remains unchanged.
  [System.IO.File]::AppendAllText((Join-Path $PluginDirectory 'main.lua'),"`n-- pending runtime candidate")
  $null=AwaitText '更新待应用'
  Invoke 'Prepare runtime update'
  $frozen=AwaitText '^已冻结，尚未切换 · 1/1'
  if($frozen -notmatch 'panel-ready#(\d+)'){throw 'Missing committed checkpoint sequence'}
  $cycle=[int]$Matches[1]
  Invoke 'Cancel runtime update'
  $null=AwaitText '^更新准备已取消'
  $selected=CurrentRun
  if($selected -ne $identity){throw 'Cancel changed run generation'}
  Invoke 'Prepare runtime update'
  $null=AwaitText '^已冻结，尚未切换 · 1/1'
  Invoke 'Stop instance'
  $null=Await '无运行实例'
  $null=AwaitText '^更新准备失败'
  Write-Output "runtime-update-ui-ok prepare/fresh-WGC/freeze/cancel/same-run/stop; retained=$identity; cancelledCheckpointCycle=$cycle"
  return
 }
 if($Phase -eq 'Save') {
  Invoke 'Start bound task'
  Invoke 'Allow permissions'
  $null=Await '运行中'
  Invoke 'Refresh checkpoints'
  CheckChoice 'SourceStillActive'
  Invoke 'Stop instance'
  $null=Await '无运行实例'
 } else {
  $null=[RecoveryUiNative]::SendMessageW([IntPtr]$TargetHwnd,0x8001,[UIntPtr]0,[IntPtr]::Zero)
  Invoke 'Refresh checkpoints'
  CheckChoice 'Ready'
  # The real host consent await is a controlled preparation barrier, released only by Deny below.
  $picker=Await 'Checkpoint selection'
  # The owning fixture asserts two distinct records before launching this host.
  # Keep the collapsed peer: expanding it before the request can invalidate WinUI's UIA root.
  $selected=$picker.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name
  Invoke 'Restore checkpoint'
  $null=Await 'Allow permissions'
  $picker=Await 'Checkpoint selection'
  if($picker.Current.IsEnabled){throw 'Checkpoint selector remained enabled during suspended preparation'}
  $selectionBlocked=$false
  try { $picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() }
  catch { if($_.Exception.GetBaseException() -is [System.Windows.Automation.ElementNotEnabledException]){$selectionBlocked=$true}else{throw} }
  if(-not $selectionBlocked){throw 'Disabled checkpoint selector accepted a different checkpoint'}
  if($picker.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name -ne $selected){throw 'Preparation changed checkpoint selection'}
  Invoke 'Deny permissions'
  $null=AwaitText '恢复准备已取消'
  if(-not (Await 'Checkpoint selection').Current.IsEnabled){throw 'Checkpoint selector not restored after preparation cancellation'}
  $null=Await '无运行实例'
  Write-Output 'checkpoint-selection-lock-ok: two choices, controlled consent await, switch rejected, cancellation re-enables selection; no validator/input dispatched'
  Invoke 'Restore checkpoint'
  Invoke 'Allow permissions'
  $null=AwaitText '恢复未启动.*SceneRejected'
  $null=[RecoveryUiNative]::SendMessageW([IntPtr]$TargetHwnd,0x8001,[UIntPtr]1,[IntPtr]::Zero)
  Invoke 'Restore checkpoint'
  $null=AwaitText '已从检查点启动新实例'
  $null=Await '运行中'
  Invoke 'Stop instance'
  $null=Await '无运行实例'
 }
 Write-Output "checkpoint-ui-ok phase=$Phase consent=true freshWgc=true restart=rebound; NOT human acceptance"
} catch { Write-Error $_; exit 1 }
