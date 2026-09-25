using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Schema;
using Rug.UI.Services;

namespace Rug.UI.Views;

public sealed partial class DynamicUIHost : Page
{
    private readonly SchemaParser _parser = new();
    private UIFactory? _factory;

    public DynamicUIHost()
    {
        InitializeComponent();
        Unloaded += (_, _) => Clear();
    }

    public void LoadJson(string json, ICommandBus? bus = null)
    {
        UISchemaNode schema = _parser.Parse(json);
        Clear();
        _factory = new UIFactory(bus ?? App.GetService<ICommandBus>());
        _factory.CommandFailed += OnCommandFailed;
        DynamicContent.Children.Add(_factory.Build(schema));
        ErrorBanner.IsOpen = false;
    }

    public async Task LoadFileAsync(string path, ICommandBus? bus = null,
        CancellationToken cancellationToken = default)
    {
        UISchemaNode schema = await _parser.ParseFileAsync(path, cancellationToken);
        Clear();
        _factory = new UIFactory(bus ?? App.GetService<ICommandBus>());
        _factory.CommandFailed += OnCommandFailed;
        DynamicContent.Children.Add(_factory.Build(schema));
        ErrorBanner.IsOpen = false;
    }

    private void OnCommandFailed(object? sender, Exception error)
    {
        ErrorBanner.Title = "命令执行失败";
        ErrorBanner.Message = error.Message;
        ErrorBanner.IsOpen = true;
    }

    private void Clear()
    {
        DynamicContent.Children.Clear();
        if (_factory is null) return;
        _factory.CommandFailed -= OnCommandFailed;
        _factory.Dispose();
        _factory = null;
    }
}
