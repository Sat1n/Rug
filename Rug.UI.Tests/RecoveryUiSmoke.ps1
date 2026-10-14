param([int]$AppProcessId, [long]$TargetHwnd)
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
try {
 $app=Get-Process -Id $AppProcessId
 $script:window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
 $null=Await '监控中 · recovery_probe' # Let initial asynchronous plugin load finish before changing navigation.
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
 Select-Item 'recovery_probe'
 Invoke 'Start bound task'
 Invoke 'Allow permissions'
 $null=Await '等待人工 · controlled-ready'
 $null=Await '人工控制已生效'
 if((Await 'Pause instance').Current.IsEnabled -or (Await 'Resume instance').Current.IsEnabled){throw 'Ordinary pause/resume enabled in recovery'}
 # Switch away/back and query the current recovery rather than a delayed notification.
 Select-Item 'timer_probe'
 $null=Await '无运行实例'
 Select-Item 'recovery_probe'
 $null=Await '等待人工 · controlled-ready'
 Invoke 'Return control'
 $null=Await '自动化可用'
 $null=Await '等待人工 · controlled-ready' # Returning control must not continue Lua.
 # Trusted fixture operation changes a controlled panel; it does not synthesize human input.
 $null=[RecoveryUiNative]::SendMessageW([IntPtr]$TargetHwnd,0x8001,[UIntPtr]1,[IntPtr]::Zero)
 $title=[System.Text.StringBuilder]::new(256)
 $null=[RecoveryUiNative]::GetWindowTextW([IntPtr]$TargetHwnd,$title,256)
 Write-Output "controlled target after ready: $title"
 if(-not $title.ToString().Contains("READY (green)")){throw "Controlled target did not turn green"}
 $verified=$false
 for($attempt=0;$attempt -lt 4;$attempt++) {
  Invoke 'Verify recovery'
  $watch=[Diagnostics.Stopwatch]::StartNew()
  while($watch.Elapsed.TotalSeconds -lt 8) {
   if($null -ne (Find '验证通过，原协程继续')){$verified=$true;break}
   if($null -ne (Find '仍需人工处理 · ValidationRejected')){break}
   Start-Sleep -Milliseconds 25
  }
  if($verified){break}
  $null=Await '等待人工 · controlled-ready'
  Invoke 'Return control' # An explicit new verification attempt, never automatic Lua continuation.
 }
 if(-not $verified){throw 'Fresh green validation did not succeed'}
 $null=Await '运行中'
 if((Await 'Verify recovery').Current.IsEnabled){throw 'Completed recovery button still enabled'}
 Invoke 'Stop instance'
 $null=Await '无运行实例'
 Write-Output 'recovery-ui-ok realConsent=true freshWgc=true preservedInit=true explicitVerify=true selection=current terminalControls=disabled'
 Write-Output 'NOT RUN: human planned recovery and unexpected human takeover; UIA operations are not human input evidence.'
} catch { Write-Error $_; exit 1 }
