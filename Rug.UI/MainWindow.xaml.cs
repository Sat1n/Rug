using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Schema;
using Rug.UI.Helpers;
using Rug.UI.Views;
using Windows.UI.ViewManagement;

namespace Rug.UI;

public sealed partial class MainWindow : WindowEx
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private readonly UISettings _settings;
    private string? _activePlugin;
    private DynamicUIHost? _host;
    private ITaskScheduler? _scheduler;
    private IInputArbiter? _inputArbiter;
    private IInputInterventionCoordinator? _intervention;
    private Services.DemoWorkflowCoordinator? _demo;
    private IDisposable? _startSubscription;
    private IDisposable? _stopSubscription;
    private IDisposable? _pauseSubscription;
    private IDisposable? _resumeSubscription;
    private Guid? _selectedInstanceId;
    private bool _updatingInstancePicker;
    private int _lifecycleRefreshQueued;
    private bool _initialized;
    private IReadOnlyList<PluginSnapshot> _effectivePlugins = [];
    private bool _closingInput, _inputClosed;
    private Window? _traceWindow;
    private Rug.UI.Core.Services.TraceReviewService? _traceReviews;
    private void OpenTraceViewer_Click(object sender,RoutedEventArgs args)
    {
        if(_traceWindow is not null){_traceWindow.Activate();return;}
        try
        {
            _traceReviews=App.GetService<Rug.UI.Core.Services.TraceReviewService>();
            var page=new TraceViewerPage();var window=new Window{Title="Rug · 轨迹与异常（只读）",Content=page};
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280,1000));
            _traceWindow=window;
            window.Closed+=(_,_)=>{page.CloseQueries();_traceWindow=null;};
            window.Activate();
        }
        catch(Exception error){ReloadStatus.Text="轨迹查看器不可用 · "+error.GetType().Name;}
    }
    private CancellationTokenSource? _checkpointCancellation;
    private sealed record CheckpointItem(CheckpointChoice Choice)
    {
        public string Label => $"{Choice.PluginId} · {Choice.NodeId ?? "未知节点"} · {Choice.SourceInstanceId?.ToString("N")[..8]} · {Choice.TimestampUtc:u} · #{Choice.Sequence} · {Choice.Compatibility}";
    }
    private async void RefreshCheckpoints_Click(object sender, RoutedEventArgs args)
    {
        if (_checkpointCancellation is not null) return;
        string? plugin = _activePlugin;
        RefreshCheckpointsButton.IsEnabled = false;
        try
        {
            if (plugin is null) throw new InvalidOperationException("请先选择插件。");
            var choices = await _scheduler!.QueryCheckpointsAsync(plugin);
            if (_activePlugin != plugin || _closingInput || _checkpointCancellation is not null) return;
            CheckpointPicker.ItemsSource = choices.Select(c => new ComboBoxItem { Content = new CheckpointItem(c).Label, Tag = c }).ToArray();
            CheckpointPicker.SelectedIndex = choices.Count == 0 ? -1 : 0;
            CheckpointStatus.Text = choices.Count == 0 ? "当前安装没有可归属的检查点。" : "请选择检查点；恢复将再次核验记录、代码、配置及当前画面。";
        }
        catch (Exception error) { CheckpointStatus.Text = "检查点查询失败 · " + error.Message; }
        finally { RefreshCheckpointsButton.IsEnabled = _checkpointCancellation is null; }
    }
    private async void RestoreCheckpoint_Click(object sender, RoutedEventArgs args)
    {
        if (_checkpointCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(); _checkpointCancellation = cancellation;
        CheckpointPicker.IsEnabled = RestoreCheckpointButton.IsEnabled = RefreshCheckpointsButton.IsEnabled = false; CancelCheckpointButton.IsEnabled = true;
        try
        {
            string plugin = _activePlugin ?? throw new InvalidOperationException("请先选择插件。");
            var choice = (CheckpointPicker.SelectedItem as ComboBoxItem)?.Tag as CheckpointChoice ?? throw new InvalidOperationException("请先查看并选择检查点。");
            if (choice.PluginId != plugin || choice.Compatibility != "Ready") throw new InvalidOperationException("此检查点不可恢复：" + choice.Compatibility);
            var tools = App.GetService<ViewModels.DevToolsViewModel>();
            var target = tools.BoundInputTarget ?? throw new InvalidOperationException("请先在开发工具中重新绑定目标窗口。");
            if (!await App.GetService<Services.PluginPermissionConsent>().ConfirmStartAsync(plugin, cancellation.Token)) throw new OperationCanceledException();
            var caller = await _scheduler!.CreateCheckpointRestoreContextAsync(plugin, target, token: cancellation.Token);
            CheckpointStatus.Text = "正在准备／验证检查点，尚未开放业务执行…";
            void BindingChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
            { if (e.PropertyName == "BoundWindow") cancellation.Cancel(); }
            tools.PropertyChanged += BindingChanged;
            try
            {
                var request = new OperationRequest(AutomationOperation.RestoreCheckpoint, JsonSerializer.SerializeToElement(new { selectionId = choice.SelectionId }),
                    Guid.NewGuid(), Guid.NewGuid().ToString("N"), new(plugin), DateTimeOffset.UtcNow.AddSeconds(30));
                var result = await _scheduler.Gateway.ExecuteAsync(caller, request, cancellation.Token);
                if (result.Status == OperationStatus.Success && result.Value is RunIdentity run)
                { _selectedInstanceId = run.InstanceId; RefreshInstancePicker(); CheckpointStatus.Text = "已从检查点启动新实例 · " + run.InstanceId.ToString("N"); }
                else CheckpointStatus.Text = $"恢复未启动 · {result.Status} / {result.Error}";
            }
            finally { tools.PropertyChanged -= BindingChanged; }
        }
        catch (OperationCanceledException) { CheckpointStatus.Text = "恢复准备已取消；不会续接旧实例。"; }
        catch (Exception error) { CheckpointStatus.Text = "检查点恢复失败 · " + error.Message; }
        finally { _checkpointCancellation = null; CheckpointPicker.IsEnabled = RestoreCheckpointButton.IsEnabled = RefreshCheckpointsButton.IsEnabled = true; CancelCheckpointButton.IsEnabled = false; }
    }
    private void CancelCheckpoint_Click(object sender, RoutedEventArgs args) => _checkpointCancellation?.Cancel();

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        Title = "AppDisplayName".GetLocalized();
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _settings = new UISettings();
        _settings.ColorValuesChanged += Settings_ColorValuesChanged;
        AppWindow.Closing += async (_, args) =>
        {
            if (_inputClosed) return;
            args.Cancel = true;
            if (_closingInput) return;
            _closingInput = true;
            await ShutdownInputAsync();
            _inputClosed = true;
            Close();
        };
        Closed += (_, _) =>
        {
            _traceWindow?.Close();
            _traceReviews?.Dispose();
            if (_scheduler is not null) _scheduler.InstancesChanged -= OnInstancesChanged;
            if (_scheduler is not null) _scheduler.LifecycleChanged -= OnLifecycleChanged;
            if (_scheduler?.RuntimeUpdates is { } updates) updates.Changed -= RuntimeUpdateChanged;
            if (_inputArbiter is not null) _inputArbiter.StateChanged -= InputControlChanged;
            if (_intervention is not null) _intervention.StateChanged -= InputControlChanged;
            if (_demo is not null) _demo.TimelineChanged -= OnDemoTimelineChanged;
            _startSubscription?.Dispose();
            _stopSubscription?.Dispose();
            _pauseSubscription?.Dispose();
            _resumeSubscription?.Dispose();
        };
    }

    public async Task InitializePluginShellAsync()
    {
        if (_initialized) return;
        _initialized = true;
        _scheduler = App.GetService<ITaskScheduler>();
        if (_scheduler.RuntimeUpdates is { } updates) updates.Changed += RuntimeUpdateChanged;
        _inputArbiter = App.GetService<IInputArbiter>();
        _intervention = App.GetService<IInputInterventionCoordinator>();
        _intervention.StateChanged += InputControlChanged;
        _inputArbiter.StateChanged += InputControlChanged;
        RefreshInputControl();
        _scheduler.InstancesChanged += OnInstancesChanged;
        _scheduler.LifecycleChanged += OnLifecycleChanged;
        _demo = App.GetService<Services.DemoWorkflowCoordinator>();
        _demo.TimelineChanged += OnDemoTimelineChanged;
        var bus = App.GetService<ICommandBus>();
        _startSubscription = bus.Subscribe("host.start_windowless", StartWindowlessAsync);
        _stopSubscription = bus.Subscribe("host.stop_selected", StopSelectedAsync);
        _pauseSubscription = bus.Subscribe("host.pause_selected", (_, token) => ControlSelectedAsync(AutomationOperation.Pause, token));
        _resumeSubscription = bus.Subscribe("host.resume_selected", (_, token) => ControlSelectedAsync(AutomationOperation.Resume, token));
        var reload = App.GetService<IHotReloadService>();
        reload.Reloaded += (_, e) => DispatcherQueue.TryEnqueue(async () =>
        {
            if (e.Status == PluginUpdateStatus.Superseded) return;
            if (e.Status == PluginUpdateStatus.Unchanged) { ReloadStatus.Text = e.PluginId + " · 内容未变，未重复应用"; return; }
            ReloadStatus.Text = e.Status == PluginUpdateStatus.RuntimePending ? e.PluginId + " · 更新待应用，旧任务继续运行" :
                e.PluginId + " 已于 " + e.Timestamp.ToString("HH:mm:ss") + " 更新";
            if (e.Status == PluginUpdateStatus.RuntimePending) return;
            _effectivePlugins = await _scheduler.GetPluginSnapshotsAsync();
            RefreshPluginList();
            RefreshInstancePicker();
        });
        reload.ReloadFailed += (_, e) => DispatcherQueue.TryEnqueue(() =>
            ReloadStatus.Text = e.PluginId + " 重载失败：" + e.Error.Message);
        _effectivePlugins = await _scheduler.GetPluginSnapshotsAsync();
        RefreshPluginList();
        if (PluginNavigation.MenuItems.FirstOrDefault() is NavigationViewItem first)
        {
            PluginNavigation.SelectedItem = first;
            await SelectPluginAsync((string)first.Tag);
        }
        else
        {
            EmptyMessage.Text = "未发现插件。请在插件目录中添加 manifest.json、ui.json 与 main.lua。";
            EmptyMessage.Visibility = Visibility.Visible;
        }
    }

    private void InputControlChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(RefreshInputControl);
    private void RefreshInputControl()
    {
        // Reread instead of applying queued snapshots: stale notifications cannot roll state back.
        var snapshot = _inputArbiter?.Snapshot;
        var detector = _intervention?.Snapshot;
        InputControlStatus.Text = snapshot?.State switch
        {
            InputControlState.TakingOver => "正在接管／等待输入清理",
            InputControlState.ManualControl => "人工控制已生效",
            InputControlState.CleanupFailed => "输入清理失败：" + snapshot.Error,
            _ => detector?.DetectorState == InputEventSourceState.Ready && detector.DetectorError is null
                ? "自动化可用" : "自动输入不可用（检测器未就绪）"
        };
        InputDetectorStatus.Text = detector is null ? "输入检测器尚未就绪" :
            detector.DetectorError is not null ? "输入检测器故障：" + detector.DetectorError :
            detector.DetectorState != InputEventSourceState.Ready ? "输入检测器尚未就绪 · " + detector.DetectorState :
            detector.Monitoring ? $"输入检测器就绪 · 正在监测 {detector.Sessions} 个输入会话" :
            detector.Sessions > 0 ? $"输入检测器就绪 · 接管中 {detector.Sessions} 个输入会话" : "输入检测器就绪 · 无输入会话";
        if (detector?.Reason is not null) InputDetectorStatus.Text += " · " + detector.Reason;
        if (detector?.CleanupError is not null) InputDetectorStatus.Text += " · 清理失败：" + detector.CleanupError;
    }
    private async void ManualTakeover_Click(object sender, RoutedEventArgs args)
    {
        try { if (_inputArbiter is not null) await _inputArbiter.TakeOverAsync(); }
        catch (Exception error) { InputControlStatus.Text = "输入清理失败：" + error.Message; }
        RefreshInputControl();
    }
    private void ReturnControl_Click(object sender, RoutedEventArgs args)
    {
        try { _inputArbiter?.ReturnControl(); }
        catch (Exception error) { InputControlStatus.Text = error.Message; }
        RefreshInputControl();
    }
    private async Task ShutdownInputAsync()
    {
        _checkpointCancellation?.Cancel();
        try
        {
            await App.GetService<IHotReloadService>().DisposeAsync();
            await App.GetService<IInputInterventionCoordinator>().DisposeAsync();
            await App.GetService<ITaskScheduler>().DisposeAsync();
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine("Input host shutdown failed: " + error.Message);
        }
    }

    private void RefreshPluginList()
    {
        var plugins = _effectivePlugins.Select(p => p.Manifest).ToArray();
        PluginNavigation.MenuItems.Clear();
        foreach (PluginManifest plugin in plugins)
            PluginNavigation.MenuItems.Add(new NavigationViewItem
            {
                Content = plugin.Name,
                Tag = plugin.Id,
                Icon = new SymbolIcon(Symbol.Document)
            });
        if (_activePlugin is not null)
            PluginNavigation.SelectedItem = PluginNavigation.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(item => (string)item.Tag == _activePlugin);
    }

    private async void PluginNavigation_SelectionChanged(NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string id)
        {
            if (id != _activePlugin) _checkpointCancellation?.Cancel();
            if (id == "__devtools")
            {
                PluginContent.Content = App.GetService<DevToolsPage>();
                _host = null;
                _activePlugin = null;
                _selectedInstanceId = null;
                RefreshInstancePicker();
                return;
            }
            await SelectPluginAsync(id);
        }
    }

    private async Task SelectPluginAsync(string pluginId)
    {
        if (_activePlugin == pluginId && _host is not null) return;
        var snapshot = (await _scheduler!.GetPluginSnapshotsAsync()).FirstOrDefault(item => item.PluginId == pluginId);
        var plugin = snapshot?.Manifest;
        if (plugin is null) return;
        var next = new DynamicUIHost();
        try { next.LoadJson(snapshot!.UiJson ?? "{\"type\":\"Page\",\"id\":\"empty-plugin-ui\"}", pluginId: plugin.Id); }
        catch (Exception error) { next.ShowLoadError(error); }
        next.AttachHotReload(App.GetService<IHotReloadService>(), pluginId);
        PluginContent.Content = next;
        _host = next;
        _activePlugin = pluginId;
        CheckpointPicker.ItemsSource = null;
        _selectedInstanceId = null;
        RefreshInstancePicker();
        EmptyMessage.Visibility = Visibility.Collapsed;
        ReloadStatus.Text = "监控中 · " + plugin.Name;
        _updateView = null;
        await RefreshRuntimeUpdateAsync();
    }

    private async void PluginPermissions_Click(object sender, RoutedEventArgs args)
    {
        if (_activePlugin is null) return;
        try { await App.GetService<Services.PluginPermissionConsent>().ShowGrantsAsync(_activePlugin); }
        catch (Exception error) { ReloadStatus.Text = error.Message; }
    }

    private void OnInstancesChanged(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(RefreshInstancePicker);

    private void OnDemoTimelineChanged(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(RefreshInstancePicker);

    private void RefreshInstancePicker()
    {
        var runs = _activePlugin is null ? [] : _scheduler!.Instances
            .Where(item => string.Equals(item.PluginId, _activePlugin, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.RunGeneration).ToArray();
        TaskExecutionContext? selected = runs.FirstOrDefault(item => item.InstanceId == _selectedInstanceId)
            ?? runs.LastOrDefault();
        _selectedInstanceId = selected?.InstanceId;
        _updatingInstancePicker = true;
        try
        {
            InstancePicker.Items.Clear();
            foreach (TaskExecutionContext run in runs)
                InstancePicker.Items.Add(new ComboBoxItem
                {
                    Content = $"运行 #{run.RunGeneration}",
                    Tag = run.InstanceId
                });
            InstancePicker.IsEnabled = runs.Length > 0;
            NextInstanceButton.IsEnabled = runs.Length > 1;
            InstancePicker.PlaceholderText = "无运行实例";
            InstancePicker.SelectedItem = InstancePicker.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is Guid id && id == _selectedInstanceId);
        }
        finally { _updatingInstancePicker = false; }
        BindSelectedInstance(selected);
    }

    private void InstancePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingInstancePicker) return;
        _selectedInstanceId = (InstancePicker.SelectedItem as ComboBoxItem)?.Tag as Guid?;
        TaskExecutionContext? selected = _scheduler?.Instances.FirstOrDefault(item =>
            item.InstanceId == _selectedInstanceId &&
            string.Equals(item.PluginId, _activePlugin, StringComparison.OrdinalIgnoreCase));
        BindSelectedInstance(selected);
    }

    private void NextInstanceButton_Click(object sender, RoutedEventArgs args)
    {
        if (_activePlugin is null || _scheduler is null) return;
        TaskExecutionContext[] runs = _scheduler.Instances
            .Where(item => string.Equals(item.PluginId, _activePlugin, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.RunGeneration).ToArray();
        if (runs.Length < 2) return;
        int index = Array.FindIndex(runs, item => item.InstanceId == _selectedInstanceId);
        _selectedInstanceId = runs[(index + 1) % runs.Length].InstanceId;
        RefreshInstancePicker();
    }

    private void BindSelectedInstance(TaskExecutionContext? selected)
    {
        RefreshLifecycle();
        if (_host is null) return;
        IReadOnlyDictionary<string, IReadOnlyList<SchemaStep>>? completedDemo = null;
        if (selected is null && _activePlugin == Services.DemoWorkflowCoordinator.PluginId &&
            _demo is { ActiveInstanceId: not null } demo)
            completedDemo = new Dictionary<string, IReadOnlyList<SchemaStep>>(StringComparer.Ordinal)
            {
                [Services.DemoWorkflowCoordinator.TimelineStateKey] = demo.CurrentSteps
            };
        _host.SetActiveInstance(selected?.Identity, completedDemo);
    }

    private void OnLifecycleChanged(object? sender, LifecycleChangedEventArgs args)
    {
        if (Interlocked.Exchange(ref _lifecycleRefreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _lifecycleRefreshQueued, 0);
            RefreshLifecycle(); // Re-query selected identity; never replay delayed event values.
        })) Interlocked.Exchange(ref _lifecycleRefreshQueued, 0);
    }

    private void RefreshLifecycle()
    {
        LifecycleSnapshot? snapshot = _selectedInstanceId is { } id ? _scheduler?.GetLifecycle(id) : null;
        if (snapshot is not null && !string.Equals(snapshot.Identity.PluginId, _activePlugin, StringComparison.OrdinalIgnoreCase))
            snapshot = null;
        LifecycleStatus.Text = Rug.UI.Helpers.LifecyclePresentation.Caption(snapshot);
        if (snapshot?.Recovery is { } recovery && snapshot.Status is AutomationTaskStatus.Recovering or AutomationTaskStatus.AwaitingHuman)
            LifecycleStatus.Text += " · " + recovery.Reason;
        VerifyRecoveryButton.IsEnabled = snapshot is { Status: AutomationTaskStatus.AwaitingHuman };
        PauseInstanceButton.IsEnabled = snapshot is { PauseRequested: false,
            Status: AutomationTaskStatus.Starting or AutomationTaskStatus.Running };
        ResumeInstanceButton.IsEnabled = snapshot is { Status: AutomationTaskStatus.Paused, ResumeRequested: false };
        StopInstanceButton.IsEnabled = snapshot is { Status: AutomationTaskStatus.Starting or AutomationTaskStatus.Running or AutomationTaskStatus.Paused or AutomationTaskStatus.Recovering or AutomationTaskStatus.AwaitingHuman };
    }

    private async void VerifyRecovery_Click(object sender, RoutedEventArgs args)
    {
        if (_scheduler is null || _selectedInstanceId is not { } id || _scheduler.GetLifecycle(id) is not { Recovery: { } recovery } snapshot) return;
        VerifyRecoveryButton.IsEnabled = false;
        try
        {
            var caller = await _scheduler.Gateway.CreateContextAsync(snapshot.Identity.PluginId, "host-ui", AutomationSource.PluginUI, snapshot.Identity);
            var request = new OperationRequest(AutomationOperation.VerifyRecovery,
                JsonSerializer.SerializeToElement(new { recoveryId = recovery.RecoveryId }), Guid.NewGuid(),
                recovery.RecoveryId.ToString("N"), OperationTarget.ForRun(snapshot.Identity), DateTimeOffset.UtcNow.AddSeconds(30));
            var result = await _scheduler.Gateway.ExecuteAsync(caller, request);
            if (result.Status != OperationStatus.Success) ReloadStatus.Text = "验证未完成 · " + result.Error;
            else if (result.Value is RecoveryResult outcome) ReloadStatus.Text = outcome.Status == RecoveryStatus.Resolved ? "验证通过，原协程继续" : "仍需人工处理 · " + outcome.Error;
        }
        catch (Exception error) { ReloadStatus.Text = "验证失败 · " + error.Message; }
        finally { RefreshLifecycle(); }
    }

    private async void PauseInstance_Click(object sender, RoutedEventArgs args) => await LifecycleCommandAsync(AutomationOperation.Pause);
    private async void ResumeInstance_Click(object sender, RoutedEventArgs args) => await LifecycleCommandAsync(AutomationOperation.Resume);
    private async void StopInstance_Click(object sender, RoutedEventArgs args) => await LifecycleCommandAsync(AutomationOperation.Stop);
    private async Task LifecycleCommandAsync(AutomationOperation operation)
    {
        try { await ControlSelectedAsync(operation, CancellationToken.None); }
        catch (Exception error) { ReloadStatus.Text = "生命周期请求失败 · " + error.Message; }
        finally { RefreshLifecycle(); }
    }

    private async Task StartWindowlessAsync(object? parameter, CancellationToken cancellationToken)
    {
        string pluginId = _activePlugin ?? throw new InvalidOperationException("No plugin is selected.");
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (parameter is string json)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Launch inputs must be a JSON object.", nameof(parameter));
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                inputs.Add(property.Name, property.Value.Clone());
        }
        else if (parameter is not null)
            throw new ArgumentException("Launch inputs must be a JSON object.", nameof(parameter));
        ReloadStatus.Text = "正在启动 · " + pluginId;
        try
        {
            var identity = (RunIdentity)(await Services.PluginAutomationCommands.ExecuteAsync(_scheduler!, pluginId,
                AutomationOperation.Start, inputs: inputs, cancellationToken: cancellationToken))!;
            Guid id = identity.InstanceId;
            RefreshInstancePicker();
            ReloadStatus.Text = "运行中 · " + id.ToString("N")[..8];
        }
        catch (Exception error)
        {
            ReloadStatus.Text = "启动失败 · " + error.Message;
            throw;
        }
    }

    private async Task StopSelectedAsync(object? parameter, CancellationToken cancellationToken)
        => await ControlSelectedAsync(AutomationOperation.Stop, cancellationToken);

    private async void StartBoundTask_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            string plugin = _activePlugin ?? throw new InvalidOperationException("请先选择插件。");
            var window = App.GetService<ViewModels.DevToolsViewModel>().BoundWindow ??
                throw new InvalidOperationException("请先在开发工具中用准星绑定目标窗口。");
            _inputArbiter!.BindTarget(window.Hwnd);
            await Services.PluginAutomationCommands.ExecuteAsync(_scheduler!, plugin, AutomationOperation.Start, startHwnd: window.Hwnd);
            RefreshInstancePicker();
        }
        catch (Exception error) { ReloadStatus.Text = "启动失败 · " + error.Message; }
    }

    private async Task ControlSelectedAsync(AutomationOperation operation, CancellationToken cancellationToken)
    {
        TaskExecutionContext? run = _scheduler?.Instances.FirstOrDefault(item => item.InstanceId == _selectedInstanceId);
        if (run is not null)
            await Services.PluginAutomationCommands.ExecuteAsync(_scheduler!, run.PluginId, operation,
                run.Identity, cancellationToken: cancellationToken);
    }

    private void Settings_ColorValuesChanged(UISettings sender, object args) =>
        _dispatcherQueue.TryEnqueue(TitleBarHelper.ApplySystemThemeToCaptionButtons);
}
