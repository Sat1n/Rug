using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using Rug.UI.ViewModels;

namespace Rug.UI.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = App.GetService<MainViewModel>();
        InitializeComponent();
        Loaded += LoadDynamicUi;
    }

    private async void LoadDynamicUi(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadDynamicUi;
        var host = new DynamicUIHost();
        ContentArea.Children.Add(host);
        try
        {
            PluginManifest? plugin = await Task.Run(() =>
                new PluginManager(App.GetService<ILogger<PluginManager>>())
                    .ScanPlugins(App.PluginsRoot)
                    .FirstOrDefault(item => File.Exists(Path.Combine(item.PluginDirectory, "ui.json"))));
            if (plugin is null)
            {
                await host.LoadFileAsync(Path.Combine(AppContext.BaseDirectory, "ui.json"));
                return;
            }
            host.AttachHotReload(App.GetService<IHotReloadService>(), plugin.Id);
            await host.LoadFileAsync(Path.Combine(plugin.PluginDirectory, "ui.json"));
        }
        catch (Exception ex)
        {
            host.ShowLoadError(ex);
        }
    }
}
