using SteamRoll.Models;
using SteamRoll.Services;

namespace SteamRoll.ViewModels;

/// <summary>
/// ViewModel for game packaging operations.
/// Extracted from MainViewModel to improve separation of concerns.
/// </summary>
public class PackagingViewModel : ViewModelBase
{
    private readonly PackageBuilder _packageBuilder;
    private readonly SettingsService _settingsService;
    private readonly CacheService _cacheService;
    private readonly LibraryManager _libraryManager;
    private readonly IDialogService _dialogService;
    
    private readonly Dictionary<int, GoldbergConfig> _gameGoldbergConfigs = new();
    private CancellationTokenSource? _currentOperationCts;
    
    private bool _isLoading;
    private string _statusText = "";
    private string _loadingMessage = "";

    public PackagingViewModel(
        PackageBuilder packageBuilder,
        SettingsService settingsService,
        CacheService cacheService,
        LibraryManager libraryManager,
        IDialogService dialogService)
    {
        _packageBuilder = packageBuilder;
        _settingsService = settingsService;
        _cacheService = cacheService;
        _libraryManager = libraryManager;
        _dialogService = dialogService;
        
        // Subscribe to package progress
        _packageBuilder.ProgressChanged += (status, percentage) => OnPackageProgress(status, percentage);
    }

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

    public string LoadingMessage
    {
        get => _loadingMessage;
        set => SetProperty(ref _loadingMessage, value);
    }

    /// <summary>
    /// Raised when loading state changes.
    /// </summary>
    public event EventHandler<bool>? LoadingStateChanged;

    /// <summary>
    /// Raised when package progress is updated.
    /// </summary>
    public event EventHandler<string>? PackageProgressUpdated;

    /// <summary>
    /// Raised when games list should be updated after packaging.
    /// </summary>
    public event EventHandler? GamesListChanged;

    /// <summary>
    /// Gets Goldberg configuration for a game.
    /// </summary>
    public GoldbergConfig? GetGoldbergConfig(int appId)
    {
        _gameGoldbergConfigs.TryGetValue(appId, out var config);
        return config;
    }

    /// <summary>
    /// Sets Goldberg configuration for a game.
    /// </summary>
    public void SetGoldbergConfig(int appId, GoldbergConfig config)
    {
        _gameGoldbergConfigs[appId] = config;
        _settingsService.Save();
    }

    /// <summary>
    /// Creates a package for a game.
    /// </summary>
    public async Task CreatePackageAsync(InstalledGame game, PackageMode mode, string outputPath, PackageState? resumeState = null)
    {
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
            _gameGoldbergConfigs.TryGetValue(game.AppId, out var goldbergConfig);

            var options = new PackageOptions
            {
                Mode = mode,
                IncludeDlc = true,
                GoldbergConfig = goldbergConfig,
                IsUpdate = isUpdate
            };

            var packagePath = await _packageBuilder.CreatePackageAsync(game, outputPath, options, ct, resumeState);

            game.IsPackaged = true;
            game.PackagePath = packagePath;
            game.PackageBuildId = game.BuildId;
            game.LastPackaged = DateTime.Now;

            _cacheService.UpdateCache(game);
            _cacheService.SaveCache();

            GamesListChanged?.Invoke(this, EventArgs.Empty);
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
    /// Packages multiple selected games.
    /// </summary>
    public async Task BatchPackageAsync(string outputPath)
    {
        var selectedGames = _libraryManager.Games.Where(g => g.IsSelected && g.IsPackageable).ToList();

        if (selectedGames.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Games Selected", "Please select packageable games first.");
            return;
        }

        var proceed = await _dialogService.ShowConfirmationAsync(
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
                    await CreatePackageAsync(game, mode, outputPath);
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
            GamesListChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Cancels the current packaging operation.
    /// </summary>
    public void CancelCurrentOperation()
    {
        _currentOperationCts?.Cancel();
        StatusText = "⏳ Cancelling operation...";
    }

    /// <summary>
    /// Gets games that are selected and packageable.
    /// </summary>
    public List<InstalledGame> GetSelectedPackageableGames()
        => _libraryManager.Games.Where(g => g.IsSelected && g.IsPackageable).ToList();

    private void OnPackageProgress(string status, int percentage)
    {
        StatusText = status;
        LoadingMessage = $"{status} ({percentage}%)";
        PackageProgressUpdated?.Invoke(this, status);
    }
}
