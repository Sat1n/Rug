using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using Rug.UI.Activation;
using Rug.UI.Contracts.Services;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using Rug.UI.Helpers;
using Rug.UI.Models;
using Rug.UI.Notifications;
using Rug.UI.Services;
using Rug.UI.ViewModels;
using Rug.UI.Views;

namespace Rug.UI;

// To learn more about WinUI 3, see https://docs.microsoft.com/windows/apps/winui/winui3/.
public partial class App : Application
{
    // The .NET Generic Host provides dependency injection, configuration, logging, and other services.
    // https://docs.microsoft.com/dotnet/core/extensions/generic-host
    // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
    // https://docs.microsoft.com/dotnet/core/extensions/configuration
    // https://docs.microsoft.com/dotnet/core/extensions/logging
    public IHost Host
    {
        get;
    }

    public static T GetService<T>()
        where T : class
    {
        if ((App.Current as App)!.Host.Services.GetService(typeof(T)) is not T service)
        {
            throw new ArgumentException($"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs.");
        }

        return service;
    }

    public static WindowEx MainWindow { get; } = new MainWindow();

    public static UIElement? AppTitlebar { get; set; }
    public static string PluginsRoot { get; } = Path.GetFullPath(
        Environment.GetEnvironmentVariable("RUG_PLUGINS_ROOT") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rug", "plugins"));

    public App()
    {
        InitializeComponent();

        Host = Microsoft.Extensions.Hosting.Host.
        CreateDefaultBuilder().
        UseContentRoot(AppContext.BaseDirectory).
        ConfigureServices((context, services) =>
        {
            // Default Activation Handler
            services.AddTransient<ActivationHandler<LaunchActivatedEventArgs>, DefaultActivationHandler>();

            // Other Activation Handlers
            services.AddTransient<IActivationHandler, AppNotificationActivationHandler>();

            // Services
            services.AddSingleton<IAppNotificationService, AppNotificationService>();
            services.AddSingleton<ILocalSettingsService, LocalSettingsService>();
            services.AddSingleton<IThemeSelectorService, ThemeSelectorService>();
            services.AddTransient<INavigationViewService, NavigationViewService>();

            services.AddSingleton<IActivationService, ActivationService>();
            services.AddSingleton<IPageService, PageService>();
            services.AddSingleton<INavigationService, NavigationService>();

            // Core Services
            services.AddSingleton<IFileService, FileService>();
            services.AddSingleton<StateStore>();
            services.AddSingleton<IInputArbiter>(_ => InputArbiter.Shared);
            services.AddSingleton<IInputEventSource, RawInputEventSource>();
            services.AddSingleton<IInputInterventionCoordinator, InputInterventionCoordinator>();
            services.AddSingleton<IPluginAuthorization, PluginAuthorization>();
            services.AddSingleton<PluginPermissionConsent>();
            services.AddSingleton<IPluginManager, PluginManager>();
            services.AddSingleton(p=>new AnomalyLogger(p.GetRequiredService<ILogger<AnomalyLogger>>(),Environment.GetEnvironmentVariable("RUG_ANOMALY_ROOT")));
            services.AddSingleton<IAnomalyLogger>(p=>p.GetRequiredService<AnomalyLogger>());
            services.AddSingleton<RecoveryValidatorRegistry>(_ =>
            {
                var registry = new RecoveryValidatorRegistry();
                // Trusted sample only; arbitrary plugins have no default success validator.
                registry.Register(new PluginInstallation("recovery_probe", Path.Combine(PluginsRoot, "recovery_probe")),
                    "controlled-ready", new FreshGreenFrameValidator());
                return registry;
            });
            services.AddSingleton<IRecoveryValidatorRegistry>(p => p.GetRequiredService<RecoveryValidatorRegistry>());
            services.AddSingleton<IRecoveryCoordinator, RecoveryCoordinator>();
            services.AddSingleton<ICheckpointStore>(_ => new FileCheckpointStore(Environment.GetEnvironmentVariable("RUG_CHECKPOINT_ROOT")));
            services.AddSingleton<ICheckpointNodeRegistry>(_ =>
            {
                var registry = new CheckpointNodeRegistry();
                registry.Register(CheckpointSamples.Node(new PluginInstallation("checkpoint_probe", Path.Combine(PluginsRoot,"checkpoint_probe"))),
                    new CheckpointSamples.GreenPanelValidator());
                registry.RegisterRestore(CheckpointSamples.Node(new PluginInstallation("checkpoint_probe", Path.Combine(PluginsRoot,"checkpoint_probe"))),
                    new CheckpointSamples.GreenRestoreValidator());
                registry.Register(RuntimeUpdateSamples.Node(new PluginInstallation("update_probe", Path.Combine(PluginsRoot,"update_probe"))),
                    new CheckpointSamples.GreenPanelValidator());
                return registry;
            });
            services.AddSingleton<ICheckpointCoordinator, CheckpointCoordinator>();
            services.AddSingleton<ICheckpointRestoreRegistry>(provider => (ICheckpointRestoreRegistry)provider.GetRequiredService<ICheckpointNodeRegistry>());
            services.AddSingleton<IRuntimeUpdateCompatibilityRegistry>(_ =>
            {
                var registry = new RuntimeUpdateCompatibilityRegistry();
                // Only the embedded exact source/target pair has a direct-continuation rule. Never trust mutable installed hashes.
                RuntimeUpdateSamples.RegisterSwitchAsync(registry, Path.Combine(PluginsRoot,"update_probe")).GetAwaiter().GetResult();
                return registry;
            });
            services.AddSingleton<ICommandBus>(provider =>
            {
                var dispatcher = MainWindow.DispatcherQueue;
                return new CommandBus(provider.GetRequiredService<StateStore>(),
                    action => dispatcher.TryEnqueue(() => action()),
                    () => dispatcher.HasThreadAccess);
            });

            services.AddSingleton(_ => new ExecutionTraceRecorder(
                Environment.GetEnvironmentVariable("RUG_TRACE_ROOT")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Rug","traces"),
                enabled:Environment.GetEnvironmentVariable("RUG_TRACE_DISABLED")!="1"));
            services.AddSingleton(_=>new TraceFileStore(Environment.GetEnvironmentVariable("RUG_TRACE_ROOT")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Rug","traces")));
            services.AddSingleton(p=>new IncidentStore(p.GetRequiredService<AnomalyLogger>().DirectoryPath));
            services.AddSingleton<ITraceReviewReader,TraceReviewReader>();
            services.AddSingleton<TraceReviewService>();
            services.AddSingleton<ITaskScheduler>(provider => new Rug.UI.Core.Services.TaskScheduler(
                provider.GetRequiredService<IPluginManager>(), PluginsRoot,
                () => new CaptureService(),
                () => new InputService(InputMode.Win32Software, 0),
                (capture, input) => new LuaRuntime(capture,
                    provider.GetRequiredService<IOcrService>(), input,
                    provider.GetRequiredService<ILogger<LuaRuntime>>(),
                    new CoordinateMapper(),
                    templateMatcher: provider.GetRequiredService<ITemplateMatchService>(),
                    commandBus: provider.GetRequiredService<ICommandBus>()),
                provider.GetRequiredService<ILogger<Rug.UI.Core.Services.TaskScheduler>>(),
                provider.GetRequiredService<IAnomalyLogger>(),
                commandBus: provider.GetRequiredService<ICommandBus>(),
                authorization: provider.GetRequiredService<IPluginAuthorization>(),
                inputArbiter: provider.GetRequiredService<IInputArbiter>(),
                intervention: provider.GetRequiredService<IInputInterventionCoordinator>(),
                recovery: provider.GetRequiredService<IRecoveryCoordinator>(),
                checkpoints: provider.GetRequiredService<ICheckpointCoordinator>(),
                checkpointStore: provider.GetRequiredService<ICheckpointStore>(),
                checkpointRestores: provider.GetRequiredService<ICheckpointRestoreRegistry>(),
                updateCompatibility: provider.GetRequiredService<IRuntimeUpdateCompatibilityRegistry>(),
                traces:provider.GetRequiredService<ExecutionTraceRecorder>()));
            services.AddSingleton<IAutomationGateway>(provider => provider.GetRequiredService<ITaskScheduler>().Gateway);
            services.AddSingleton<IHotReloadService>(provider => new HotReloadService(
                PluginsRoot, provider.GetRequiredService<ITaskScheduler>(),
                provider.GetRequiredService<ILogger<HotReloadService>>()));
            services.AddSingleton<DemoWorkflowCoordinator>();
            // Rug.UI.Core native-backed services (the only P/Invoke boundary)
            services.AddSingleton<IOcrService>(_ => new OcrService());
            services.AddSingleton<ITemplateMatchService, TemplateMatchService>();
            services.AddSingleton<ICaptureService, CaptureService>();
            services.AddSingleton<IWindowSpyService, WindowSpyService>();
            services.AddSingleton<IInputService>(_ => new InputService(InputMode.Win32Software, 0));

            // Views and ViewModels
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<SettingsPage>();
            services.AddTransient<MainViewModel>();
            services.AddTransient<MainPage>();
            services.AddSingleton<DevToolsViewModel>();
            services.AddTransient<DevToolsPage>();
            services.AddTransient<ShellPage>();
            services.AddTransient<ShellViewModel>();

            // Configuration
            services.Configure<LocalSettingsOptions>(context.Configuration.GetSection(nameof(LocalSettingsOptions)));
        }).
        Build();

        App.GetService<IAppNotificationService>().Initialize();

        UnhandledException += App_UnhandledException;
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // TODO: Log and handle exceptions as appropriate.
        // https://docs.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.application.unhandledexception.
    }

    protected async override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        App.GetService<IAppNotificationService>().Show(string.Format("AppNotificationSamplePayload".GetLocalized(), AppContext.BaseDirectory));

        SeedDemoPlugin();
        try { await App.GetService<IInputInterventionCoordinator>().StartAsync(); }
        catch (Exception error) { App.GetService<ILogger<App>>().LogError(error, "Input detector unavailable; automatic input remains closed"); }
        _ = App.GetService<DemoWorkflowCoordinator>();
        App.GetService<IHotReloadService>().Start();
        await App.GetService<IHotReloadService>().Ready;
        await App.GetService<IActivationService>().ActivateAsync(args);
    }

    private static void SeedDemoPlugin()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RUG_PLUGINS_ROOT"))) return;
        string source = Path.Combine(AppContext.BaseDirectory, "plugins", DemoWorkflowCoordinator.PluginId);
        if (!Directory.Exists(source)) return;
        string destination = Path.Combine(PluginsRoot, DemoWorkflowCoordinator.PluginId);
        Directory.CreateDirectory(destination);
        foreach (string file in new[] { "manifest.json", "ui.json", "main.lua" })
        {
            string target = Path.Combine(destination, file);
            if (!File.Exists(target)) File.Copy(Path.Combine(source, file), target);
        }
    }
}
