using System.Diagnostics;
using System.Windows.Input;
using SteamRoll.Models;
using SteamRoll.Services;

namespace SteamRoll.ViewModels;

/// <summary>
/// ViewModel for the game details view, managing state and commands for a single game.
/// </summary>
public class GameDetailsViewModel : ViewModelBase
{
    private readonly SteamStoreService? _storeService;
    private readonly CacheService? _cacheService;
    
    private InstalledGame? _game;
    private bool _isLoading;
    private string _loadingMessage = "";
    private PackageMode _selectedPackageMode = PackageMode.Goldberg;
    private SteamGameDetails? _storeDetails;

    public GameDetailsViewModel() : this(null, null) { }

    public GameDetailsViewModel(SteamStoreService? storeService, CacheService? cacheService)
    {
        _storeService = storeService;
        _cacheService = cacheService;

        // Initialize commands
        BackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
        PackageCommand = new RelayCommand(ExecutePackage, () => Game != null);
        OpenSteamStoreCommand = new RelayCommand(OpenSteamStore, () => Game != null);
        OpenSteamDbCommand = new RelayCommand(OpenSteamDb, () => Game != null);
        OpenInstallFolderCommand = new RelayCommand(OpenInstallFolder, () => Game != null);
        VerifyIntegrityCommand = new RelayCommand(() => VerifyIntegrityRequested?.Invoke(this, Game!), () => Game?.IsPackaged == true);
        SyncSavesCommand = new RelayCommand(() => SyncSavesRequested?.Invoke(this, Game!), () => Game?.IsPackaged == true);
        ToggleFavoriteCommand = new RelayCommand(ToggleFavorite, () => Game != null);
    }

    #region Properties

    /// <summary>
    /// The currently displayed game.
    /// </summary>
    public InstalledGame? Game
    {
        get => _game;
        set
        {
            if (SetProperty(ref _game, value))
            {
                OnPropertyChanged(nameof(IsPackaged));
                OnPropertyChanged(nameof(IsPackageable));
                OnPropertyChanged(nameof(PackageButtonText));
                OnPropertyChanged(nameof(HasDlc));
                OnPropertyChanged(nameof(GameTitle));
                OnPropertyChanged(nameof(AppId));
                OnPropertyChanged(nameof(FormattedSize));
                OnPropertyChanged(nameof(PrimaryDrm));
                OnPropertyChanged(nameof(CompatibilityScore));
                OnPropertyChanged(nameof(CompatibilityReason));
                OnPropertyChanged(nameof(HeaderImageUrl));
            }
        }
    }

    /// <summary>
    /// Whether the view is loading data.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    /// Loading message to display.
    /// </summary>
    public string LoadingMessage
    {
        get => _loadingMessage;
        set => SetProperty(ref _loadingMessage, value);
    }

    /// <summary>
    /// Selected package mode (Goldberg/CreamApi).
    /// </summary>
    public PackageMode SelectedPackageMode
    {
        get => _selectedPackageMode;
        set => SetProperty(ref _selectedPackageMode, value);
    }

    /// <summary>
    /// Fetched Steam Store details.
    /// </summary>
    public SteamGameDetails? StoreDetails
    {
        get => _storeDetails;
        set
        {
            if (SetProperty(ref _storeDetails, value))
            {
                OnPropertyChanged(nameof(Description));
                OnPropertyChanged(nameof(Genres));
                OnPropertyChanged(nameof(Developer));
                OnPropertyChanged(nameof(ReleaseDate));
                OnPropertyChanged(nameof(MetacriticScore));
                OnPropertyChanged(nameof(ReviewScore));
                OnPropertyChanged(nameof(Screenshots));
            }
        }
    }

    // Computed properties from Game
    public bool IsPackaged => Game?.IsPackaged ?? false;
    public bool IsPackageable => Game?.IsPackageable ?? false;
    public string PackageButtonText => IsPackaged ? "Open" : "Build";
    public bool HasDlc => Game?.HasDlc ?? false;
    public string GameTitle => Game?.Name ?? "";
    public int AppId => Game?.AppId ?? 0;
    public string FormattedSize => Game?.FormattedSize ?? "";
    public DrmType PrimaryDrm => Game?.PrimaryDrm ?? DrmType.None;
    public double CompatibilityScore => Game?.CompatibilityScore ?? 0;
    public string CompatibilityReason => Game?.CompatibilityReason ?? "";
    public string HeaderImageUrl => Game?.HeaderImageUrl ?? "";

    // Computed properties from StoreDetails
    public string Description => StoreDetails?.Description ?? "No description available.";
    public string Genres => StoreDetails?.GenresDisplay ?? "";
    public string Developer => StoreDetails?.DevelopersDisplay ?? "";
    public string ReleaseDate => StoreDetails?.ReleaseDate ?? "";
    public int? MetacriticScore => StoreDetails?.MetacriticScore;
    public int? ReviewScore => StoreDetails?.ReviewPositivePercent;
    public IReadOnlyList<string> Screenshots => StoreDetails?.Screenshots ?? [];

    #endregion

    #region Commands

    public ICommand BackCommand { get; }
    public ICommand PackageCommand { get; }
    public ICommand OpenSteamStoreCommand { get; }
    public ICommand OpenSteamDbCommand { get; }
    public ICommand OpenInstallFolderCommand { get; }
    public ICommand VerifyIntegrityCommand { get; }
    public ICommand SyncSavesCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }

    #endregion

    #region Events

    /// <summary>
    /// Raised when back navigation is requested.
    /// </summary>
    public event EventHandler? BackRequested;

    /// <summary>
    /// Raised when packaging is requested (View handles dialog).
    /// </summary>
    public event EventHandler<(InstalledGame Game, PackageMode Mode)>? PackageRequested;

    /// <summary>
    /// Raised when integrity verification is requested.
    /// </summary>
    public event EventHandler<InstalledGame>? VerifyIntegrityRequested;

    /// <summary>
    /// Raised when save sync is requested.
    /// </summary>
    public event EventHandler<InstalledGame>? SyncSavesRequested;

    #endregion

    #region Methods

    /// <summary>
    /// Loads game data and fetches Steam Store details.
    /// </summary>
    public async Task LoadGameAsync(InstalledGame game)
    {
        Game = game;
        IsLoading = true;
        LoadingMessage = "Loading game details...";

        try
        {
            if (_storeService != null)
            {
                StoreDetails = await _storeService.GetGameDetailsAsync(game.AppId);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to load store details: {ex.Message}", ex, "GameDetailsViewModel");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Refreshes package state after packaging completes.
    /// </summary>
    public void RefreshPackageState()
    {
        OnPropertyChanged(nameof(IsPackaged));
        OnPropertyChanged(nameof(PackageButtonText));
    }

    private void ExecutePackage()
    {
        if (Game == null) return;

        if (IsPackaged && !string.IsNullOrEmpty(Game.PackagePath))
        {
            // Open package folder
            try
            {
                Process.Start("explorer.exe", Game.PackagePath);
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Failed to open package folder: {ex.Message}", ex, "GameDetailsViewModel");
            }
        }
        else
        {
            // Request packaging
            PackageRequested?.Invoke(this, (Game, SelectedPackageMode));
        }
    }

    private void OpenSteamStore()
    {
        if (Game == null) return;
        OpenUrl($"https://store.steampowered.com/app/{Game.AppId}");
    }

    private void OpenSteamDb()
    {
        if (Game == null) return;
        OpenUrl($"https://steamdb.info/app/{Game.AppId}/");
    }

    private void OpenInstallFolder()
    {
        if (Game == null || string.IsNullOrEmpty(Game.FullPath)) return;
        try
        {
            Process.Start("explorer.exe", Game.FullPath);
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to open install folder: {ex.Message}", ex, "GameDetailsViewModel");
        }
    }

    private void ToggleFavorite()
    {
        if (Game == null) return;
        Game.IsFavorite = !Game.IsFavorite;
        _cacheService?.UpdateCache(Game);
        _cacheService?.SaveCache();
        OnPropertyChanged(nameof(Game));
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to open URL: {ex.Message}", ex, "GameDetailsViewModel");
        }
    }

    #endregion
}
