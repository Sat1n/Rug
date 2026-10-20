using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rug.UI.Core.Models;

namespace Rug.UI;

public sealed partial class MainWindow
{
    private RuntimeUpdateSnapshot? _updateView;
    private bool _updateControlBusy;
    private bool _switchControlBusy;
    private long _updateQueryGeneration;
    private void RuntimeUpdateChanged(object? sender, RuntimeUpdateChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(async () => await RefreshRuntimeUpdateAsync());

    private async Task RefreshRuntimeUpdateAsync()
    {
        string? plugin = _activePlugin;
        long query = ++_updateQueryGeneration;
        if (plugin is null || _scheduler is null || _closingInput) return;
        try
        {
            var caller = await _scheduler.CreateRuntimeUpdateContextAsync(plugin);
            var result = await _scheduler.Gateway.ExecuteAsync(caller, new(AutomationOperation.QueryRuntimeUpdate,
                JsonSerializer.SerializeToElement(new { }), Guid.NewGuid(), "host.update.query", new(plugin), DateTimeOffset.UtcNow.AddSeconds(5)));
            if (_activePlugin != plugin || _closingInput || query != _updateQueryGeneration) return;
            var snapshot = result.Value as RuntimeUpdateSnapshot;
            if (snapshot is not null && _updateView?.Installation == snapshot.Installation && _updateView.Revision > snapshot.Revision) return;
            _updateView = snapshot;
            if (snapshot?.Stage is RuntimeUpdateStage.Committed or RuntimeUpdateStage.RestoreCompleted)
                _host?.ShowRuntimeSwitchStatus(snapshot.Stage == RuntimeUpdateStage.RestoreCompleted,
                    snapshot.Stage == RuntimeUpdateStage.Committed ? "新快照已发布；恢复钩子尚未全部完成，用户暂停仍生效。" : "新实例已通过 on_restore 继续；旧实例不会恢复或重放。" );
            bool active = snapshot?.Stage is RuntimeUpdateStage.WaitingCheckpoint or RuntimeUpdateStage.Frozen or RuntimeUpdateStage.PreparingSwitch or
                RuntimeUpdateStage.StoppingSources or RuntimeUpdateStage.ValidatingStopped or RuntimeUpdateStage.Committed;
            PrepareUpdateButton.IsEnabled = !_updateControlBusy && !_switchControlBusy && snapshot is not null && !active;
            CancelUpdateButton.IsEnabled = !_updateControlBusy && snapshot?.Stage is RuntimeUpdateStage.WaitingCheckpoint or RuntimeUpdateStage.Frozen or RuntimeUpdateStage.PreparingSwitch;
            SwitchUpdateButton.IsEnabled = !_updateControlBusy && !_switchControlBusy && snapshot?.Stage == RuntimeUpdateStage.Frozen;
            RuntimeUpdateStatus.Text = snapshot is null ? "无运行更新准备；文件保存不会冻结任务。" :
                (snapshot.Stage switch
                {
                    RuntimeUpdateStage.Pending => "运行更新待应用",
                    RuntimeUpdateStage.WaitingCheckpoint => "等待安全检查点",
                    RuntimeUpdateStage.Frozen => "已冻结，尚未切换",
                    RuntimeUpdateStage.Cancelled => "更新准备已取消，未切换代码",
                    RuntimeUpdateStage.PreparingSwitch => "正在预备新实例，旧实例仍冻结",
                    RuntimeUpdateStage.StoppingSources => "正在停止旧实例，已不可回退",
                    RuntimeUpdateStage.ValidatingStopped => "旧实例已停止，重新验证现场",
                    RuntimeUpdateStage.Committed => "切换已提交，等待恢复钩子完成",
                    RuntimeUpdateStage.RestoreCompleted => "新实例恢复完成",
                    RuntimeUpdateStage.NeedsHuman => "切换失败，需要人工处理 · " + snapshot.Error + " · " + snapshot.Reason,
                    _ => "更新准备失败 · " + snapshot.Error + " · " + snapshot.Reason
                }) + $" · {snapshot.Instances.Count(p => p.Frozen)}/{snapshot.Instances.Count} 实例已冻结" +
                (snapshot.Instances.Count == 0 ? "" : " · " + string.Join(" / ", snapshot.Instances.Select(p =>
                    p.Identity.InstanceId.ToString("N")[..8] + ":" + (p.Replacement is { } replacement ? "→"+replacement.InstanceId.ToString("N")[..8]+" g"+replacement.RunGeneration : p.Frozen ? p.Checkpoint?.NodeId + "#" + p.Checkpoint?.SubmissionSequence : !active ? "冻结已释放" : p.Checkpoint is null ? "等待节点" : "节点已保存，等待排空"))));
        }
        catch (Exception error) { if (_activePlugin == plugin) RuntimeUpdateStatus.Text = "更新准备查询失败 · " + error.Message; }
    }
    private async void PrepareUpdate_Click(object sender, RoutedEventArgs args) => await ControlUpdateAsync(false);
    private async void CancelUpdate_Click(object sender, RoutedEventArgs args) => await ControlUpdateAsync(true);
    private async void SwitchUpdate_Click(object sender, RoutedEventArgs args)
    {
        if (_switchControlBusy || _activePlugin is not { } plugin || _updateView is not { Stage: RuntimeUpdateStage.Frozen } plan) return;
        _switchControlBusy = true; SwitchUpdateButton.IsEnabled = false;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = "确认运行代码切换",
                Content = $"插件 {plugin} · {plan.Instances.Count} 个实例\n候选 {plan.CandidateVersion}\n开始停止旧实例后不能恢复原协程。新实例仅从已验证检查点继续；外部动作不可回滚。",
                PrimaryButtonText = "确认切换", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (_activePlugin != plugin || _updateView is not { } current || current.UpdateId != plan.UpdateId || current.Revision != plan.Revision)
            { RuntimeUpdateStatus.Text = "计划已变化，请重新确认"; return; }
            var caller = await _scheduler!.CreateRuntimeUpdateContextAsync(plugin);
            var result = await _scheduler.Gateway.ExecuteAsync(caller, new(AutomationOperation.SwitchRuntimeUpdate,
                JsonSerializer.SerializeToElement(new { updateId = plan.UpdateId, candidateVersion = plan.CandidateVersion, revision = plan.Revision }),
                Guid.NewGuid(), "host.update.switch", new(plugin), DateTimeOffset.UtcNow.AddSeconds(30)));
            if (result.Status != OperationStatus.Success && _activePlugin == plugin)
                RuntimeUpdateStatus.Text = "切换未完成 · " + result.Error;
        }
        catch (Exception error) { RuntimeUpdateStatus.Text = "切换失败 · " + error.Message; }
        finally { _switchControlBusy = false; await RefreshRuntimeUpdateAsync(); }
    }
    private async Task ControlUpdateAsync(bool cancel)
    {
        if (_updateControlBusy || _activePlugin is not { } plugin) return;
        var plan = _updateView;
        string? failure = null;
        _updateControlBusy = true; PrepareUpdateButton.IsEnabled = CancelUpdateButton.IsEnabled = false;
        try
        {
            var caller = await _scheduler!.CreateRuntimeUpdateContextAsync(plugin);
            var parameters = cancel ? JsonSerializer.SerializeToElement(new { updateId = plan?.UpdateId }) : JsonSerializer.SerializeToElement(new { });
            var result = await _scheduler.Gateway.ExecuteAsync(caller, new(cancel ? AutomationOperation.CancelRuntimeUpdate : AutomationOperation.PrepareRuntimeUpdate,
                parameters, Guid.NewGuid(), "host.update.control", new(plugin), DateTimeOffset.UtcNow.AddSeconds(30)));
            if (result.Status != OperationStatus.Success && _activePlugin == plugin)
                failure = "更新准备未受理 · " + result.Error;
        }
        catch (Exception error) { failure = "更新准备失败 · " + error.Message; }
        finally { _updateControlBusy = false; }
        await RefreshRuntimeUpdateAsync();
        if (failure is not null && _activePlugin == plugin) RuntimeUpdateStatus.Text = failure;
    }
}
