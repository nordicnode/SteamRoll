using System.Collections.ObjectModel;
using System.Windows.Data;
using SteamRoll.Models;
using SteamRoll.Services;

namespace SteamRoll.ViewModels;

/// <summary>
/// ViewModel for library browsing, filtering, sorting, and scanning operations.
/// Extracted from MainViewModel to improve separation of concerns.
/// </summary>
public class LibraryViewModel : ViewModelBase
{
    private readonly LibraryManager _libraryManager;
    private readonly SettingsService _settingsService;
    private readonly CacheService _cacheService;
    private readonly PackageScanner _packageScanner;
    
    // Filter state
    private string _searchText = "";
    private bool _isReadyChecked;
    private bool _isPackagedChecked;
    private bool _isDlcChecked;
    private bool _isUpdateChecked;
    private bool _isFavoritesChecked;
    private string _selectedSortType = "Name";
    
    // Stats
    private int _totalGames;
    private int _packageableCount;
    private int _packagedCount;
    private int _dlcCount;
    private int _updateCount;
    private long _totalSize;
    
    // Loading state
    private bool _isLoading;
    private string _statusText = "";

    /// <summary>
    /// Observable collection of filtered games for data binding.
    /// </summary>
    public ObservableCollection<InstalledGame> FilteredGames { get; } = new();

    public LibraryViewModel(
        LibraryManager libraryManager,
        SettingsService settingsService,
        CacheService cacheService,
        PackageScanner packageScanner)
    {
        _libraryManager = libraryManager;
        _settingsService = settingsService;
        _cacheService = cacheService;
        _packageScanner = packageScanner;
        
        // Enable thread-safe collection access
        BindingOperations.EnableCollectionSynchronization(FilteredGames, new object());
    }

    // ========================================
    // Filter Properties
    // ========================================

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnPropertyChanged(nameof(IsSearchActive));
                RefreshFilteredGames();
            }
        }
    }

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchText);

    public bool IsReadyChecked
    {
        get => _isReadyChecked;
        set { if (SetProperty(ref _isReadyChecked, value)) RefreshFilteredGames(); }
    }

    public bool IsPackagedChecked
    {
        get => _isPackagedChecked;
        set { if (SetProperty(ref _isPackagedChecked, value)) RefreshFilteredGames(); }
    }

    public bool IsDlcChecked
    {
        get => _isDlcChecked;
        set { if (SetProperty(ref _isDlcChecked, value)) RefreshFilteredGames(); }
    }

    public bool IsUpdateChecked
    {
        get => _isUpdateChecked;
        set { if (SetProperty(ref _isUpdateChecked, value)) RefreshFilteredGames(); }
    }

    public bool IsFavoritesChecked
    {
        get => _isFavoritesChecked;
        set { if (SetProperty(ref _isFavoritesChecked, value)) RefreshFilteredGames(); }
    }

    public string SelectedSortType
    {
        get => _selectedSortType;
        set { if (SetProperty(ref _selectedSortType, value)) RefreshFilteredGames(); }
    }

    // ========================================
    // Stats Properties
    // ========================================

    public int TotalGames { get => _totalGames; set => SetProperty(ref _totalGames, value); }
    public int PackageableCount { get => _packageableCount; set => SetProperty(ref _packageableCount, value); }
    public int PackagedCount { get => _packagedCount; set => SetProperty(ref _packagedCount, value); }
    public int DlcCount { get => _dlcCount; set => SetProperty(ref _dlcCount, value); }
    public int UpdateCount { get => _updateCount; set => SetProperty(ref _updateCount, value); }
    public long TotalSize { get => _totalSize; set => SetProperty(ref _totalSize, value); }

    // ========================================
    // Loading State
    // ========================================

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>
    /// Raised when loading state changes (for View overlay).
    /// </summary>
    public event EventHandler<bool>? LoadingStateChanged;

    /// <summary>
    /// Raised when games list is updated.
    /// </summary>
    public event EventHandler<List<InstalledGame>>? GamesListUpdated;

    // ========================================
    // Core Methods
    // ========================================

    /// <summary>
    /// Gets filtered and sorted games based on current filter state.
    /// </summary>
    public List<InstalledGame> GetFilteredGames()
    {
        var filtered = _libraryManager.GetGamesSnapshot().AsEnumerable();

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

        // Apply sorting with favorites pinning
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
        GamesListUpdated?.Invoke(this, games);
    }

    /// <summary>
    /// Updates game statistics from current library.
    /// </summary>
    public void UpdateStats()
    {
        var games = _libraryManager.GetGamesSnapshot();
        TotalGames = games.Count;
        PackageableCount = games.Count(g => g.IsPackageable);
        PackagedCount = games.Count(g => g.IsPackaged);
        DlcCount = games.Sum(g => g.TotalDlcCount);
        UpdateCount = games.Count(g => g.UpdateAvailable);
        TotalSize = games.Sum(g => g.SizeOnDisk);
    }

    /// <summary>
    /// Scans the Steam library for installed games.
    /// </summary>
    public async Task ScanLibraryAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        LoadingStateChanged?.Invoke(this, true);

        var steamPath = _libraryManager.GetSteamPath();
        if (steamPath == null)
        {
            IsLoading = false;
            LoadingStateChanged?.Invoke(this, false);
            StatusText = "⚠ Steam installation not found. Please ensure Steam is installed.";
            ToastService.Instance.ShowError("Steam Not Found", "Please ensure Steam is installed.");
            return;
        }

        try
        {
            var scanResult = await _libraryManager.ScanLibrariesAsync(ct);
            ct.ThrowIfCancellationRequested();

            if (scanResult.GamesToAnalyze.Count > 0)
            {
                await _libraryManager.AnalyzeGamesForDrmAsync(scanResult.GamesToAnalyze, ct);
            }
            ct.ThrowIfCancellationRequested();

            _libraryManager.CheckExistingPackages(scanResult.AllGames);

            RefreshFilteredGames();
            UpdateStats();

            _libraryManager.SaveCache(scanResult.AllGames);

            var gamesNeedingDlc = _libraryManager.GetGamesNeedingDlc();
            if (gamesNeedingDlc.Count > 0)
            {
                StatusText = $"Fetching DLC for {gamesNeedingDlc.Count} games...";
                _ = _libraryManager.FetchDlcForGamesAsync(gamesNeedingDlc, ct);
            }

            _ = _libraryManager.EnrichWithStoreDataAsync(_libraryManager.GetGamesSnapshot(), ct);
            _ = _libraryManager.ResolveGameImagesAsync(_libraryManager.GetGamesSnapshot(), ct);

            var packageableCount = scanResult.AllGames.Count(g => g.IsPackageable);
            var packagedCount = scanResult.AllGames.Count(g => g.IsPackaged);
            StatusText = $"✓ {scanResult.AllGames.Count} games • {packageableCount} ready • {packagedCount} packaged";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"⚠ Error scanning library: {ex.Message}";
            ToastService.Instance.ShowError("Scan Failed", ex.Message);
        }
        finally
        {
            IsLoading = false;
            LoadingStateChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Scans for packaged games in the output directory.
    /// </summary>
    public async Task ScanPackagesAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        LoadingStateChanged?.Invoke(this, true);
        StatusText = "Scanning packages...";

        try
        {
            var packages = await Task.Run(() => _packageScanner.ScanPackages(ct), ct);
            ct.ThrowIfCancellationRequested();

            RefreshFilteredGames();
            UpdateStats();

            StatusText = $"✓ Found {packages.Count} packages";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"⚠ Error scanning packages: {ex.Message}";
            ToastService.Instance.ShowError("Scan Failed", ex.Message);
        }
        finally
        {
            IsLoading = false;
            LoadingStateChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Toggles favorite status for a game.
    /// </summary>
    public void ToggleFavorite(InstalledGame? game)
    {
        if (game == null) return;
        game.IsFavorite = !game.IsFavorite;
        _cacheService.UpdateCache(game);
        _cacheService.SaveCache();
        RefreshFilteredGames();
    }

    /// <summary>
    /// Gets games that are selected and packageable.
    /// </summary>
    public List<InstalledGame> GetSelectedPackageableGames()
        => _libraryManager.Games.Where(g => g.IsSelected && g.IsPackageable).ToList();

    /// <summary>
    /// Gets games that are selected and already packaged.
    /// </summary>
    public List<InstalledGame> GetSelectedPackagedGames()
        => _libraryManager.Games.Where(g => g.IsSelected && g.IsPackaged).ToList();

    /// <summary>
    /// Clears selection on all games.
    /// </summary>
    public void ClearSelection()
    {
        foreach (var game in _libraryManager.Games)
        {
            game.IsSelected = false;
        }
    }

    /// <summary>
    /// Finds a game by AppId.
    /// </summary>
    public InstalledGame? FindGameByAppId(int appId) => _libraryManager.FindGameByAppId(appId);

    /// <summary>
    /// Gets a snapshot of all games.
    /// </summary>
    public List<InstalledGame> GetGamesSnapshot() => _libraryManager.GetGamesSnapshot();
}
