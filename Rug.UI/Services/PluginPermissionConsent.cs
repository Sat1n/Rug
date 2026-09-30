using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Services;

/// <summary>Host-owned dialogs. Only explicit foreground UI actions invoke this service.</summary>
public sealed class PluginPermissionConsent(IPluginAuthorization authorization, ITaskScheduler scheduler)
{
    private readonly SemaphoreSlim _dialogs = new(1, 1);
    /// <summary>@shape pluginId: verified catalog identity; return: explicit foreground approval or dismissal.
    /// @source declaration: AutomationGateway.CreateContextAsync and IPluginAuthorization.Query</summary>
    public async Task<bool> ConfirmStartAsync(string pluginId, CancellationToken ct)
    {
        var caller = await scheduler.Gateway.CreateContextAsync(pluginId, "permission-ui", AutomationSource.PluginUI,
            cancellationToken: ct);
        var snapshot = authorization.Query(caller.Authorization.Installation);
        if (authorization.Missing(caller.Authorization, snapshot.Requested).Count == 0) return true;
        await _dialogs.WaitAsync(ct);
        try
        {
            return await OnUIAsync(async () =>
            {
                var dialog = Create(snapshot, "插件请求权限", "Allow permissions", "Deny permissions");
                dialog.IsPrimaryButtonEnabled = snapshot.Requested.All(Permission.IsKnown);
                using var cancel = ct.Register(() => App.MainWindow.DispatcherQueue.TryEnqueue(dialog.Hide));
                var answer = await dialog.ShowAsync();
                if (answer != ContentDialogResult.Primary || ct.IsCancellationRequested) return false;
                // Approve only what was displayed, never a newer manifest's expanded request.
                var latest = authorization.Query(snapshot.Installation);
                if (!latest.Requested.SequenceEqual(snapshot.Requested)) return false;
                authorization.Grant(snapshot.Installation, snapshot.Requested);
                return true;
            });
        }
        finally { _dialogs.Release(); }
    }
    public async Task ShowGrantsAsync(string pluginId)
    {
        var caller = await scheduler.Gateway.CreateContextAsync(pluginId, "permission-ui", AutomationSource.PluginUI);
        await _dialogs.WaitAsync();
        try
        {
            await OnUIAsync(async () =>
            {
                var snapshot = authorization.Query(caller.Authorization.Installation);
                var dialog = Create(snapshot, "当前插件授权（本次应用会话）", "Revoke permissions", "Close");
                dialog.IsPrimaryButtonEnabled = snapshot.Granted.Count > 0;
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    var current = authorization.Query(snapshot.Installation);
                    authorization.Revoke(snapshot.Installation, current.Granted);
                }
                return true;
            });
        }
        finally { _dialogs.Release(); }
    }
    private static ContentDialog Create(AuthorizationSnapshot snapshot, string title, string primary, string close) => new()
    {
        XamlRoot = App.MainWindow.Content.XamlRoot,
        Title = title,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Close,
        Content = new ScrollViewer
        {
            MaxHeight = 400,
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Text = $"插件 ID：{snapshot.Installation.PluginId}\n安装位置：{snapshot.Installation.Directory}\n\n申请权限：\n" +
                    string.Join("\n", snapshot.Requested.Select(p => p + " — " + Purpose(p))) +
                    "\n\n已授权：" + string.Join(", ", snapshot.Granted) +
                    "\n授权仅限本次应用会话。撤销会停止使用该权限的运行实例。"
            }
        }
    };
    private static string Purpose(string permission) => permission switch
    {
        Permission.Timer => "协程等待与计时",
        Permission.Capture => "截图目标窗口",
        Permission.VisionOcr => "识别截图文字",
        Permission.VisionMatch => "匹配截图中的图像",
        Permission.ControlInput => "发送鼠标与键盘输入",
        Permission.FileSystem => "访问插件目录之外的模板文件",
        Permission.Network => "访问网络",
        Permission.Agent => "记录异常并挂起任务",
        _ => "未知权限，无法授权"
    };
    private static Task<T> OnUIAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = App.MainWindow.DispatcherQueue;
        if (dispatcher.HasThreadAccess) return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetException(new InvalidOperationException("UI dispatcher is unavailable."));
        return completion.Task;
    }
}
