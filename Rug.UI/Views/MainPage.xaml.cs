using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Rug.UI.ViewModels;

namespace Rug.UI.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel
    {
        get;
    }

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
            await host.LoadFileAsync(Path.Combine(AppContext.BaseDirectory, "ui.json"));
        }
        catch (Exception ex)
        {
            ContentArea.Children.Clear();
            ContentArea.Children.Add(new InfoBar
            {
                Title = "UI Schema 加载失败", Message = ex.Message,
                Severity = InfoBarSeverity.Error, IsOpen = true
            });
        }
    }
}
