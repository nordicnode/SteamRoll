using System.IO;
using SteamRoll.Models;

namespace SteamRoll.Services;

/// <summary>
/// Manages game library operations including scanning, analysis, and enrichment.
/// Extracted from MainWindow to improve separation of concerns.
/// </summary>
public class LibraryManager
{
    private readonly SteamLocator _steamLocator;
    private readonly LibraryScanner _libraryScanner;
    private readonly PackageScanner _packageScanner;
    private readonly CacheService _cacheService;
    private readonly DlcService _dlcService;
    private readonly SettingsService _settingsService;
    
    private List<InstalledGame> _allGames = new();
    private readonly object _gamesLock = new();
    
    /// <summary>
    /// Event raised when scan progress updates.
    /// </summary>
    public event Action<string>? ProgressChanged;

    public LibraryManager(
        SteamLocator steamLocator,
        LibraryScanner libraryScanner,
        PackageScanner packageScanner,
        CacheService cacheService,
        DlcService dlcService,
        SettingsService settingsService)
    {
        _steamLocator = steamLocator;
        _libraryScanner = libraryScanner;
        _packageScanner = packageScanner;
        _cacheService = cacheService;
        _dlcService = dlcService;
        _settingsService = settingsService;
    }
    
    /// <summary>
    /// Gets a thread-safe snapshot of all games.
    /// </summary>
    public List<InstalledGame> GetGamesSnapshot()
    {
        lock (_gamesLock)
        {
            return _allGames.ToList();
        }
    }
    
    /// <summary>
    /// Gets a game by AppId in a thread-safe manner.
    /// </summary>
    public InstalledGame? FindGameByAppId(int appId)
    {
        lock (_gamesLock)
        {
            return _allGames.FirstOrDefault(g => g.AppId == appId);
        }
    }
    
    /// <summary>
    /// Finds a game by predicate in a thread-safe manner.
    /// </summary>
    public InstalledGame? FindGame(Func<InstalledGame, bool> predicate)
    {
        lock (_gamesLock)
        {
            return _allGames.FirstOrDefault(predicate);
        }
    }
    
    /// <summary>
    /// Gets the Steam installation path, or null if not found.
    /// </summary>
    public string? GetSteamPath() => _steamLocator.GetSteamInstallPath();
    
    /// <summary>
    /// Scans all Steam libraries and returns the list of games.
    /// </summary>
    public async Task<LibraryScanResult> ScanLibrariesAsync(CancellationToken ct = default)
    {
        var result = new LibraryScanResult();
        
        ReportProgress("Scanning Steam libraries...");
        
        // Run scan on background thread
        var scannedGames = await Task.Run(() => _libraryScanner.ScanAllLibraries(), ct);
        ct.ThrowIfCancellationRequested();
        
        lock (_gamesLock)
        {
            _allGames = scannedGames;
        }
        
        // Apply cache to games - separate into cached and uncached
        var gamesToAnalyze = new List<InstalledGame>();
        var cachedGames = 0;
        
        foreach (var game in scannedGames)
        {
            if (_cacheService.ApplyCachedData(game))
            {
                cachedGames++;
            }
            else
            {
                gamesToAnalyze.Add(game);
            }
        }
        
        result.AllGames = scannedGames;
        result.CachedCount = cachedGames;
        result.GamesToAnalyze = gamesToAnalyze;
        
        ReportProgress($"Found {scannedGames.Count} games ({cachedGames} cached, {gamesToAnalyze.Count} to analyze)...");
        
        return result;
    }
    
    /// <summary>
    /// Analyzes games for DRM in parallel with progress updates.
    /// </summary>
    public async Task AnalyzeGamesForDrmAsync(List<InstalledGame> games, CancellationToken ct = default)
    {
        if (games.Count == 0) return;
        
        var completed = 0;
        var total = games.Count;
        
        await Parallel.ForEachAsync(games, 
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (game, token) =>
            {
                try
                {
                    game.DrmAnalysis = await Task.Run(() => DrmDetector.Analyze(game.FullPath), token);
                    Interlocked.Increment(ref completed);
                    
                    if (completed % 10 == 0)
                    {
                        ReportProgress($"Analyzing DRM: {completed}/{total}...");
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Debug($"DRM analysis failed for {game.Name}: {ex.Message}", "LibraryManager");
                }
            });
    }
    
    /// <summary>
    /// Checks which games have existing packages and detects available updates.
    /// </summary>
    public void CheckExistingPackages(List<InstalledGame> games)
    {
        var outputPath = _settingsService.Settings.OutputPath;
        if (string.IsNullOrEmpty(outputPath) || !Directory.Exists(outputPath)) return;
        
        var packageDirs = new Dictionary<int, string>();
        
        try
        {
            foreach (var dir in Directory.GetDirectories(outputPath))
            {
                // Check both root and steam_settings directory for AppId
                // GoldbergService writes to root, but some packages have it in steam_settings
                var appIdPaths = new[]
                {
                    System.IO.Path.Combine(dir, "steam_appid.txt"),  // Root (where GoldbergService puts it)
                    System.IO.Path.Combine(dir, "steam_settings", "steam_appid.txt")  // steam_settings subdirectory
                };
                
                foreach (var appIdPath in appIdPaths)
                {
                    if (File.Exists(appIdPath))
                    {
                        if (int.TryParse(File.ReadAllText(appIdPath).Trim(), out var appId))
                        {
                            packageDirs[appId] = dir;
                            break; // Found it, no need to check further
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Error scanning packages: {ex.Message}", "LibraryManager");
        }
        
        foreach (var game in games)
        {
            if (packageDirs.TryGetValue(game.AppId, out var packagePath))
            {
                game.IsPackaged = true;
                game.PackagePath = packagePath;
                
                // Read PackageBuildId from steamroll.json metadata
                var metadataPath = System.IO.Path.Combine(packagePath, "steamroll.json");
                if (File.Exists(metadataPath))
                {
                    try
                    {
                        var json = File.ReadAllText(metadataPath);
                        var metadata = System.Text.Json.JsonSerializer.Deserialize<Models.PackageMetadata>(json);
                        if (metadata != null && metadata.BuildId > 0)
                        {
                            game.PackageBuildId = metadata.BuildId;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.Debug($"Failed to read package metadata for {game.Name}: {ex.Message}", "LibraryManager");
                    }
                }
            }
            else
            {
                game.IsPackaged = false;
                game.PackagePath = null;
                game.PackageBuildId = null;
            }
        }
        
        // Populate Steam BuildIds for update detection
        // Games already have BuildId from Steam library scan, so UpdateAvailable will now work
        var packagesWithMetadata = games.Where(g => g.IsPackaged && g.PackageBuildId.HasValue).ToList();
        var updatesAvailable = packagesWithMetadata.Count(g => g.UpdateAvailable);
        
        if (updatesAvailable > 0)
        {
            LogService.Instance.Info($"Found {updatesAvailable} package(s) with updates available", "LibraryManager");
        }
    }

    
    /// <summary>
    /// Fetches DLC information for games.
    /// </summary>
    public async Task FetchDlcForGamesAsync(List<InstalledGame> games, CancellationToken ct = default)
    {
        foreach (var game in games)
        {
            if (ct.IsCancellationRequested) break;
            if (game.DlcFetched || game.AppId <= 0) continue;
            
            try
            {
                var dlcList = await _dlcService.GetDlcListAsync(game.AppId, ct);
                game.AvailableDlc = dlcList;
                game.DlcFetched = true;
                
                _cacheService.UpdateCache(game);
            }
            catch (Exception ex)
            {
                LogService.Instance.Debug($"Failed to fetch DLC for {game.Name}: {ex.Message}", "LibraryManager");
            }
        }
        
        _cacheService.SaveCache();
    }
    
    /// <summary>
    /// Enriches games with Steam Store data (reviews, metacritic).
    /// </summary>
    public async Task EnrichWithStoreDataAsync(List<InstalledGame> games, CancellationToken ct, bool forceRefresh = false)
    {
        var enrichedCount = 0;
        
        foreach (var game in games)
        {
            if (ct.IsCancellationRequested) break;
            if (game.AppId <= 0) continue;
            
            // Skip games that already have cached review data (unless forcing refresh)
            if (!forceRefresh && (game.HasReviewScore || game.HasMetacriticScore)) continue;
            
            try
            {
                var details = await SteamStoreService.Instance.GetGameDetailsAsync(game.AppId, ct);
                if (details != null)
                {
                    game.ReviewPositivePercent = details.ReviewPositivePercent;
                    game.ReviewDescription = details.ReviewDescription;
                    game.MetacriticScore = details.MetacriticScore;
                    enrichedCount++;
                    
                    _cacheService.UpdateCache(game);
                }
                
                await Task.Delay(50, ct);
            }
            catch (Exception ex)
            {
                LogService.Instance.Debug($"Failed to enrich {game.Name}: {ex.Message}", "LibraryManager");
            }
        }
        
        if (enrichedCount > 0)
        {
            _cacheService.SaveCache();
            LogService.Instance.Info($"Enriched and cached review data for {enrichedCount} games", "LibraryManager");
        }
    }
    
    /// <summary>
    /// Gets games that need DLC fetching.
    /// </summary>
    public List<InstalledGame> GetGamesNeedingDlc()
    {
        lock (_gamesLock)
        {
            return _allGames.Where(g => !g.DlcFetched).ToList();
        }
    }
    
    /// <summary>
    /// Saves cache for all games.
    /// </summary>
    public void SaveCache(List<InstalledGame> games)
    {
        foreach (var game in games)
        {
            _cacheService.UpdateCache(game);
        }
        _cacheService.SaveCache();
    }
    
    private void ReportProgress(string message)
    {
        ProgressChanged?.Invoke(message);
    }
}

/// <summary>
/// Result of a library scan operation.
/// </summary>
public class LibraryScanResult
{
    public List<InstalledGame> AllGames { get; set; } = new();
    public List<InstalledGame> GamesToAnalyze { get; set; } = new();
    public int CachedCount { get; set; }
}
