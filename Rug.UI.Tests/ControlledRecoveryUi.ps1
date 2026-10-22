param([int]$AppProcessId, [long]$TargetHwnd, [string]$TraceRoot,[ValidateSet('None','Preview','Active','Awaiting')][string]$ExitPhase='None')
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
 return $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
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

function WaitFor($action){$watch=[Diagnostics.Stopwatch]::StartNew();while($watch.Elapsed.TotalSeconds -lt 18){$value=& $action;if($value){return $value};Start-Sleep -Milliseconds 40};throw 'Controlled recovery UI wait timed out'}
try {
 $app=Get-Process -Id $AppProcessId
 $script:window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
 $null=Await '监控中 · Controlled visual recovery'
 Select-Item '开发工具'
 $null=[RecoveryUiNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
 $screen=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
 $null=[RecoveryUiNative]::SetWindowPos([IntPtr]$TargetHwnd,[IntPtr](-1),$screen.Right-620,$screen.Top+80,600,400,0x10)
 $crosshair=Await 'Window crosshair'
 $null=[RecoveryUiNative]::SetForegroundWindow($app.MainWindowHandle)
 $rect=$crosshair.Current.BoundingRectangle
 # Software-tagged picker setup BEFORE an input session; never human acceptance.
 [RecoveryUiNative]::MoveSoftware([int]($rect.X+$rect.Width/2),[int]($rect.Y+$rect.Height/2))
 [RecoveryUiNative]::mouse_event(2,0,0,0,[UIntPtr]0x52554749)
 Start-Sleep -Milliseconds 100
 $targetRect=[RecoveryUiNative+Rect]::new()
 if(-not [RecoveryUiNative]::GetWindowRect([IntPtr]$TargetHwnd,[ref]$targetRect)){throw 'Target unavailable'}
 [RecoveryUiNative]::MoveSoftware($targetRect.L+100,$targetRect.T+100)
 Start-Sleep -Milliseconds 100
 [RecoveryUiNative]::mouse_event(4,0,0,0,[UIntPtr]0x52554749)
 $null=Await ('0x'+$TargetHwnd.ToString('X'))
 if($ExitPhase -eq 'Preview'){
  Invoke '开启实时预览'
  $null=WaitFor {$elements=$script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition);$elements | Where-Object {$_.Current.Name -match '^Captured preview [1-9]'} | Select-Object -First 1}
  Write-Output 'EXIT READY: actual preview frame rendered; close while preview is running';return
 }
 Select-Item 'Controlled visual recovery'
 Invoke 'Start bound task'; Invoke 'Allow permissions'
 if($ExitPhase -eq 'Active'){
  $null=WaitFor {
   $matched=0
   foreach($file in Get-ChildItem -LiteralPath $TraceRoot -Filter '*.events' -ErrorAction SilentlyContinue){
    foreach($line in Get-Content -LiteralPath $file.FullName){try{$row=$line|ConvertFrom-Json -Depth 32}catch{continue};if($row.payload.reason -eq 'template-match:result' -and $row.payload.status -eq 'Matched'){$matched++}}
   };if($matched -ge 2){return $true}
  };$null=Await '运行中';Write-Output 'EXIT READY: active observed PostMessage run; no explicit Stop';return
 }
 $null=Await '等待人工 · controlled-visual-ready'; $null=Await '人工控制已生效'
 if($ExitPhase -eq 'Awaiting'){Write-Output 'EXIT READY: original coroutine waiting for human; no Return/Verify/Stop';return}
 Write-Output 'RECOVERY UI: BEFORE received/visually confirmed, planned handoff'
 if((Await 'Resume instance').Current.IsEnabled){throw 'Ordinary Resume bypass enabled'}
 Invoke 'Return control';$null=Await '自动化可用';$null=Await '等待人工 · controlled-visual-ready'
 Invoke 'Verify recovery'
 $null=Await '人工控制已生效';$null=Await '等待人工 · controlled-visual-ready'
 Write-Output 'RECOVERY UI: red rejected, original run remains waiting'
 $null=[RecoveryUiNative]::SendMessageW([IntPtr]$TargetHwnd,0x8005,[UIntPtr]::Zero,[IntPtr]::Zero)
 Invoke 'Return control';Invoke 'Verify recovery'
 $null=Await '验证通过，原协程继续'
 # Wait for the actual post-input new-frame template result, not merely receiver/API completion.
 $null=WaitFor {
  $matched=0
  foreach($file in Get-ChildItem -LiteralPath $TraceRoot -Filter '*.events' -ErrorAction SilentlyContinue){
   if($file.Length -gt 1048576){throw 'Trace file beyond fixture bound'}
   foreach($line in Get-Content -LiteralPath $file.FullName){
    try {$row=$line | ConvertFrom-Json -Depth 32} catch {continue}
    if($row.payload.reason -eq 'template-match:result' -and $row.payload.status -eq 'Matched'){$matched++}
   }
  }
  if($matched -ge 4){return $true}
 }
 Invoke 'Stop instance';$null=Await '无运行实例'
 Invoke 'Open trace viewer'
 # Review lives in another owned window; keep using bounded current PID roots.
 function Find($name,$id=$false){
  $property=if($id){[System.Windows.Automation.AutomationElement]::AutomationIdProperty}else{[System.Windows.Automation.AutomationElement]::NameProperty}
  foreach($root in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$AppProcessId))){
   $item=$root.FindFirst([System.Windows.Automation.TreeScope]::Subtree,[System.Windows.Automation.PropertyCondition]::new($property,$name));if($item){return $item}
  };return $null
 }
 $null=WaitFor {Find 'Incident selection'}
 Invoke 'Refresh incidents'
 $entry=WaitFor {(Find 'Incident selection').FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)) | Where-Object {$_.Current.Name -match '^Valid.*controlled_visual_recovery'} | Select-Object -First 1}
 $entry.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $null=WaitFor {$detail=Find 'Trace incident detail';if($detail -and $detail.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -match 'incidentId='){return $true}}
 Invoke 'Locate incident trace'
 $null=WaitFor {$integrity=Find 'Trace integrity' $true;if($integrity -and $integrity.Current.Name -match '异常关联上界' -and $integrity.Current.Name -notmatch 'NotFound'){return $true}}
 Write-Output 'RECOVERY UI PASS: explicit fresh WGC verification, AFTER-only, real incident -> trace. Human acceptance NOT RUN.'
} catch {Write-Error $_;exit 1}
