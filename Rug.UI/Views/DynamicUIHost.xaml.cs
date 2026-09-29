using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Schema;
using Rug.UI.Services;

namespace Rug.UI.Views;

public sealed partial class DynamicUIHost : Page
{
    private readonly SchemaParser _parser = new();
    private UIFactory? _factory;
    private IHotReloadService? _hotReload;
    private IDisposable? _hotReloadRegistration;
    private string? _pluginId;
    private ICommandBus? _bus;
    private DemoWorkflowCoordinator? _demo;
    private CancellationTokenSource? _noticeTimer;

    public DynamicUIHost()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            DetachHotReload();
            DetachDemo();
            Clear();
        };
    }

    public void LoadJson(string json, ICommandBus? bus = null)
    {
        UISchemaNode schema = _parser.Parse(json);
        Render(schema, bus ?? App.GetService<ICommandBus>());
    }

    public async Task LoadFileAsync(string path, ICommandBus? bus = null,
        CancellationToken cancellationToken = default, string? pluginId = null)
    {
        UISchemaNode schema = await _parser.ParseFileAsync(path, cancellationToken);
        _pluginId = pluginId ?? _pluginId ??
            Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? "preview";
        Render(schema, bus ?? App.GetService<ICommandBus>());
    }

    public void AttachDemo(DemoWorkflowCoordinator demo)
    {
        DetachDemo();
        _demo = demo;
        demo.TimelineChanged += OnTimelineChanged;
        _factory?.SetActiveInstance(demo.ActiveInstanceId, demo.CurrentSteps);
    }

    private void OnTimelineChanged(object? sender, EventArgs args) => RunOnUi(() =>
    {
        if (_demo is { } demo) _factory?.SetActiveInstance(demo.ActiveInstanceId, demo.CurrentSteps);
    });

    private void DetachDemo()
    {
        if (_demo is not null) _demo.TimelineChanged -= OnTimelineChanged;
        _demo = null;
    }

    public void ShowLoadError(Exception error) =>
        ShowNotice(InfoBarSeverity.Error, "UI Schema 加载失败", error.ToString());

    public void AttachHotReload(IHotReloadService service, string pluginId, ICommandBus? bus = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        DetachHotReload();
        _hotReload = service;
        _pluginId = pluginId;
        _bus = bus ?? App.GetService<ICommandBus>();
        _hotReloadRegistration = service.RegisterUiHandler(pluginId, ReloadUiAsync);
        service.Reloaded += OnReloaded;
        service.ReloadFailed += OnReloadFailed;
    }

    private async Task ReloadUiAsync(string schemaPath, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await LoadFileAsync(schemaPath, _bus, cancellationToken);
                completion.TrySetResult(true);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }))
            throw new InvalidOperationException("UI dispatcher rejected the reload request.");
        await completion.Task.WaitAsync(cancellationToken);
    }

    private void Render(UISchemaNode schema, ICommandBus bus)
    {
        // Build the replacement before removing the old view. Invalid schemas keep the last good UI.
        var nextFactory = new UIFactory(bus, _pluginId ?? "preview");
        UIElement tree;
        try { tree = nextFactory.Build(schema); }
        catch
        {
            nextFactory.Dispose();
            throw;
        }
        Clear();
        _factory = nextFactory;
        if (_demo is not null)
            _factory.SetActiveInstance(_demo.ActiveInstanceId, _demo.CurrentSteps);
        _factory.CommandFailed += OnCommandFailed;
        DynamicContent.Children.Add(tree);
        ErrorBanner.IsOpen = false;
    }

    private void OnReloaded(object? sender, HotReloadEventArgs e)
    {
        if (!string.Equals(e.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase)) return;
        RunOnUi(() => ShowNotice(InfoBarSeverity.Success, "自动更新完成",
            $"脚本/UI 已于 {e.Timestamp:HH:mm:ss} 自动更新", autoHide: true));
    }

    private void OnReloadFailed(object? sender, HotReloadFailedEventArgs e)
    {
        if (!string.Equals(e.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase)) return;
        RunOnUi(() => ShowNotice(InfoBarSeverity.Error, "热重载失败", e.Error.ToString()));
    }

    private void OnCommandFailed(object? sender, Exception error) =>
        ShowNotice(InfoBarSeverity.Error, "命令执行失败", error.Message);

    private void ShowNotice(InfoBarSeverity severity, string title, string message, bool autoHide = false)
    {
        _noticeTimer?.Cancel();
        _noticeTimer?.Dispose();
        _noticeTimer = null;
        ErrorBanner.Severity = severity;
        ErrorBanner.Title = title;
        ErrorBanner.Message = message;
        ErrorBanner.IsOpen = true;
        if (!autoHide) return;
        var timer = new CancellationTokenSource();
        _noticeTimer = timer;
        _ = HideNoticeAsync(timer.Token);
    }

    private async Task HideNoticeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            RunOnUi(() =>
            {
                if (!cancellationToken.IsCancellationRequested) ErrorBanner.IsOpen = false;
            });
        }
        catch (OperationCanceledException) { }
    }

    private void RunOnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => action());
    }

    private void Clear()
    {
        DynamicContent.Children.Clear();
        if (_factory is null) return;
        _factory.CommandFailed -= OnCommandFailed;
        _factory.Dispose();
        _factory = null;
    }

    private void DetachHotReload()
    {
        _noticeTimer?.Cancel();
        _noticeTimer?.Dispose();
        _noticeTimer = null;
        _hotReloadRegistration?.Dispose();
        _hotReloadRegistration = null;
        if (_hotReload is not null)
        {
            _hotReload.Reloaded -= OnReloaded;
            _hotReload.ReloadFailed -= OnReloadFailed;
        }
        _hotReload = null;
        _pluginId = null;
    }
}
