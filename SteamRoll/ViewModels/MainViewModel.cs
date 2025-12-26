using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using SteamRoll.Controls;
using SteamRoll.Models;
using SteamRoll.Services;
using SteamRoll.Services.Transfer;
using SteamRoll.ViewModels.Messages;

namespace SteamRoll.ViewModels;

/// <summary>
/// Represents the current view mode in the application.
/// </summary>
public enum ViewMode
{
    Library,
    Packages,
    Details,
    Transfers
}

/// <summary>
/// Main ViewModel for the application. Acts as composition root, coordinating sub-viewmodels:
/// - Library: filtering, sorting, scanning (LibraryViewModel)
/// - Network: peer discovery, mesh library (NetworkViewModel)  
/// - Packaging: package creation, batch operations (PackagingViewModel)
/// Manages view navigation, service coordination, and backward-compatible event forwarding.
/// </summary>
public class MainViewModel : ViewModelBase
{
    // Services
    private readonly ServiceContainer _services;
    private readonly SettingsService _settingsService;
    private readonly LibraryManager _libraryManager;
    private readonly TransferService _transferService;
    private readonly LanDiscoveryService _lanDiscoveryService;
    private readonly PackageBuilder _packageBuilder;
    private readonly UpdateService _updateService;
    private readonly GoldbergService _goldbergService;
    private readonly SaveGameService _saveGameService;
    private readonly IntegrityService _integrityService;
    private readonly CacheService _cacheService;
    private readonly PackageScanner _packageScanner;
    private readonly IDialogService _dialogService;
    private MeshLibraryService? _meshLibraryService;
    
    // Sub-ViewModels
    private readonly LibraryViewModel _libraryViewModel;
    private readonly NetworkViewModel _networkViewModel;
    private readonly PackagingViewModel _packagingViewModel;
    
    // Cancellation tokens
    private CancellationTokenSource? _currentOperationCts;
    private CancellationTokenSource? _scanCts;

    // State properties - Core
    private string _statusText = "";
    private bool _isLoading;
    private ViewMode _currentView = ViewMode.Library;
    private InstalledGame? _selectedGame;
    private string _outputPath = "";
    
    // State properties - Filters
    private string _searchText = "";
    private bool _isReadyChecked;
    private bool _isPackagedChecked;
    private bool _isDlcChecked;
    private bool _isUpdateChecked;
    private bool _isFavoritesChecked;
    private string _selectedSortType = "Name";
    
    // State properties - Loading
    private string _loadingMessage = "";
    private int _loadingProgress;
    private bool _isLibraryLoading;
    
    // State properties - Network
    private int _peerCount;

    // Phase I: Observable collection for data binding
    private ObservableCollection<InstalledGame> _filteredGames = new();
    public ObservableCollection<InstalledGame> FilteredGames
    {
        get => _filteredGames;
        set => SetProperty(ref _filteredGames, value);
    }

    /// <summary>
    /// Refreshes the FilteredGames collection from the current filter state.
    /// </summary>
    public void RefreshFilteredGames()
    {
        var games = GetFilteredGames();
        FilteredGames.Clear();
        foreach (var game in games)
        {
            FilteredGames.Add(game);
        }
    }

    /// <summary>
    /// Status bar text shown at bottom of window.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>
    /// Whether a loading operation is in progress.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    /// Current view mode (Library, Packages, Details, Transfers).
    /// </summary>
    public ViewMode CurrentView
    {
        get => _currentView;
        set
        {
            if (SetProperty(ref _currentView, value))
            {
                OnPropertyChanged(nameof(IsLibraryViewActive));
                OnPropertyChanged(nameof(IsPackagesViewActive));
                OnPropertyChanged(nameof(IsDetailsViewActive));
                OnPropertyChanged(nameof(IsTransfersViewActive));
            }
        }
    }

    /// <summary>
    /// Whether the library view is active.
    /// </summary>
    public bool IsLibraryViewActive => CurrentView == ViewMode.Library;

    /// <summary>
    /// Whether the packages view is active.
    /// </summary>
    public bool IsPackagesViewActive => CurrentView == ViewMode.Packages;

    /// <summary>
    /// Whether the details view is active.
    /// </summary>
    public bool IsDetailsViewActive => CurrentView == ViewMode.Details;

    /// <summary>
    /// Whether the transfers view is active.
    /// </summary>
    public bool IsTransfersViewActive => CurrentView == ViewMode.Transfers;

    /// <summary>
    /// Currently selected game for details view.
    /// </summary>
    public InstalledGame? SelectedGame
    {
        get => _selectedGame;
        set => SetProperty(ref _selectedGame, value);
    }

    /// <summary>
    /// Number of connected peers on the network.
    /// </summary>
    public int PeerCount
    {
        get => _peerCount;
        set
        {
            if (SetProperty(ref _peerCount, value))
                OnPropertyChanged(nameof(HasPeers));
        }
    }

    /// <summary>
    /// Whether any peers are connected.
    /// </summary>
    public bool HasPeers => PeerCount > 0;

    /// <summary>
    /// Output path for packaged games.
    /// </summary>
    public string OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
    }

    // ========================================
    // Filter State Properties (A2)
    // ========================================

    /// <summary>
    /// Search text for filtering games.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                OnPropertyChanged(nameof(IsSearchActive));
        }
    }

    /// <summary>
    /// Whether search is active.
    /// </summary>
    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    /// Filter for "Ready to package" games.
    /// </summary>
    public bool IsReadyChecked
    {
        get => _isReadyChecked;
        set => SetProperty(ref _isReadyChecked, value);
    }

    /// <summary>
    /// Filter for packaged games.
    /// </summary>
    public bool IsPackagedChecked
    {
        get => _isPackagedChecked;
        set => SetProperty(ref _isPackagedChecked, value);
    }

    /// <summary>
    /// Filter for games with DLC.
    /// </summary>
    public bool IsDlcChecked
    {
        get => _isDlcChecked;
        set => SetProperty(ref _isDlcChecked, value);
    }

    /// <summary>
    /// Filter for games with updates available.
    /// </summary>
    public bool IsUpdateChecked
    {
        get => _isUpdateChecked;
        set => SetProperty(ref _isUpdateChecked, value);
    }

    /// <summary>
    /// Filter for favorite games.
    /// </summary>
    public bool IsFavoritesChecked
    {
        get => _isFavoritesChecked;
        set => SetProperty(ref _isFavoritesChecked, value);
    }

    /// <summary>
    /// Selected sort type (Name, Size, LastPlayed, ReviewScore, ReleaseDate).
    /// </summary>
    public string SelectedSortType
    {
        get => _selectedSortType;
        set => SetProperty(ref _selectedSortType, value);
    }

    // ========================================
    // Loading State Properties (A3)
    // ========================================

    /// <summary>
    /// Message shown during loading operations.
    /// </summary>
    public string LoadingMessage
    {
        get => _loadingMessage;
        set => SetProperty(ref _loadingMessage, value);
    }

    /// <summary>
    /// Progress percentage (0-100) for loading operations.
    /// </summary>
    public int LoadingProgress
    {
        get => _loadingProgress;
        set => SetProperty(ref _loadingProgress, value);
    }

    /// <summary>
    /// Whether the library is currently being scanned/loaded.
    /// </summary>
    public bool IsLibraryLoading
    {
        get => _isLibraryLoading;
        set => SetProperty(ref _isLibraryLoading, value);
    }

    // ========================================
    // Network State Properties (A4)
    // ========================================

    /// <summary>
    /// Collection of discovered network peers.
    /// </summary>
    public ObservableCollection<PeerInfo> NetworkPeers { get; } = new();

    /// <summary>
    /// Gets the list of peers from the discovery service and updates the collection.
    /// Delegates to NetworkViewModel.
    /// </summary>
    public void RefreshNetworkPeers()
    {
        _networkViewModel.RefreshNetworkPeers();
        // Also sync MainViewModel's NetworkPeers collection for backward compatibility
        NetworkPeers.Clear();
        foreach (var peer in _networkViewModel.NetworkPeers)
        {
            NetworkPeers.Add(peer);
        }
    }

    /// <summary>
    /// Per-game Goldberg configuration (persisted in settings).
    /// </summary>
    public Dictionary<int, GoldbergConfig> GameGoldbergConfigs 
        => _settingsService.Settings.GameGoldbergConfigs;

    /// <summary>
    /// Access to library manager for game data.
    /// </summary>
    public LibraryManager LibraryManager => _libraryManager;

    /// <summary>
    /// Access to settings service.
    /// </summary>
    public SettingsService SettingsService => _settingsService;

    /// <summary>
    /// Access to transfer service.
    /// </summary>
    public TransferService TransferService => _transferService;

    /// <summary>
    /// Access to LAN discovery service.
    /// </summary>
    public LanDiscoveryService LanDiscoveryService => _lanDiscoveryService;

    /// <summary>
    /// Access to package builder.
    /// </summary>
    public PackageBuilder PackageBuilder => _packageBuilder;

    /// <summary>
    /// Access to Goldberg service.
    /// </summary>
    public GoldbergService GoldbergService => _goldbergService;

    /// <summary>
    /// Access to save game service.
    /// </summary>
    public SaveGameService SaveGameService => _saveGameService;

    /// <summary>
    /// Access to integrity service.
    /// </summary>
    public IntegrityService IntegrityService => _integrityService;

    /// <summary>
    /// Access to cache service.
    /// </summary>
    public CacheService CacheService => _cacheService;

    /// <summary>
    /// Access to mesh library service.
    /// </summary>
    public MeshLibraryService? MeshLibraryService => _meshLibraryService;

    /// <summary>
    /// Sub-ViewModel for library browsing, filtering, and scanning.
    /// </summary>
    public LibraryViewModel Library => _libraryViewModel;

    /// <summary>
    /// Sub-ViewModel for network peer discovery and management.
    /// </summary>
    public NetworkViewModel Network => _networkViewModel;

    /// <summary>
    /// Sub-ViewModel for game packaging operations.
    /// </summary>
    public PackagingViewModel Packaging => _packagingViewModel;

    // ========================================
    // Commands (Phase B)
    // ========================================

    // B1: Header Commands
    public ICommand RefreshCommand { get; }
    public ICommand OpenOutputCommand { get; }
    public ICommand CancelOperationCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ShowTransfersCommand { get; }
    public ICommand ImportPackageCommand { get; }

    // B2: View Navigation Commands
    public ICommand ShowLibraryViewCommand { get; }
    public ICommand ShowPackagesViewCommand { get; }
    public ICommand ShowDetailsViewCommand { get; }
    public ICommand BackToLibraryCommand { get; }
    public ICommand ToggleViewModeCommand { get; }

    // B3: Game Action Commands (parameter: InstalledGame)
    public ICommand PackageGameCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand OpenInstallFolderCommand { get; }
    public ICommand OpenSteamStoreCommand { get; }
    public ICommand DeletePackageCommand { get; }

    // B4: Save/Sync Commands (parameter: InstalledGame)
    public ICommand BackupSavesCommand { get; }
    public ICommand SyncSavesCommand { get; }

    // B5: Batch Commands
    public ICommand BatchPackageCommand { get; }
    public ICommand BatchSendToPeerCommand { get; }
    public ICommand ClearSelectionCommand { get; }

    public MainViewModel(ServiceContainer services, IDialogService? dialogService = null)
    {
        _services = services;
        _settingsService = services.Settings;
        _libraryManager = services.LibraryManager;
        _transferService = services.TransferService;
        _lanDiscoveryService = services.LanDiscoveryService;
        _packageBuilder = services.PackageBuilder;
        _updateService = services.UpdateService;
        _goldbergService = services.GoldbergService;
        _saveGameService = services.SaveGameService;
        _integrityService = services.IntegrityService;
        _cacheService = services.CacheService;
        _packageScanner = services.PackageScanner;
        _dialogService = dialogService ?? new DialogService();

        _outputPath = _settingsService.Settings.OutputPath;

        // Enable thread-safe collection access for UI-bound collections
        // This prevents cross-thread exceptions when peers are discovered/lost from background threads
        BindingOperations.EnableCollectionSynchronization(NetworkPeers, new object());
        BindingOperations.EnableCollectionSynchronization(FilteredGames, new object());

        // Initialize sub-ViewModels
        _libraryViewModel = new LibraryViewModel(_libraryManager, _settingsService, _cacheService, _packageScanner);
        
        // Sync LibraryViewModel status with main StatusText
        _libraryViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.StatusText))
                StatusText = _libraryViewModel.StatusText;
            if (e.PropertyName == nameof(LibraryViewModel.IsLoading))
                IsLoading = _libraryViewModel.IsLoading;
        };

        // Initialize NetworkViewModel
        _networkViewModel = new NetworkViewModel(_lanDiscoveryService, _transferService);
        
        // Subscribe to Messenger for decoupled peer notifications
        WeakReferenceMessenger.Default.Register<PeerDiscoveredMessage>(this, (r, m) =>
        {
            PeerDiscoveredNotification?.Invoke(this, m.Value);
        });
        WeakReferenceMessenger.Default.Register<PeerCountChangedMessage>(this, (r, m) =>
        {
            PeerCount = m.Value;
        });
        WeakReferenceMessenger.Default.Register<StatusTextChangedMessage>(this, (r, m) =>
        {
            StatusText = m.Value;
        });
        
        // Legacy event forwarding (keep for backward compatibility during transition)
        _networkViewModel.NetworkStatusChanged += (_, _) => NetworkStatusChanged?.Invoke(this, EventArgs.Empty);

        // Initialize PackagingViewModel
        _packagingViewModel = new PackagingViewModel(_packageBuilder, _settingsService, _cacheService, _libraryManager, _dialogService);
        
        // Sync PackagingViewModel with main status and events
        _packagingViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PackagingViewModel.StatusText))
                StatusText = _packagingViewModel.StatusText;
            if (e.PropertyName == nameof(PackagingViewModel.IsLoading))
                IsLoading = _packagingViewModel.IsLoading;
            if (e.PropertyName == nameof(PackagingViewModel.LoadingMessage))
                LoadingMessage = _packagingViewModel.LoadingMessage;
        };
        _packagingViewModel.LoadingStateChanged += (_, loading) => LoadingStateChanged?.Invoke(this, loading);
        _packagingViewModel.GamesListChanged += (_, _) => NotifyGamesListUpdated();

        // Initialize B1: Header Commands
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenOutputCommand = new RelayCommand(OpenOutput);
        CancelOperationCommand = new RelayCommand(CancelCurrentOperation, () => _currentOperationCts != null);
        SettingsCommand = new RelayCommand(OpenSettings);
        ShowTransfersCommand = new RelayCommand(ShowTransfers);
        ImportPackageCommand = new RelayCommand(ImportPackage);

        // Initialize B2: View Navigation Commands
        ShowLibraryViewCommand = new RelayCommand(() => CurrentView = ViewMode.Library);
        ShowPackagesViewCommand = new RelayCommand(() => CurrentView = ViewMode.Packages);
        ShowDetailsViewCommand = new RelayCommand<InstalledGame>(game => { if (game != null) { SelectedGame = game; CurrentView = ViewMode.Details; } });
        BackToLibraryCommand = new RelayCommand(() => CurrentView = IsLibraryViewActive ? ViewMode.Library : ViewMode.Packages);
        ToggleViewModeCommand = new RelayCommand(ToggleViewMode);

        // Initialize B3: Game Action Commands
        PackageGameCommand = new RelayCommand<InstalledGame>(game => PackageGameRequested?.Invoke(this, game!), game => game?.IsPackageable == true);
        ToggleFavoriteCommand = new RelayCommand<InstalledGame>(ToggleFavorite);
        OpenInstallFolderCommand = new RelayCommand<InstalledGame>(OpenInstallFolder);
        OpenSteamStoreCommand = new RelayCommand<InstalledGame>(OpenSteamStore);
        DeletePackageCommand = new RelayCommand<InstalledGame>(game => DeletePackageRequested?.Invoke(this, game!), game => game?.IsPackaged == true);

        // Initialize B4: Save/Sync Commands
        BackupSavesCommand = new RelayCommand<InstalledGame>(game => BackupSavesRequested?.Invoke(this, game!), game => game?.IsPackaged == true);
        SyncSavesCommand = new RelayCommand<InstalledGame>(game => SyncSavesRequested?.Invoke(this, game!), game => game?.IsPackaged == true);

        // Initialize B5: Batch Commands - Phase K: Call methods directly instead of raising events
        BatchPackageCommand = new RelayCommand(async () => await BatchPackageAsync(), HasSelectedGames);
        BatchSendToPeerCommand = new RelayCommand(async () => await ExecuteBatchSendToPeerAsync(), HasSelectedPackages);
        ClearSelectionCommand = new RelayCommand(ClearSelection);

        // Subscribe to service events
        // Note: Peer events now handled via Messenger from NetworkViewModel
        _libraryManager.ProgressChanged += status => StatusText = status;

        // Phase H: Transfer and Update service subscriptions
        _transferService.ProgressChanged += (_, progress) => OnTransferProgress(progress);
        _transferService.TransferComplete += async (_, result) => await OnTransferCompleteAsync(result);
        _transferService.LocalLibraryRequested += () => GetLocalGamesForSharing();
        _transferService.PullPackageRequested += async (gameName, ip, port) => await OnPullPackageRequestedAsync(gameName, ip, port);
        _updateService.UpdateAvailable += (_, args) => OnUpdateAvailable(args);
        _packageBuilder.ProgressChanged += (_, progress) => OnPackageProgress(progress);
    }

    // ========================================
    // Events for View-handled operations
    // ========================================
    
    /// <summary>
    /// Raised when a game should be packaged (handled by View for dialogs).
    /// </summary>
    public event EventHandler<InstalledGame>? PackageGameRequested;

    /// <summary>
    /// Raised when a package should be deleted (handled by View for confirmation).
    /// </summary>
    public event EventHandler<InstalledGame>? DeletePackageRequested;

    /// <summary>
    /// Raised when settings should be opened (handled by View for dialog).
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Raised when import package dialog should open.
    /// </summary>
    public event EventHandler? ImportPackageRequested;

    // B4: Save/Sync Events
    /// <summary>
    /// Raised when saves should be backed up (handled by View for file dialog).
    /// </summary>
    public event EventHandler<InstalledGame>? BackupSavesRequested;

    /// <summary>
    /// Raised when saves should be synced to peer (handled by View for peer selection).
    /// </summary>
    public event EventHandler<InstalledGame>? SyncSavesRequested;

    // B5: Batch Events - REMOVED (deprecated, commands now call methods directly)
    // BatchPackageRequested and BatchSendToPeerRequested events were removed
    // Use BatchPackageCommand and BatchSendToPeerCommand instead

    // B5: Batch Helpers
    /// <summary>
    /// Returns true if any games are selected for batch operations.
    /// </summary>
    private bool HasSelectedGames() => _libraryManager.Games.Any(g => g.IsSelected && g.IsPackageable);

    /// <summary>
    /// Returns true if any packaged games are selected for batch send.
    /// </summary>
    private bool HasSelectedPackages() => _libraryManager.Games.Any(g => g.IsSelected && g.IsPackaged);

    /// <summary>
    /// Clears selection on all games.
    /// </summary>
    private void ClearSelection()
    {
        foreach (var game in _libraryManager.Games)
        {
            game.IsSelected = false;
        }
    }

    /// <summary>
    /// Initializes the mesh library service after window is loaded.
    /// </summary>
    public void InitializeMeshLibrary()
    {
        _meshLibraryService = new MeshLibraryService(_lanDiscoveryService, _transferService);
    }

    /// <summary>
    /// Gets a snapshot of all games.
    /// </summary>
    public List<InstalledGame> GetGamesSnapshot() => _libraryManager.GetGamesSnapshot();

    /// <summary>
    /// Finds a game by AppId.
    /// </summary>
    public InstalledGame? FindGameByAppId(int appId) => _libraryManager.FindGameByAppId(appId);

    /// <summary>
    /// Finds a game by predicate.
    /// </summary>
    public InstalledGame? FindGame(Func<InstalledGame, bool> predicate) => _libraryManager.FindGame(predicate);

    /// <summary>
    /// Updates the peer count from the discovery service.
    /// </summary>
    public void UpdatePeerCount()
    {
        PeerCount = _lanDiscoveryService.GetPeers().Count;
    }

    /// <summary>
    /// Refreshes the game library or packages.
    /// </summary>
    private async Task RefreshAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        // Will be called by MainWindow which handles the actual scanning
        // This is a placeholder for future full migration
    }

    /// <summary>
    /// Opens the output folder in Explorer.
    /// </summary>
    private void OpenOutput()
    {
        if (!System.IO.Directory.Exists(OutputPath))
        {
            System.IO.Directory.CreateDirectory(OutputPath);
        }
        System.Diagnostics.Process.Start("explorer.exe", OutputPath);
    }

    /// <summary>
    /// Cancels the current operation.
    /// </summary>
    private void CancelCurrentOperation()
    {
        _currentOperationCts?.Cancel();
        StatusText = "⏳ Cancelling operation...";
    }

    /// <summary>
    /// Sets the current operation cancellation token.
    /// </summary>
    public void SetCurrentOperation(CancellationTokenSource cts)
    {
        _currentOperationCts = cts;
    }

    /// <summary>
    /// Clears the current operation.
    /// </summary>
    public void ClearCurrentOperation()
    {
        _currentOperationCts = null;
    }

    /// <summary>
    /// Saves settings to disk.
    /// </summary>
    public void SaveSettings()
    {
        _settingsService.Save();
    }

    // ========================================
    // Command Implementation Methods (Phase B)
    // ========================================

    /// <summary>
    /// Opens the settings dialog.
    /// </summary>
    private void OpenSettings()
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows the transfers view.
    /// </summary>
    private void ShowTransfers()
    {
        CurrentView = ViewMode.Transfers;
    }

    /// <summary>
    /// Opens the import package dialog.
    /// </summary>
    private void ImportPackage()
    {
        ImportPackageRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Toggles the view mode (list/grid).
    /// </summary>
    private bool _isListViewMode;
    public bool IsListViewMode
    {
        get => _isListViewMode;
        set => SetProperty(ref _isListViewMode, value);
    }

    private void ToggleViewMode()
    {
        IsListViewMode = !IsListViewMode;
    }

    /// <summary>
    /// Toggles favorite status for a game.
    /// </summary>
    private void ToggleFavorite(InstalledGame? game)
    {
        if (game == null) return;
        game.IsFavorite = !game.IsFavorite;
        _cacheService.UpdateCache(game);
        _cacheService.SaveCache();
    }

    /// <summary>
    /// Opens the install folder for a game.
    /// </summary>
    private void OpenInstallFolder(InstalledGame? game)
    {
        if (game == null || string.IsNullOrEmpty(game.FullPath)) return;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", game.FullPath);
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to open folder: {ex.Message}", ex, "ViewModel");
        }
    }

    /// <summary>
    /// Opens the Steam store page for a game.
    /// </summary>
    private void OpenSteamStore(InstalledGame? game)
    {
        if (game == null || game.AppId <= 0) return;
        var url = $"https://store.steampowered.com/app/{game.AppId}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to open Steam store: {ex.Message}", ex, "ViewModel");
        }
    }

    // ========================================
    // Phase C: Event Handlers
    // ========================================

    // C1: Network Events
    /// <summary>
    /// Raised when a peer is discovered (View should show toast).
    /// </summary>
    public event EventHandler<PeerInfo>? PeerDiscoveredNotification;

    /// <summary>
    /// Raised when network status should be updated in UI.
    /// </summary>
    public event EventHandler? NetworkStatusChanged;

    /// <summary>
    /// Handles peer discovered from LAN discovery service.
    /// </summary>
    public void HandlePeerDiscovered(PeerInfo peer)
    {
        UpdatePeerCount();
        RefreshNetworkPeers();
        StatusText = $"🔗 Found peer: {peer.HostName}";
        PeerDiscoveredNotification?.Invoke(this, peer);
        NetworkStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Handles peer lost from LAN discovery service.
    /// </summary>
    public void HandlePeerLost(PeerInfo peer)
    {
        UpdatePeerCount();
        RefreshNetworkPeers();
        NetworkStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // C2: Transfer Progress Events
    /// <summary>
    /// Raised when transfer progress should be displayed.
    /// </summary>
    public event EventHandler<TransferProgress>? TransferProgressUpdated;

    /// <summary>
    /// Raised when a transfer completes.
    /// </summary>
    public event EventHandler<TransferResult>? TransferCompleted;

    /// <summary>
    /// Handles transfer progress updates.
    /// </summary>
    public void HandleTransferProgress(TransferProgress progress)
    {
        var direction = progress.IsSending ? "📤 Sending" : "📥 Receiving";
        StatusText = $"{direction} {progress.GameName}: {progress.FormattedProgress}";
        TransferProgressUpdated?.Invoke(this, progress);
    }

    /// <summary>
    /// Handles transfer completion.
    /// </summary>
    public void HandleTransferComplete(TransferResult result)
    {
        if (result.Success)
        {
            StatusText = result.WasReceived
                ? $"✓ Received: {result.GameName}"
                : $"✓ Sent: {result.GameName}";
        }
        else
        {
            StatusText = $"⚠ Transfer failed: {result.GameName}";
        }
        TransferCompleted?.Invoke(this, result);
    }

    // C2: Package Progress Events
    /// <summary>
    /// Raised when package progress is updated.
    /// </summary>
    public event EventHandler<string>? PackageProgressUpdated;

    /// <summary>
    /// Handles package builder progress updates.
    /// </summary>
    public void HandlePackageProgress(string status)
    {
        StatusText = status;
        LoadingMessage = status;
        PackageProgressUpdated?.Invoke(this, status);
    }

    // C3: Library Manager already subscribed in constructor

    // ========================================
    // Phase D: Logic Extraction
    // ========================================

    // D1: Library Operations
    /// <summary>
    /// Gets filtered and sorted games based on current ViewModel state.
    /// </summary>
    public List<InstalledGame> GetFilteredGames()
    {
        var filtered = GetGamesSnapshot().AsEnumerable();

        // Apply search filter
        if (IsSearchActive)
        {
            filtered = filtered.Where(g =>
                g.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                g.AppId.ToString().Contains(SearchText));
        }

        // Apply toggle filters
        if (IsReadyChecked)
            filtered = filtered.Where(g => g.IsPackageable);

        if (IsPackagedChecked)
            filtered = filtered.Where(g => g.IsPackaged);

        if (IsDlcChecked)
            filtered = filtered.Where(g => g.HasDlc);

        if (IsUpdateChecked)
            filtered = filtered.Where(g => g.UpdateAvailable);

        if (IsFavoritesChecked)
            filtered = filtered.Where(g => g.IsFavorite);

        // Apply sorting with favorites pinning (if not filtering by favorites)
        var pinFavorites = !IsFavoritesChecked;

        filtered = SelectedSortType switch
        {
            "Size" => pinFavorites
                ? filtered.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.SizeOnDisk)
                : filtered.OrderByDescending(g => g.SizeOnDisk),
            "LastPlayed" => pinFavorites
                ? filtered.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.LastPlayed)
                : filtered.OrderByDescending(g => g.LastPlayed),
            "ReviewScore" => pinFavorites
                ? filtered.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.ReviewPositivePercent ?? 0)
                : filtered.OrderByDescending(g => g.ReviewPositivePercent ?? 0),
            "ReleaseDate" => pinFavorites
                ? filtered.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.BuildId)
                : filtered.OrderByDescending(g => g.BuildId),
            _ => pinFavorites
                ? filtered.OrderByDescending(g => g.IsFavorite).ThenBy(g => g.Name)
                : filtered.OrderBy(g => g.Name)
        };

        return filtered.ToList();
    }

    // Stats properties for D1
    private int _totalGames;
    private int _packageableCount;
    private int _packagedCount;
    private int _dlcCount;
    private int _updateCount;
    private long _totalSize;

    public int TotalGames { get => _totalGames; set => SetProperty(ref _totalGames, value); }
    public int PackageableCount { get => _packageableCount; set => SetProperty(ref _packageableCount, value); }
    public int PackagedCount { get => _packagedCount; set => SetProperty(ref _packagedCount, value); }
    public int DlcCount { get => _dlcCount; set => SetProperty(ref _dlcCount, value); }
    public int UpdateCount { get => _updateCount; set => SetProperty(ref _updateCount, value); }
    public long TotalSize { get => _totalSize; set => SetProperty(ref _totalSize, value); }

    /// <summary>
    /// Updates game statistics from current library.
    /// </summary>
    public void UpdateStats()
    {
        var games = GetGamesSnapshot();
        TotalGames = games.Count;
        PackageableCount = games.Count(g => g.IsPackageable);
        PackagedCount = games.Count(g => g.IsPackaged);
        DlcCount = games.Sum(g => g.TotalDlcCount);
        UpdateCount = games.Count(g => g.UpdateAvailable);
        TotalSize = games.Sum(g => g.SizeOnDisk);
    }

    // D2: Package Operation Helpers
    /// <summary>
    /// Gets games that are selected and packageable.
    /// </summary>
    public List<InstalledGame> GetSelectedPackageableGames()
        => _libraryManager.Games.Where(g => g.IsSelected && g.IsPackageable).ToList();

    /// <summary>
    /// Gets games that are selected and already packaged.
    /// </summary>
    public List<InstalledGame> GetSelectedPackagedGames()
        => _libraryManager.Games.Where(g => g.IsSelected && g.IsPackaged && !string.IsNullOrEmpty(g.PackagePath)).ToList();

    // D3: Network Operation Helpers
    /// <summary>
    /// Gets peers available for transfer.
    /// </summary>
    public List<PeerInfo> GetAvailablePeers() => _lanDiscoveryService.GetPeers();

    /// <summary>
    /// Checks if network operations are available.
    /// </summary>
    public bool HasAvailablePeers => _lanDiscoveryService.GetPeers().Count > 0;

    /// <summary>
    /// Raised when games list should be refreshed in UI.
    /// </summary>
    public event EventHandler<List<InstalledGame>>? GamesListUpdated;

    /// <summary>
    /// Notifies View to update games list with filtered results.
    /// Also updates the FilteredGames collection for data binding.
    /// </summary>
    public void NotifyGamesListUpdated()
    {
        var filtered = GetFilteredGames();
        
        // Update observable collection for binding
        FilteredGames.Clear();
        foreach (var game in filtered)
        {
            FilteredGames.Add(game);
        }
        
        // Also raise event for backward compatibility
        GamesListUpdated?.Invoke(this, filtered);
    }

    // ========================================
    // Phase G1: Scanning Logic
    // ========================================

    /// <summary>
    /// Raised when loading state changes for View to handle overlay.
    /// </summary>
    public event EventHandler<bool>? LoadingStateChanged;

    /// <summary>
    /// Scans the Steam library for installed games.
    /// Delegates to LibraryViewModel for the actual implementation.
    /// </summary>
    public async Task ScanLibraryAsync(CancellationToken ct = default)
    {
        LoadingStateChanged?.Invoke(this, true);
        try
        {
            await _libraryViewModel.ScanLibraryAsync(ct);
            // Also update MainViewModel's FilteredGames for backward compatibility
            NotifyGamesListUpdated();
            UpdateStats();
        }
        finally
        {
            LoadingStateChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Scans for packaged games in the output directory.
    /// Delegates to LibraryViewModel for the actual implementation.
    /// </summary>
    public async Task ScanPackagesAsync(CancellationToken ct = default)
    {
        LoadingStateChanged?.Invoke(this, true);
        try
        {
            await _libraryViewModel.ScanPackagesAsync(ct);
            // Also update MainViewModel's FilteredGames for backward compatibility
            NotifyGamesListUpdated();
            UpdateStats();
        }
        finally
        {
            LoadingStateChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Raised when a game list refresh should occur (for UI to call ApplyFilters).
    /// </summary>
    public event EventHandler? RefreshGamesRequested;

    /// <summary>
    /// Requests a refresh of the games display.
    /// </summary>
    public void RequestGamesRefresh()
    {
        RefreshGamesRequested?.Invoke(this, EventArgs.Empty);
        NotifyGamesListUpdated();
    }

    // ========================================
    // Phase G2: Packaging Logic
    // ========================================

    /// <summary>
    /// Dictionary storing Goldberg configurations per game.
    /// </summary>
    private readonly Dictionary<int, GoldbergConfig> _gameGoldbergConfigs = new();

    /// <summary>
    /// Creates a package for a game.
    /// </summary>
    public async Task CreatePackageAsync(InstalledGame game, PackageMode mode, PackageState? resumeState = null)
    {
        // Create cancellation token for this operation  
        _currentOperationCts?.Cancel();
        _currentOperationCts?.Dispose();
        _currentOperationCts = new CancellationTokenSource();
        var ct = _currentOperationCts.Token;

        var isUpdate = game.IsPackaged && game.UpdateAvailable && resumeState == null;
        var actionText = isUpdate ? "Updating package for" : "Packaging";

        IsLoading = true;
        LoadingMessage = $"{actionText} {game.Name}... (Press ESC to cancel)";
        LoadingStateChanged?.Invoke(this, true);
        StatusText = $"⏳ {actionText} {game.Name}...";

        try
        {
            // Look up any stored Goldberg config for this game
            _gameGoldbergConfigs.TryGetValue(game.AppId, out var goldbergConfig);

            // Create package options with specified mode
            var options = new PackageOptions
            {
                Mode = mode,
                IncludeDlc = true,
                GoldbergConfig = goldbergConfig,
                IsUpdate = isUpdate
            };

            // Start packaging process with optional resume state
            var packagePath = await _packageBuilder.CreatePackageAsync(game, _outputPath, options, ct, resumeState);

            // Update game status
            game.IsPackaged = true;
            game.PackagePath = packagePath;
            game.PackageBuildId = game.BuildId;
            game.LastPackaged = DateTime.Now;

            _cacheService.UpdateCache(game);
            _cacheService.SaveCache();

            NotifyGamesListUpdated();
            ToastService.Instance.ShowSuccess("Packaging Complete", $"Successfully packaged {game.Name}!");
            StatusText = $"✓ Packaged {game.Name}";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"⚠ Packaging cancelled for {game.Name}";
            ToastService.Instance.ShowWarning("Packaging Cancelled", $"{game.Name} packaging was cancelled.");
        }
        catch (Exception ex)
        {
            StatusText = $"⚠ Failed to package {game.Name}: {ex.Message}";
            ToastService.Instance.ShowError("Package Failed", ex.Message);
        }
        finally
        {
            IsLoading = false;
            LoadingStateChanged?.Invoke(this, false);
            _currentOperationCts = null;
        }
    }

    /// <summary>
    /// Sets Goldberg configuration for a game.
    /// </summary>
    public void SetGoldbergConfig(int appId, GoldbergConfig config)
    {
        _gameGoldbergConfigs[appId] = config;
        SaveSettings();
    }

    /// <summary>
    /// Gets Goldberg configuration for a game.
    /// </summary>
    public GoldbergConfig? GetGoldbergConfig(int appId)
    {
        _gameGoldbergConfigs.TryGetValue(appId, out var config);
        return config;
    }

    // ========================================
    // Phase G4: Batch Operations
    // ========================================

    /// <summary>
    /// Packages multiple selected games.
    /// </summary>
    public async Task BatchPackageAsync()
    {
        var selectedGames = _libraryManager.Games.Where(g => g.IsSelected && g.IsPackageable).ToList();

        if (selectedGames.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Games Selected", "Please select packageable games first.");
            return;
        }

        // Raise event for View to show confirmation dialog
        var proceed = await RequestConfirmationAsync(
            "Batch Package", 
            $"Package {selectedGames.Count} game{(selectedGames.Count > 1 ? "s" : "")}?\n\nThis may take a while depending on game sizes.");
        
        if (!proceed) return;

        var successCount = 0;
        var failCount = 0;

        try
        {
            for (int i = 0; i < selectedGames.Count; i++)
            {
                var game = selectedGames[i];
                StatusText = $"📦 Packaging {i + 1}/{selectedGames.Count}: {game.Name}";

                try
                {
                    var mode = _settingsService.Settings.DefaultPackageMode;
                    await CreatePackageAsync(game, mode);
                    successCount++;
                    game.IsSelected = false;
                }
                catch (Exception ex)
                {
                    LogService.Instance.Error($"Batch package failed for {game.Name}", ex, "Batch");
                    failCount++;
                }
            }

            if (failCount == 0)
            {
                ToastService.Instance.ShowSuccess("Batch Complete", $"Successfully packaged {successCount} game{(successCount > 1 ? "s" : "")}.");
            }
            else
            {
                ToastService.Instance.ShowWarning("Batch Complete", $"Packaged {successCount}, failed {failCount}. Check logs for details.");
            }

            StatusText = $"✓ Batch packaging complete: {successCount} succeeded, {failCount} failed";
        }
        finally
        {
            NotifyGamesListUpdated();
        }
    }

    /// <summary>
    /// Sends multiple selected packages to a peer.
    /// </summary>
    public async Task BatchSendToPeerAsync(PeerInfo targetPeer)
    {
        var selectedGames = _libraryManager.Games.Where(g => g.IsSelected && g.IsPackaged && !string.IsNullOrEmpty(g.PackagePath)).ToList();

        if (selectedGames.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Packages Selected", "Please select packaged games to send.");
            return;
        }

        var totalSize = selectedGames.Sum(g => g.SizeOnDisk);
        var proceed = await RequestConfirmationAsync(
            "Confirm Batch Transfer",
            $"Send {selectedGames.Count} package{(selectedGames.Count > 1 ? "s" : "")} to {targetPeer.HostName}?\n\nTotal size: ~{totalSize / (1024 * 1024 * 1024.0):F1} GB");

        if (!proceed) return;

        var successCount = 0;
        var failCount = 0;

        try
        {
            for (int i = 0; i < selectedGames.Count; i++)
            {
                var game = selectedGames[i];
                StatusText = $"📤 Sending {i + 1}/{selectedGames.Count}: {game.Name} to {targetPeer.HostName}...";

                try
                {
                    var success = await _transferService.SendPackageAsync(
                        targetPeer.IpAddress,
                        targetPeer.TransferPort,
                        game.PackagePath!);

                    if (success)
                    {
                        successCount++;
                        game.IsSelected = false;
                    }
                    else
                    {
                        failCount++;
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Error($"Batch transfer failed for {game.Name}", ex, "BatchTransfer");
                    failCount++;
                }
            }

            if (failCount == 0)
            {
                ToastService.Instance.ShowSuccess("Batch Transfer Complete", $"Successfully sent {successCount} package{(successCount > 1 ? "s" : "")} to {targetPeer.HostName}.");
            }
            else
            {
                ToastService.Instance.ShowWarning("Batch Transfer Complete", $"Sent {successCount}, failed {failCount}. Check logs for details.");
            }

            StatusText = $"✓ Batch transfer complete: {successCount} sent, {failCount} failed";
        }
        finally
        {
            NotifyGamesListUpdated();
        }
    }

    /// <summary>
    /// Executes batch send to peer with dialog-based peer selection.
    /// </summary>
    private async Task ExecuteBatchSendToPeerAsync()
    {
        var peers = _lanDiscoveryService.GetPeers();
        if (peers.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Peers Available", "No peers found on the network.");
            return;
        }

        var selectedPeer = _dialogService.SelectPeer(peers);
        if (selectedPeer == null)
        {
            return; // User cancelled
        }

        await BatchSendToPeerAsync(selectedPeer);
    }

    /// <summary>
    /// Event for requesting user confirmation (View provides callback).
    /// </summary>
    [Obsolete("Use IDialogService.ShowConfirmationAsync() instead.")]
    public event Func<string, string, Task<bool>>? ConfirmationRequested;

    private async Task<bool> RequestConfirmationAsync(string title, string message)
    {
        // Prefer dialog service if available
        if (_dialogService != null)
        {
            return await _dialogService.ShowConfirmationAsync(title, message);
        }
        
        // Fall back to event for backward compatibility
        if (ConfirmationRequested != null)
        {
            return await ConfirmationRequested(title, message);
        }
        
        return true; // Default to proceed if no handler
    }

    // ========================================
    // Phase G3: Network Logic
    // ========================================

    /// <summary>
    /// Tracks active transfer IDs for TransferManager.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Guid> _activeTransferIds = new();

    /// <summary>
    /// Handles transfer progress updates.
    /// </summary>
    public void OnTransferProgress(TransferProgress progress)
    {
        var direction = progress.IsSending ? "📤 Sending" : "📥 Receiving";
        StatusText = $"{direction} {progress.GameName}: {progress.FormattedProgress}";

        // Track in TransferManager
        var key = $"{progress.GameName}_{progress.IsSending}";
        if (!_activeTransferIds.TryGetValue(key, out var transferId))
        {
            // Start tracking this transfer
            var game = GetGamesSnapshot().FirstOrDefault(g =>
                g.Name.Equals(progress.GameName, StringComparison.OrdinalIgnoreCase));
            var appId = game?.AppId ?? 0;

            var transferInfo = TransferManager.Instance.StartTransfer(
                progress.GameName,
                progress.TotalBytes,
                1,
                progress.IsSending,
                appId
            );
            _activeTransferIds[key] = transferInfo.Id;
            transferId = transferInfo.Id;
        }

        // Update progress
        TransferManager.Instance.UpdateProgress(transferId, progress.BytesTransferred, 0);
    }

    /// <summary>
    /// Handles transfer completion.
    /// </summary>
    public async Task OnTransferCompleteAsync(TransferResult result)
    {
        // Complete transfer tracking
        var key = $"{result.GameName}_{!result.WasReceived}";
        if (_activeTransferIds.TryRemove(key, out var transferId))
        {
            TransferManager.Instance.CompleteTransfer(transferId, result.Success, result.Success ? null : "Transfer failed");
        }

        if (result.Success)
        {
            var action = result.WasReceived ? "received" : "sent";

            if (result.WasReceived)
            {
                if (result.IsSaveSync)
                {
                    // Handle save sync - request confirmation
                    var game = GetGamesSnapshot().FirstOrDefault(g => g.Name.Equals(result.GameName, StringComparison.OrdinalIgnoreCase));
                    if (game != null)
                    {
                        var confirm = await RequestConfirmationAsync(
                            "Save Sync Received",
                            $"Received updated saves for {game.Name}. Overwrite local saves?");

                        if (confirm)
                        {
                            try
                            {
                                await _saveGameService.RestoreSavesAsync(result.Path, game.AppId, game.PackagePath);
                                StatusText = $"✓ Synced saves for {result.GameName}";
                                ToastService.Instance.ShowSuccess("Save Sync", "Local saves updated successfully.");
                            }
                            catch (Exception ex)
                            {
                                LogService.Instance.Error($"Failed to restore saves: {ex.Message}", ex, "SaveSync");
                                ToastService.Instance.ShowError("Save Restore Failed", ex.Message);
                            }
                        }
                        else
                        {
                            StatusText = $"⚠ Save sync skipped for {result.GameName}";
                        }
                    }
                    return;
                }

                if (result.VerificationPassed)
                {
                    StatusText = $"✓ Successfully {action}: {result.GameName} (Verified ✓)";
                    ToastService.Instance.ShowSuccess("Transfer Complete",
                        $"Successfully {action} {result.GameName}\n✓ Package integrity verified");
                }
                else
                {
                    var errorSummary = result.VerificationErrors.Count > 0
                        ? string.Join(", ", result.VerificationErrors.Take(3))
                        : "Unknown verification error";
                    StatusText = $"⚠ {action}: {result.GameName} (Verification failed)";
                    ToastService.Instance.ShowWarning("Transfer Complete - Verification Failed",
                        $"{result.GameName} was received but verification failed:\n{errorSummary}");
                }

                // Trigger package rescan
                _scanCts?.Cancel();
                _scanCts = new CancellationTokenSource();
                await ScanPackagesAsync(_scanCts.Token);
            }
            else
            {
                StatusText = $"✓ Successfully {action}: {result.GameName}";
                ToastService.Instance.ShowSuccess("Transfer Complete", $"Successfully {action} {result.GameName}");
            }
        }
        else
        {
            StatusText = $"⚠ Transfer failed: {result.GameName}";
            ToastService.Instance.ShowError("Transfer Failed", $"Failed to transfer {result.GameName}");
        }
    }

    /// <summary>
    /// Gets the local library for sharing with peers.
    /// </summary>
    public List<RemoteGame> GetLocalGamesForSharing()
    {
        return GetGamesSnapshot().Where(g => g.IsPackaged && !string.IsNullOrEmpty(g.PackagePath))
            .Select(g => new RemoteGame { Name = g.Name, SizeBytes = g.SizeOnDisk })
            .ToList();
    }

    /// <summary>
    /// Installs a game from a peer.
    /// </summary>
    public async Task InstallFromPeerAsync(InstalledGame game)
    {
        if (!game.IsNetworkAvailable) return;

        try
        {
            var peerWithGame = _meshLibraryService?.GetPeersWithGame(game.AppId).FirstOrDefault();
            if (peerWithGame == null)
            {
                ToastService.Instance.ShowWarning("No Peers Available", $"No peers currently have {game.Name} available.");
                return;
            }

            StatusText = $"📡 Requesting {game.Name} from {peerWithGame.PeerHostName}...";
            ToastService.Instance.ShowInfo("Transfer Starting", $"Requesting {game.Name} from {peerWithGame.PeerHostName}");

            await _lanDiscoveryService.SendTransferRequestAsync(
                new PeerInfo
                {
                    IpAddress = peerWithGame.PeerIp,
                    TransferPort = peerWithGame.PeerPort,
                    HostName = peerWithGame.PeerHostName
                },
                game.Name,
                peerWithGame.SizeBytes);

            LogService.Instance.Info($"Requested game transfer: {game.Name} from {peerWithGame.PeerHostName}", "MainViewModel");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to request game from peer: {ex.Message}", ex, "MainViewModel");
            ToastService.Instance.ShowError("Transfer Failed", $"Could not request {game.Name} from peer.");
        }
    }

    /// <summary>
    /// Sends a package when requested by a peer.
    /// </summary>
    public async Task OnPullPackageRequestedAsync(string gameName, string targetIp, int targetPort)
    {
        var game = FindGame(g => g.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase) && g.IsPackaged);

        if (game != null && !string.IsNullOrEmpty(game.PackagePath))
        {
            StatusText = $"📤 Sending requested package {game.Name}...";
            ToastService.Instance.ShowInfo("Transfer Started", $"Sending {game.Name} (Requested by peer)");
            await _transferService.SendPackageAsync(targetIp, targetPort, game.PackagePath);
        }
        else
        {
            LogService.Instance.Warning($"Peer requested unknown/unpackaged game: {gameName}", "MainViewModel");
        }
    }

    // ========================================
    // Phase H: Update and Package Progress Handlers
    // ========================================

    /// <summary>
    /// Whether an application update is available.
    /// </summary>
    private bool _isUpdateAvailable;
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set => SetProperty(ref _isUpdateAvailable, value);
    }

    /// <summary>
    /// The available update version string.
    /// </summary>
    private string _availableVersion = "";
    public string AvailableVersion
    {
        get => _availableVersion;
        set => SetProperty(ref _availableVersion, value);
    }

    /// <summary>
    /// Current package progress (0-100).
    /// </summary>
    private int _packageProgress;
    public int PackageProgress
    {
        get => _packageProgress;
        set => SetProperty(ref _packageProgress, value);
    }

    /// <summary>
    /// Handles update available notifications.
    /// </summary>
    private void OnUpdateAvailable(UpdateAvailableEventArgs args)
    {
        IsUpdateAvailable = true;
        AvailableVersion = args.Update.LatestVersion;
        StatusText = $"🔄 Update available: v{args.Update.LatestVersion}";
        ToastService.Instance.ShowInfo("Update Available", $"Version {args.Update.LatestVersion} is available.");
    }

    /// <summary>
    /// Handles package progress updates.
    /// </summary>
    private void OnPackageProgress(int progressPercent)
    {
        PackageProgress = progressPercent;
        if (progressPercent < 100)
        {
            StatusText = $"📦 Packaging: {progressPercent}%";
        }
    }
}

