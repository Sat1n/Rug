param([int]$AppProcessId,[long]$TargetHwnd,[ValidateSet('Baseline','Live','History')][string]$Phase,[string]$IncidentRoot)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class TraceReviewUiNative {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L,T,R,B; }
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpi);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int w,int h,uint flags);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
 public static void Move(int x,int y) {
  int l=GetSystemMetrics(76),t=GetSystemMetrics(77),w=GetSystemMetrics(78),h=GetSystemMetrics(79);
  mouse_event(0xC001,(uint)((long)(x-l)*65535/(w-1)),(uint)((long)(y-t)*65535/(h-1)),0,(UIntPtr)0x52554749);
 }
}
'@
function Roots {
 [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$AppProcessId))
}
function Find($name,$id=$false) {
 $property=if($id){[System.Windows.Automation.AutomationElement]::AutomationIdProperty}else{[System.Windows.Automation.AutomationElement]::NameProperty}
 foreach($root in (Roots)) {
  $e=$root.FindFirst([System.Windows.Automation.TreeScope]::Subtree,[System.Windows.Automation.PropertyCondition]::new($property,$name))
  if($null -ne $e){return $e}
 };return $null
}
function WaitFor($action,$description) {
 $watch=[Diagnostics.Stopwatch]::StartNew()
 while($watch.Elapsed.TotalSeconds -lt 15){$value=& $action;if($value){return $value};Start-Sleep -Milliseconds 40}
 throw "Trace UI timeout: $description"
}
function Await($name){WaitFor {Find $name} $name}
function Invoke($name){$e=Await $name;$null=WaitFor {$e.Current.IsEnabled} "enabled $name";$e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
function SelectItem($name){(Await $name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()}
function Pick($name,$pattern) {
 $picker=Await $name
 $item=WaitFor {
  foreach($e in $picker.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))) {
   if($e.Current.Name -match $pattern){return $e}
  };return $null
 } "choice $pattern"
 $selected=$item.Current.Name;$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 return $selected
}
function Status($pattern){
 try { WaitFor {$e=Find 'Trace integrity' $true;if($e -and $e.Current.Name -match $pattern){return $e.Current.Name};return $null} "integrity $pattern" }
 catch {
  $e=Find 'Trace integrity' $true;$picker=Find 'Trace selection'
  $chosen=if($picker){($picker.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection() | ForEach-Object {$_.Current.Name}) -join '|'}else{'none'}
  $names=@(foreach($root in (Roots)){"ROOT:"+$root.Current.Name;foreach($item in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)) {if($item.Current.Name){$item.Current.Name}}}) | Select-Object -First 70
  throw "Trace status expected $pattern; actual=$($e.Current.Name); selection=$chosen; names=$($names -join '|')"
 }
}
function CloseViewer {
 foreach($root in (Roots)){if($root.Current.Name -like 'Rug · 轨迹*'){$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close();return}}
}
$app=Get-Process -Id $AppProcessId
$null=Await 'Open trace viewer'
if($Phase -eq 'Baseline'){Write-Output 'trace-review-baseline-no-viewer';exit 0}
if($Phase -eq 'Live') {
 $null=Await '监控中 · Trace review probe'
 SelectItem '开发工具'
 $null=[TraceReviewUiNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
 $screen=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
 $null=[TraceReviewUiNative]::SetWindowPos([IntPtr]$TargetHwnd,[IntPtr](-1),$screen.Right-620,$screen.Top+80,600,400,0x10)
 $cross=Await 'Window crosshair';$null=[TraceReviewUiNative]::SetForegroundWindow($app.MainWindowHandle)
 $r=$cross.Current.BoundingRectangle
 [TraceReviewUiNative]::Move([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2));[TraceReviewUiNative]::mouse_event(2,0,0,0,[UIntPtr]0x52554749)
 Start-Sleep -Milliseconds 100
 $rect=[TraceReviewUiNative+Rect]::new();if(-not [TraceReviewUiNative]::GetWindowRect([IntPtr]$TargetHwnd,[ref]$rect)){throw 'Missing fixture target'}
 [TraceReviewUiNative]::Move($rect.L+100,$rect.T+100);Start-Sleep -Milliseconds 100
 [TraceReviewUiNative]::mouse_event(4,0,0,0,[UIntPtr]0x52554749)
 $null=Await ('0x'+$TargetHwnd.ToString('X'));SelectItem 'Trace review probe'
 Invoke 'Start bound task';Invoke 'Allow permissions'
 $null=WaitFor {Get-ChildItem -LiteralPath $IncidentRoot -Filter '*.json' -ErrorAction SilentlyContinue | Select-Object -First 1} 'actual incident PNG/JSON'
}
Invoke 'Open trace viewer'
$null=Status '请选择'
if($Phase -eq 'Live') {
 $null=Pick 'Trace selection' '^当前保留窗口.*control'
 $null=Status '当前保留窗口'
 $selected=Pick 'Trace selection' '^当前保留窗口.*trace_review_probe'
 $null=Status '当前保留窗口'
 $null=WaitFor { $list=Await 'Trace event list';$list.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)) } 'live events'
 $null=Pick 'Trace event list' 'Authorized'
 $eventDetail=(Await 'Trace incident detail').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
 if($eventDetail -notmatch 'Authorization' -or $eventDetail -notmatch 'Revision'){throw 'Actual event authorization/revision missing'}
 Invoke 'Stop instance';$null=Await '无运行实例'
 Invoke 'Refresh traces';$null=Status '请选择'
 $null=Pick 'Trace selection' '^文件.*trace_review_probe'
 $null=Status 'Incomplete'
} else {
 $null=Await '无运行实例'
 $null=Pick 'Trace selection' '^文件.*trace_review_probe'
 $null=Status 'Valid'
}
Invoke 'Refresh incidents'
$null=WaitFor {$e=Find 'Incident status' $true;if($e -and $e.Current.Name -match '本页异常 [1-9]'){return $e}} 'incident catalog'
$null=Pick 'Incident selection' '^Valid.*trace_review_probe'
$null=WaitFor {$e=Find 'Incident status' $true;if($e -and $e.Current.Name -match 'AssociatedInMemory'){return $e}} 'versioned incident/link'
$detail=(Await 'Trace incident detail').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
if($detail -notmatch 'independent-anomaly-capture' -or $detail -notmatch 'incidentId='){throw 'Incident detail missing versioned PNG boundary'}
Invoke 'Locate incident trace';$located=Status '异常关联上界'
if($located -match 'NotFound'){throw 'Incident linked trace not found'}
CloseViewer
for($i=0;$i -lt 2;$i++){Invoke 'Open trace viewer';$null=Status '请选择';CloseViewer}
if($Phase -eq 'History'){$null=Await '无运行实例'}
Write-Output "trace-review-ui-ok $Phase live/history/control catalog, versioned real incident, linked boundary, bounded reopen; read-only history does not start task or grant permissions"
