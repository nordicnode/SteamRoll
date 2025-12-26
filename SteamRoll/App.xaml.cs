using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SteamRoll.Services;

namespace SteamRoll;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IServiceProvider? _serviceProvider;
    
    /// <summary>
    /// Gets the application's service provider for dependency injection.
    /// </summary>
    public static IServiceProvider Services => ((App)Current)._serviceProvider!;
    
    protected override void OnStartup(StartupEventArgs e)
    {
        // Hook global exception handlers
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            MessageBox.Show($"Fatal error: {ex?.Message}\n\n{ex?.StackTrace}", "SteamRoll Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"UI error: {args.Exception.Message}\n\n{args.Exception.StackTrace}", "SteamRoll Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        
        base.OnStartup(e);
        
        // Build service collection and provider
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
        
        // Initialize ServiceContainer as facade over IServiceProvider
        var legacyContainer = new ServiceContainer(_serviceProvider);
        ServiceContainer.Initialize(legacyContainer);

        // Load transfer history
        _ = SteamRoll.Services.Transfer.TransferManager.Instance.LoadHistoryAsync();

        // Show main window
        var mainWindow = new MainWindow(legacyContainer);
        mainWindow.Show();
    }
    
    /// <summary>
    /// Configures all application services for dependency injection.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // Core infrastructure
        services.AddSingleton<IDispatcherService, WpfDispatcherService>();
        
        // Settings
        services.AddSingleton<SettingsService>(sp =>
        {
            var settings = new SettingsService();
            try { settings.Load(); }
            catch (Exception ex)
            {
                LogService.Instance.Error("Failed to load settings, using defaults", ex, "App");
            }
            return settings;
        });
        
        // Steam services
        services.AddSingleton<SteamLocator>();
        services.AddSingleton<LibraryScanner>(sp => 
            new LibraryScanner(sp.GetRequiredService<SteamLocator>()));
        
        // Goldberg & DLC
        services.AddSingleton<GoldbergService>();
        services.AddSingleton<DlcService>();
        
        // Package services
        services.AddSingleton<PackageScanner>(sp =>
            new PackageScanner(sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<CacheService>();
        
        // HTTP services
        services.AddSingleton<SteamStoreService>();
        services.AddSingleton<GameImageService>(sp =>
            new GameImageService(sp.GetRequiredService<SteamStoreService>()));
        
        // Package building
        services.AddSingleton<PackageBuilder>(sp =>
            new PackageBuilder(
                sp.GetRequiredService<GoldbergService>(),
                sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<DlcService>(),
                steamStoreService: sp.GetRequiredService<SteamStoreService>()));
        
        // Library management
        services.AddSingleton<LibraryManager>(sp =>
            new LibraryManager(
                sp.GetRequiredService<SteamLocator>(),
                sp.GetRequiredService<LibraryScanner>(),
                sp.GetRequiredService<PackageScanner>(),
                sp.GetRequiredService<CacheService>(),
                sp.GetRequiredService<DlcService>(),
                sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<SteamStoreService>(),
                sp.GetRequiredService<GameImageService>()));
        
        // Network services
        services.AddSingleton<LanDiscoveryService>();
        services.AddSingleton<TransferService>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new TransferService(settings.Settings.OutputPath, settings);
        });
        
        // Other services
        services.AddSingleton<SaveGameService>(sp =>
            new SaveGameService(sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<IntegrityService>();
        services.AddSingleton<UpdateService>(sp =>
            new UpdateService(sp.GetRequiredService<GoldbergService>().GoldbergPath));
    }
    
    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose services on exit
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
        ServiceContainer.Instance?.Dispose();
        base.OnExit(e);
    }
}

