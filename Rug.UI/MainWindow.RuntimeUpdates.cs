using System.Text.Json;
using Microsoft.UI.Xaml;
using Rug.UI.Core.Models;

namespace Rug.UI;

public sealed partial class MainWindow
{
    private RuntimeUpdateSnapshot? _updateView;
    private bool _updateControlBusy;
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
            bool active = snapshot?.Stage is RuntimeUpdateStage.WaitingCheckpoint or RuntimeUpdateStage.Frozen;
            PrepareUpdateButton.IsEnabled = !_updateControlBusy && snapshot is not null && !active;
            CancelUpdateButton.IsEnabled = !_updateControlBusy && active;
            RuntimeUpdateStatus.Text = snapshot is null ? "无运行更新准备；文件保存不会冻结任务。" :
                (snapshot.Stage switch
                {
                    RuntimeUpdateStage.Pending => "运行更新待应用",
                    RuntimeUpdateStage.WaitingCheckpoint => "等待安全检查点",
                    RuntimeUpdateStage.Frozen => "已冻结，尚未切换",
                    RuntimeUpdateStage.Cancelled => "更新准备已取消，未切换代码",
                    _ => "更新准备失败 · " + snapshot.Error + " · " + snapshot.Reason
                }) + $" · {snapshot.Instances.Count(p => p.Frozen)}/{snapshot.Instances.Count} 实例已冻结" +
                (snapshot.Instances.Count == 0 ? "" : " · " + string.Join(" / ", snapshot.Instances.Select(p =>
                    p.Identity.InstanceId.ToString("N")[..8] + ":" + (p.Frozen ? p.Checkpoint?.NodeId + "#" + p.Checkpoint?.SubmissionSequence : !active ? "冻结已释放" : p.Checkpoint is null ? "等待节点" : "节点已保存，等待排空"))));
        }
        catch (Exception error) { if (_activePlugin == plugin) RuntimeUpdateStatus.Text = "更新准备查询失败 · " + error.Message; }
    }
    private async void PrepareUpdate_Click(object sender, RoutedEventArgs args) => await ControlUpdateAsync(false);
    private async void CancelUpdate_Click(object sender, RoutedEventArgs args) => await ControlUpdateAsync(true);
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
