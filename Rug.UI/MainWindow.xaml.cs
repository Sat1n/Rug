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
    private bool _closingInput, _inputClosed;

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
            if (_scheduler is not null) _scheduler.InstancesChanged -= OnInstancesChanged;
            if (_scheduler is not null) _scheduler.LifecycleChanged -= OnLifecycleChanged;
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
        reload.Reloaded += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            ReloadStatus.Text = e.PluginId + " 已于 " + e.Timestamp.ToString("HH:mm:ss") + " 重载";
            RefreshPluginList();
            RefreshInstancePicker();
        });
        reload.ReloadFailed += (_, e) => DispatcherQueue.TryEnqueue(() =>
            ReloadStatus.Text = e.PluginId + " 重载失败：" + e.Error.Message);
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
        var plugins = App.GetService<IPluginManager>().ScanPlugins(App.PluginsRoot).ToArray();
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
        var plugin = App.GetService<IPluginManager>().ScanPlugins(App.PluginsRoot)
            .FirstOrDefault(item => item.Id == pluginId);
        if (plugin is null) return;
        var next = new DynamicUIHost();
        try { await next.LoadFileAsync(Path.Combine(plugin.PluginDirectory, "ui.json"), pluginId: plugin.Id); }
        catch (Exception error) { next.ShowLoadError(error); }
        next.AttachHotReload(App.GetService<IHotReloadService>(), pluginId);
        PluginContent.Content = next;
        _host = next;
        _activePlugin = pluginId;
        _selectedInstanceId = null;
        RefreshInstancePicker();
        EmptyMessage.Visibility = Visibility.Collapsed;
        ReloadStatus.Text = "监控中 · " + plugin.Name;
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
        LifecycleStatus.Text = snapshot switch
        {
            { Status: AutomationTaskStatus.Stopping } => "正在停止",
            { Status: AutomationTaskStatus.Paused, ResumeRequested: true } => "正在恢复",
            { Status: AutomationTaskStatus.Paused } => "已暂停",
            { PauseRequested: true } => "正在暂停",
            { Status: AutomationTaskStatus.Starting } => "正在启动",
            { Status: AutomationTaskStatus.Running } => "运行中",
            { Status: AutomationTaskStatus.Faulted } => "运行故障",
            { Status: AutomationTaskStatus.Stopped } => "已停止",
            _ => "无运行实例"
        };
        PauseInstanceButton.IsEnabled = snapshot is { PauseRequested: false,
            Status: AutomationTaskStatus.Starting or AutomationTaskStatus.Running };
        ResumeInstanceButton.IsEnabled = snapshot is { Status: AutomationTaskStatus.Paused, ResumeRequested: false };
        StopInstanceButton.IsEnabled = snapshot is { Status: AutomationTaskStatus.Starting or AutomationTaskStatus.Running or AutomationTaskStatus.Paused };
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
