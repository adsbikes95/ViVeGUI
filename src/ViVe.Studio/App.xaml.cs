// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ViVe.Core;
using ViVe.FeatureCatalog;
using ViVe.Studio.Platform;
using ViVe.Studio.Services;
using ViVe.Studio.ViewModels;

namespace ViVe.Studio;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _services = ConfigureServices();
        var settings = _services.GetRequiredService<ISettingsService>();
        _services.GetRequiredService<IThemeService>().Apply(settings.Current.Theme);

        var window = new MainWindow { DataContext = _services.GetRequiredService<MainViewModel>() };
        MainWindow = window;
        window.Show();
        await ((MainViewModel)window.DataContext).InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    internal static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        var paths = new AppPaths();
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<ISettingsService>(_ => new JsonSettingsService(paths.SettingsFile));
        services.AddSingleton<IAppLogger>(sp => new FileLogger(paths, () => sp.GetRequiredService<ISettingsService>().Current.EnableFileLogging));
        services.AddSingleton<IChangeHistoryStore>(_ => new JsonChangeHistoryStore(paths.HistoryFile));
        services.AddSingleton<IFeatureCatalog>(sp => new ReloadableFeatureCatalog(() => FeatureCatalog.FeatureCatalog.LoadFromDirectory(
            paths.CatalogDirectory,
            sp.GetRequiredService<ISettingsService>().Current.UserCatalogPath,
            new[]
            {
                System.IO.Path.Combine(paths.ImportedCatalogDirectory, Mach2ImportService.ImportFileName),
                System.IO.Path.Combine(paths.ImportedCatalogDirectory, SymbolScanService.CatalogFileName),
            })));
        services.AddSingleton<IFileDownloader, HttpFileDownloader>();
        services.AddSingleton(sp => new Mach2ImportService(paths, sp.GetRequiredService<IFileDownloader>()));
        services.AddSingleton(sp => new SymbolScanService(paths, sp.GetRequiredService<IFileDownloader>()));
        services.AddSingleton(_ => new BuildSnapshotService(paths));

        // Engine: the real ViVe library. All GUI modifications go through FeatureEngine.
        services.AddSingleton<IFeatureConfigurationApi, NativeFeatureConfigurationApi>();
        services.AddSingleton<IFeatureManager>(sp => new FeatureEngine(sp.GetRequiredService<IFeatureConfigurationApi>()));
        services.AddSingleton<FeatureChangeService>();

        services.AddSingleton<ISystemInfoProvider, WindowsSystemInfoProvider>();
        services.AddSingleton<IElevationService, WindowsElevationService>();
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<IThemeService, WpfThemeService>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<FeaturesViewModel>();
        services.AddSingleton(sp => new CollectionsViewModel(sp.GetRequiredService<IFeatureCatalog>(), sp.GetRequiredService<FeaturesViewModel>(),
            sp.GetRequiredService<IFeatureManager>()));
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton(sp => new MainViewModel(sp.GetRequiredService<DashboardViewModel>(), sp.GetRequiredService<FeaturesViewModel>(),
            sp.GetRequiredService<HistoryViewModel>(), sp.GetRequiredService<SettingsViewModel>(), sp.GetRequiredService<IElevationService>(),
            sp.GetRequiredService<IChangeHistoryStore>(), sp.GetRequiredService<CollectionsViewModel>()));
        return services.BuildServiceProvider();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.GetService<IAppLogger>()?.Log(LogLevel.Error, e.Exception.ToString());
        MessageBox.Show(e.Exception.Message, "ViVeTool Studio - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
