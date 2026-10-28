using Rug.UI.Core.Models;

namespace Rug.UI.Helpers;

/// <summary>Lifecycle status wins over a retained recovery phase in a selected-run snapshot.</summary>
internal static class LifecyclePresentation
{
    internal static string Caption(LifecycleSnapshot? snapshot) => snapshot switch
    {
        { Status: AutomationTaskStatus.Stopping } => "正在停止",
        { Status: AutomationTaskStatus.Faulted } => "运行故障",
        { Status: AutomationTaskStatus.Stopped } => "已停止",
        { Status: AutomationTaskStatus.Recovering, Recovery.Phase: RecoveryPhase.Validating } => "正在验证",
        { Status: AutomationTaskStatus.Recovering } => "恢复处理中",
        { Status: AutomationTaskStatus.AwaitingHuman } => "等待人工",
        { Status: AutomationTaskStatus.Paused, ResumeRequested: true } => "正在恢复",
        { Status: AutomationTaskStatus.Paused } => "已暂停",
        { PauseRequested: true } => "正在暂停",
        { Status: AutomationTaskStatus.Starting } => "正在启动",
        { Status: AutomationTaskStatus.Running } => "运行中",
        _ => "无运行实例"
    };
}
