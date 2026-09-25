using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
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
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        Title = "AppDisplayName".GetLocalized();
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _settings = new UISettings();
        _settings.ColorValuesChanged += Settings_ColorValuesChanged;
    }

    public async Task InitializePluginShellAsync()
    {
        if (_initialized) return;
        _initialized = true;
        var reload = App.GetService<IHotReloadService>();
        reload.Reloaded += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            ReloadStatus.Text = e.PluginId + " 已于 " + e.Timestamp.ToString("HH:mm:ss") + " 重载";
            RefreshPluginList();
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
        try { await next.LoadFileAsync(Path.Combine(plugin.PluginDirectory, "ui.json")); }
        catch (Exception error) { next.ShowLoadError(error); }
        next.AttachHotReload(App.GetService<IHotReloadService>(), pluginId);
        PluginContent.Content = next;
        _host = next;
        _activePlugin = pluginId;
        EmptyMessage.Visibility = Visibility.Collapsed;
        ReloadStatus.Text = "监控中 · " + plugin.Name;
    }

    private void Settings_ColorValuesChanged(UISettings sender, object args) =>
        _dispatcherQueue.TryEnqueue(TitleBarHelper.ApplySystemThemeToCaptionButtons);
}
