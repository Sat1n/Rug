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
    private Services.DemoWorkflowCoordinator? _demo;
    private IDisposable? _startSubscription;
    private IDisposable? _stopSubscription;
    private IDisposable? _pauseSubscription;
    private IDisposable? _resumeSubscription;
    private Guid? _selectedInstanceId;
    private bool _updatingInstancePicker;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        Title = "AppDisplayName".GetLocalized();
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _settings = new UISettings();
        _settings.ColorValuesChanged += Settings_ColorValuesChanged;
        Closed += (_, _) =>
        {
            if (_scheduler is not null) _scheduler.InstancesChanged -= OnInstancesChanged;
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
        _scheduler.InstancesChanged += OnInstancesChanged;
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
