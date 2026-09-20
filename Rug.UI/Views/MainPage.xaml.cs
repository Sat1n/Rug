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
    }
}
