using Microsoft.UI.Xaml;
using Rug.UI.Activation;
using Rug.UI.Contracts.Services;

namespace Rug.UI.Services;

public class ActivationService : IActivationService
{
    private readonly IEnumerable<IActivationHandler> _activationHandlers;
    private readonly IThemeSelectorService _themeSelectorService;

    public ActivationService(IEnumerable<IActivationHandler> activationHandlers,
        IThemeSelectorService themeSelectorService)
    {
        _activationHandlers = activationHandlers;
        _themeSelectorService = themeSelectorService;
    }

    public async Task ActivateAsync(object activationArgs)
    {
        await _themeSelectorService.InitializeAsync();
        if (App.MainWindow is MainWindow window)
            await window.InitializePluginShellAsync();
        var handler = _activationHandlers.FirstOrDefault(item => item.CanHandle(activationArgs));
        if (handler is not null) await handler.HandleAsync(activationArgs);
        App.MainWindow.Activate();
        await _themeSelectorService.SetRequestedThemeAsync();
    }
}
