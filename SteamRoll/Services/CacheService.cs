using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamRoll.Models;

namespace SteamRoll.Services;

/// <summary>
/// Provides persistent caching for game metadata, DRM analysis, and DLC information.
/// Uses JSON file storage in the application data folder.
/// </summary>
public class CacheService
{
    private readonly string _cacheDir;
    private readonly string _cacheFile;
    private GameCache _cache;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CacheService()
    {
        // Store cache in AppData/Local/SteamRoll
        _cacheDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamRoll");
        _cacheFile = System.IO.Path.Combine(_cacheDir, "game_cache.json");
        _cache = new GameCache();
        
        LoadCache();
    }

    /// <summary>
    /// Loads the cache from disk.
    /// </summary>
    private void LoadCache()
    {
        try
        {
            if (File.Exists(_cacheFile))
            {
                var json = File.ReadAllText(_cacheFile);
                _cache = JsonSerializer.Deserialize<GameCache>(json, _jsonOptions) ?? new GameCache();
                LogService.Instance.Info($"Loaded cache with {_cache.Games.Count} games", "CacheService");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Cache load failed: {ex.Message}", ex, "CacheService");
            _cache = new GameCache();
        }
    }

    /// <summary>
    /// Saves the cache to disk.
    /// </summary>
    public void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            _cache.LastUpdated = DateTime.Now;
            
            var json = JsonSerializer.Serialize(_cache, _jsonOptions);
            File.WriteAllText(_cacheFile, json);
            
            LogService.Instance.Info($"Saved cache with {_cache.Games.Count} games", "CacheService");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Cache save failed: {ex.Message}", ex, "CacheService");
        }
    }

    /// <summary>
    /// Gets cached data for a game by AppID.
    /// Returns null if not cached or if cache is stale.
    /// </summary>
    public CachedGame? GetCachedGame(int appId)
    {
        if (_cache.Games.TryGetValue(appId, out var cached))
        {
            // Cache is valid if less than 7 days old
            if (cached.CachedAt > DateTime.Now.AddDays(-7))
            {
                return cached;
            }
        }
        return null;
    }

    /// <summary>
    /// Updates the cache for a game.
    /// </summary>
    public void UpdateCache(InstalledGame game)
    {
        var cached = new CachedGame
        {
            AppId = game.AppId,
            Name = game.Name,
            SizeOnDisk = game.SizeOnDisk,
            BuildId = game.BuildId,
            InstallPath = game.FullPath,
            CachedAt = DateTime.Now,
            
            // DRM Analysis
            PrimaryDrm = game.PrimaryDrm.ToString(),
            CompatibilityScore = game.CompatibilityScore,
            CompatibilityReason = game.CompatibilityReason,
            IsGoldbergCompatible = game.IsGoldbergCompatible,
            HasSteamworksIntegration = game.DrmAnalysis?.HasSteamworksIntegration ?? false,
            
            // DLC Info
            DlcFetched = game.DlcFetched,
            DlcList = game.AvailableDlc.Select(d => new CachedDlc
            {
                AppId = d.AppId,
                Name = d.Name,
                IsInstalled = d.IsInstalled,
                IsFree = d.IsFree
            }).ToList(),
            
            // Package Status
            IsPackaged = game.IsPackaged,
            PackagePath = game.PackagePath,
            LastPackaged = game.LastPackaged,
            IsReceivedPackage = game.IsReceivedPackage,
            
            // Review Scores
            ReviewPositivePercent = game.ReviewPositivePercent,
            MetacriticScore = game.MetacriticScore,
            ReviewsFetched = game.ReviewPositivePercent.HasValue || game.MetacriticScore.HasValue
        };
        
        _cache.Games[game.AppId] = cached;
    }

    /// <summary>
    /// Applies cached data to an InstalledGame object.
    /// Returns true if cache was applied, false if rescan needed.
    /// </summary>
    /// <param name="game">The game to apply cache to</param>
    /// <param name="skipPathValidation">Skip path validation (useful for packages where path differs from original)</param>
    public bool ApplyCachedData(InstalledGame game, bool skipPathValidation = false)
    {
        var cached = GetCachedGame(game.AppId);
        if (cached == null) return false;
        
        // Verify the cache is still valid (same path, similar size)
        // Skip path validation for packages since they're in a different location
        if (!skipPathValidation)
        {
            if (cached.InstallPath != game.FullPath ||
                Math.Abs(cached.SizeOnDisk - game.SizeOnDisk) > 100_000_000) // >100MB difference = rescan
            {
                return false;
            }
        }
        
        // Apply DRM analysis from cache
        if (!string.IsNullOrEmpty(cached.PrimaryDrm) && 
            Enum.TryParse<DrmType>(cached.PrimaryDrm, out var drmType))
        {
            game.DrmAnalysis = new DrmAnalysisResult();
            game.DrmAnalysis.AddDrm(drmType, "From cache");
            game.DrmAnalysis.HasSteamworksIntegration = cached.HasSteamworksIntegration;
            
            // Recalculate compatibility based on restored DRM
            game.DrmAnalysis.CalculateCompatibility();
        }
        
        // Apply DLC info from cache
        if (cached.DlcFetched && cached.DlcList != null)
        {
            game.AvailableDlc = cached.DlcList.Select(d => new DlcInfo
            {
                AppId = d.AppId,
                Name = d.Name,
                IsInstalled = d.IsInstalled,
                IsFree = d.IsFree
            }).ToList();
            game.DlcFetched = true;
        }
        
        // Apply package status
        game.IsPackaged = cached.IsPackaged;
        game.PackagePath = cached.PackagePath;
        game.LastPackaged = cached.LastPackaged;
        game.IsReceivedPackage = cached.IsReceivedPackage;
        
        // Apply review scores from cache
        if (cached.ReviewsFetched)
        {
            game.ReviewPositivePercent = cached.ReviewPositivePercent;
            game.MetacriticScore = cached.MetacriticScore;
        }
        
        return true;
    }

    /// <summary>
    /// Clears the entire cache.
    /// </summary>
    public void ClearCache()
    {
        _cache = new GameCache();
        if (File.Exists(_cacheFile))
        {
            File.Delete(_cacheFile);
        }
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public (int gameCount, DateTime? lastUpdated) GetStats()
    {
        return (_cache.Games.Count, _cache.LastUpdated);
    }
}

/// <summary>
/// Root cache structure.
/// </summary>
public class GameCache
{
    public DateTime? LastUpdated { get; set; }
    public string Version { get; set; } = "1.0";
    public Dictionary<int, CachedGame> Games { get; set; } = new();
}

/// <summary>
/// Cached data for a single game.
/// </summary>
public class CachedGame
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public long SizeOnDisk { get; set; }
    public int BuildId { get; set; }
    public string InstallPath { get; set; } = "";
    public DateTime CachedAt { get; set; }
    
    // DRM Analysis
    public string PrimaryDrm { get; set; } = "None";
    public float CompatibilityScore { get; set; }
    public string CompatibilityReason { get; set; } = "";
    public bool IsGoldbergCompatible { get; set; }
    public bool HasSteamworksIntegration { get; set; }
    
    // DLC Info
    public bool DlcFetched { get; set; }
    public List<CachedDlc>? DlcList { get; set; }
    
    // Package Status
    public bool IsPackaged { get; set; }
    public string? PackagePath { get; set; }
    public DateTime? LastPackaged { get; set; }
    public bool IsReceivedPackage { get; set; }
    
    // Review Scores (from Steam Store)
    public int? ReviewPositivePercent { get; set; }
    public int? MetacriticScore { get; set; }
    public bool ReviewsFetched { get; set; }
}

/// <summary>
/// Cached DLC information.
/// </summary>
public class CachedDlc
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public bool IsInstalled { get; set; }
    public bool IsFree { get; set; }
}
