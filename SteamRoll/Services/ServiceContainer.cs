using Microsoft.Extensions.DependencyInjection;

namespace SteamRoll.Services;

/// <summary>
/// Service container facade for dependency injection.
/// Acts as a bridge between legacy code and the modern IServiceProvider.
/// All services are resolved from the DI container to ensure single instances.
/// </summary>
public class ServiceContainer : IDisposable
{
    private static ServiceContainer? _instance;
    private readonly IServiceProvider? _serviceProvider;
    private bool _disposed;
    
    /// <summary>
    /// Gets the singleton instance of the service container.
    /// </summary>
    public static ServiceContainer Instance => _instance ?? throw new InvalidOperationException(
        "ServiceContainer not initialized. Call Initialize() first.");
    
    /// <summary>
    /// Initializes the service container explicitly.
    /// Call this at application startup before accessing services.
    /// </summary>
    public static void Initialize(ServiceContainer container)
    {
        _instance = container;
    }
    
    // Core services - resolved from IServiceProvider
    public SettingsService Settings { get; }
    public IDispatcherService Dispatcher { get; }
    public SteamLocator SteamLocator { get; }
    public LibraryScanner LibraryScanner { get; }
    public PackageScanner PackageScanner { get; }
    public GoldbergService GoldbergService { get; }
    public PackageBuilder PackageBuilder { get; }
    public DlcService DlcService { get; }
    public CacheService CacheService { get; }
    public LanDiscoveryService LanDiscoveryService { get; }
    public TransferService TransferService { get; }
    public SaveGameService SaveGameService { get; }
    public UpdateService UpdateService { get; }
    public IntegrityService IntegrityService { get; }
    public LibraryManager LibraryManager { get; }
    public SteamStoreService SteamStoreService { get; }
    public GameImageService GameImageService { get; }
    
    /// <summary>
    /// Creates a new service container that resolves from IServiceProvider.
    /// This is the preferred constructor for production use.
    /// </summary>
    public ServiceContainer(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        
        // Resolve all services from DI container - ensures single instances
        Dispatcher = serviceProvider.GetRequiredService<IDispatcherService>();
        Settings = serviceProvider.GetRequiredService<SettingsService>();
        SteamLocator = serviceProvider.GetRequiredService<SteamLocator>();
        LibraryScanner = serviceProvider.GetRequiredService<LibraryScanner>();
        GoldbergService = serviceProvider.GetRequiredService<GoldbergService>();
        DlcService = serviceProvider.GetRequiredService<DlcService>();
        PackageScanner = serviceProvider.GetRequiredService<PackageScanner>();
        CacheService = serviceProvider.GetRequiredService<CacheService>();
        SteamStoreService = serviceProvider.GetRequiredService<SteamStoreService>();
        GameImageService = serviceProvider.GetRequiredService<GameImageService>();
        PackageBuilder = serviceProvider.GetRequiredService<PackageBuilder>();
        LibraryManager = serviceProvider.GetRequiredService<LibraryManager>();
        LanDiscoveryService = serviceProvider.GetRequiredService<LanDiscoveryService>();
        TransferService = serviceProvider.GetRequiredService<TransferService>();
        SaveGameService = serviceProvider.GetRequiredService<SaveGameService>();
        IntegrityService = serviceProvider.GetRequiredService<IntegrityService>();
        UpdateService = serviceProvider.GetRequiredService<UpdateService>();
    }
    
    /// <summary>
    /// Creates a new service container with specified dispatcher (for testing).
    /// </summary>
    [Obsolete("Use constructor with IServiceProvider for production. This is for testing only.")]
    public ServiceContainer(IDispatcherService dispatcher)
    {
        // Fallback for tests that don't use full DI
        Dispatcher = dispatcher;
        
        Settings = new SettingsService();
        try { Settings.Load(); }
        catch (Exception ex)
        {
            LogService.Instance.Error("Failed to load settings, using defaults", ex, "ServiceContainer");
        }
        
        SteamLocator = new SteamLocator();
        LibraryScanner = new LibraryScanner(SteamLocator);
        GoldbergService = new GoldbergService();
        DlcService = new DlcService();
        PackageScanner = new PackageScanner(Settings);
        CacheService = new CacheService();
        SteamStoreService = new SteamStoreService();
        GameImageService = new GameImageService(SteamStoreService);
        PackageBuilder = new PackageBuilder(GoldbergService, Settings, DlcService, steamStoreService: SteamStoreService);
        LibraryManager = new LibraryManager(
            SteamLocator, LibraryScanner, PackageScanner,
            CacheService, DlcService, Settings, SteamStoreService, GameImageService);
        LanDiscoveryService = new LanDiscoveryService();
        TransferService = new TransferService(Settings.Settings.OutputPath, Settings);
        SaveGameService = new SaveGameService(Settings);
        IntegrityService = new IntegrityService();
        UpdateService = new UpdateService(GoldbergService.GoldbergPath);
    }
    
    /// <summary>
    /// Disposes all disposable services.
    /// Note: When using IServiceProvider, disposal is handled by the DI container.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        // If we have a service provider, it handles disposal
        if (_serviceProvider != null) return;
        
        try
        {
            LanDiscoveryService?.Stop();
            TransferService?.StopListening();
            LanDiscoveryService?.Dispose();
            TransferService?.Dispose();
            GoldbergService?.Dispose();
            DlcService?.Dispose();
            SteamStoreService?.Dispose();
            GameImageService?.Dispose();
            UpdateService?.Dispose();
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Disposal error: {ex.Message}", "ServiceContainer");
        }
    }
}

