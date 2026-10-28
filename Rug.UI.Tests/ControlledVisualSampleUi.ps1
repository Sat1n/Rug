param([int]$AppProcessId,[string]$TraceId)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName UIAutomationClient
function Find($name,$id=$false){
 $property=if($id){[System.Windows.Automation.AutomationElement]::AutomationIdProperty}else{[System.Windows.Automation.AutomationElement]::NameProperty}
 foreach($root in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$AppProcessId))){
  $item=$root.FindFirst([System.Windows.Automation.TreeScope]::Subtree,[System.Windows.Automation.PropertyCondition]::new($property,$name));if($item){return $item}
 };return $null
}
function WaitFor($action){$watch=[Diagnostics.Stopwatch]::StartNew();while($watch.Elapsed.TotalSeconds -lt 20){$result=& $action;if($result){return $result};Start-Sleep -Milliseconds 40};throw 'Sample viewer wait timed out'}
$open=WaitFor {Find 'Open trace viewer'}
Write-Output 'VIEWER stage: host ready'
$open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$null=WaitFor {$status=Find 'Trace integrity' $true;if($status -and $status.Current.Name -match '请选择'){return $status}}
Write-Output 'VIEWER stage: catalog ready'
$picker=Find 'Trace selection'
$item=$null
for($page=0;$page -lt 20;$page++){
 $item=$picker.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)) | Where-Object {$_.Current.Name -match '^文件.*controlled_visual' -and $_.Current.Name -match $TraceId} | Select-Object -First 1
 if($item){break}
 $scroll=$picker.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
 if(-not $scroll.Current.VerticallyScrollable -or $scroll.Current.VerticalScrollPercent -ge 100){break}
 $previous=$scroll.Current.VerticalScrollPercent
 $scroll.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount,[System.Windows.Automation.ScrollAmount]::LargeIncrement)
 $null=WaitFor {if($scroll.Current.VerticalScrollPercent -ne $previous){return $true}}
}
if(-not $item){throw 'No controlled sample historical trace'}
Write-Output 'VIEWER stage: stable trace selected'
$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$null=WaitFor {$status=Find 'Trace integrity' $true;if($status -and $status.Current.Name -match '^Valid'){return $status}}
Write-Output 'VIEWER stage: persisted page Valid'
if(-not (Find '无运行实例')){throw 'Read-only viewer resurrected an instance'}
Write-Output ('CONTROLLED VIEWER PASS: read-only historical trace '+$item.Current.Name)
