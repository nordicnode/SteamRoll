namespace SteamRoll.Services;

/// <summary>
/// Simple service container for dependency injection.
/// Provides a lightweight alternative to full DI frameworks while
/// improving testability and decoupling components.
/// </summary>
public class ServiceContainer : IDisposable
{
    private static ServiceContainer? _instance;
    private bool _disposed;
    
    /// <summary>
    /// Gets the singleton instance of the service container.
    /// </summary>
    public static ServiceContainer Instance => _instance ??= new ServiceContainer();
    
    /// <summary>
    /// Initializes the service container explicitly.
    /// Call this at application startup before accessing services.
    /// </summary>
    public static void Initialize(ServiceContainer container)
    {
        _instance = container;
    }
    
    // Core services
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
    /// Creates a new service container with default WPF services.
    /// </summary>
    public ServiceContainer() : this(null)
    {
    }
    
    /// <summary>
    /// Creates a new service container with specified dispatcher.
    /// Pass null for default WPF dispatcher, or provide custom for testing/other platforms.
    /// </summary>
    public ServiceContainer(IDispatcherService? dispatcher)
    {
        // Default to WPF dispatcher if not specified - easier porting to other platforms later
        Dispatcher = dispatcher ?? new WpfDispatcherService();
        
        // Initialize settings with fallback to defaults on error
        Settings = new SettingsService();
        try
        {
            Settings.Load();
        }
        catch (Exception ex)
        {
            // Log error but continue with defaults - UI can still show
            LogService.Instance.Error("Failed to load settings, using defaults", ex, "ServiceContainer");
        }
        
        SteamLocator = new SteamLocator();
        LibraryScanner = new LibraryScanner(SteamLocator);
        GoldbergService = new GoldbergService();
        DlcService = new DlcService();
        PackageScanner = new PackageScanner(Settings);
        CacheService = new CacheService();
        
        // HTTP-based services (share cache and HttpClient)
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
    /// Call this when the application is shutting down.
    /// Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
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
        }
        catch (Exception ex)
        {
            // Log but don't throw during shutdown
            LogService.Instance.Debug($"Disposal error: {ex.Message}", "ServiceContainer");
        }
    }
}
