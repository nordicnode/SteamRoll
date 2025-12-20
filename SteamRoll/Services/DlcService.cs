using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace SteamRoll.Services;

/// <summary>
/// Manages DLC detection, fetching, and configuration for game packages.
/// Uses Steam's public API to discover DLC and generates unlock configurations.
/// </summary>
public class DlcService : IDisposable
{
    private readonly HttpClient _httpClient;
    private bool _disposed;
    private const string STEAM_API_URL = "https://store.steampowered.com/api/appdetails?appids={0}";

    public DlcService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SteamRoll/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Fetches all available DLC for a game from Steam's API.
    /// </summary>
    /// <param name="appId">The game's AppID.</param>
    /// <returns>List of DLC info, or empty list on failure.</returns>
    public async Task<List<DlcInfo>> GetDlcListAsync(int appId, CancellationToken ct = default)
    {
        var dlcList = new List<DlcInfo>();

        try
        {
            var url = string.Format(STEAM_API_URL, appId);
            var response = await HttpRetryHelper.ExecuteWithRetryAsync(
                () => _httpClient.GetStringAsync(url, ct),
                $"Steam DLC API (AppId: {appId})",
                maxRetries: 2,
                cancellationToken: ct);
            
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            // Navigate to the data.dlc array
            if (root.TryGetProperty(appId.ToString(), out var appData) &&
                appData.TryGetProperty("success", out var success) &&
                success.GetBoolean() &&
                appData.TryGetProperty("data", out var data))
            {
                // Get DLC AppIDs
                if (data.TryGetProperty("dlc", out var dlcArray))
                {
                    var dlcAppIds = new List<int>();
                    foreach (var dlcId in dlcArray.EnumerateArray())
                    {
                        dlcAppIds.Add(dlcId.GetInt32());
                    }

                    // Fetch details for each DLC (in batches to avoid rate limiting)
                    var batches = dlcAppIds.Chunk(20);
                    foreach (var batch in batches)
                    {
                        var batchInfos = await GetDlcDetailsBatchAsync(batch, ct);
                        dlcList.AddRange(batchInfos);
                        
                        // Delay between batches
                        await Task.Delay(500, ct); 
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error fetching DLC list for {appId}: {ex.Message}", ex, "DlcService");
        }

        return dlcList;
    }

    /// <summary>
    /// Fetches details for a batch of DLC AppIDs.
    /// </summary>
    private async Task<List<DlcInfo>> GetDlcDetailsBatchAsync(int[] dlcAppIds, CancellationToken ct = default)
    {
        var results = new List<DlcInfo>();
        if (dlcAppIds.Length == 0) return results;

        try
        {
            // Join IDs for batch request
            var idsStr = string.Join(",", dlcAppIds);
            var url = string.Format(STEAM_API_URL, idsStr);
            
            var response = await HttpRetryHelper.ExecuteWithRetryAsync(
                () => _httpClient.GetStringAsync(url, ct),
                $"Steam DLC Details Batch API",
                maxRetries: 2,
                cancellationToken: ct);
            
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            foreach (var dlcAppId in dlcAppIds)
            {
                if (root.TryGetProperty(dlcAppId.ToString(), out var appData) &&
                    appData.TryGetProperty("success", out var success) &&
                    success.GetBoolean() &&
                    appData.TryGetProperty("data", out var data))
                {
                    var name = data.TryGetProperty("name", out var nameProp) 
                        ? nameProp.GetString() ?? $"DLC {dlcAppId}" 
                        : $"DLC {dlcAppId}";

                    var isFree = data.TryGetProperty("is_free", out var freeProp) && freeProp.GetBoolean();

                    results.Add(new DlcInfo
                    {
                        AppId = dlcAppId,
                        Name = name,
                        IsFree = isFree
                    });
                }
                else
                {
                    // If one fails in batch, we can add a placeholder or skip
                    // Adding placeholder ensures we track existence
                     results.Add(new DlcInfo { AppId = dlcAppId, Name = $"DLC {dlcAppId}" });
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error fetching DLC batch", ex);
            // Fallback: return basic info for all in batch
            foreach (var id in dlcAppIds)
            {
                 results.Add(new DlcInfo { AppId = id, Name = $"DLC {id}" });
            }
        }
        
        return results;
    }



    /// <summary>
    /// Checks which DLC are installed locally for a game.
    /// </summary>
    /// <param name="gamePath">Path to the game installation.</param>
    /// <param name="libraryPath">Path to the Steam library.</param>
    /// <param name="allDlc">List of all available DLC.</param>
    public void CheckInstalledDlc(string gamePath, string libraryPath, List<DlcInfo> allDlc)
    {
        // Check for DLC app manifests in the steamapps folder
        var steamappsPath = System.IO.Path.Combine(libraryPath, "steamapps");
        
        foreach (var dlc in allDlc)
        {
            var manifestPath = System.IO.Path.Combine(steamappsPath, $"appmanifest_{dlc.AppId}.acf");
            dlc.IsInstalled = File.Exists(manifestPath);
        }
    }

    /// <summary>
    /// Generates Goldberg DLC.txt content to unlock all DLC.
    /// </summary>
    /// <param name="dlcList">List of DLC to include.</param>
    /// <returns>Content for steam_settings/DLC.txt</returns>
    public string GenerateGoldbergDlcConfig(List<DlcInfo> dlcList)
    {
        // Goldberg DLC.txt format: one line per DLC
        // <appid>=<dlc_name>
        var lines = new List<string>();
        
        foreach (var dlc in dlcList)
        {
            // Clean the name for config file (remove special chars)
            var cleanName = dlc.Name
                .Replace("=", "-")
                .Replace("\n", " ")
                .Replace("\r", "")
                .Trim();
            
            lines.Add($"{dlc.AppId}={cleanName}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Writes Goldberg DLC configuration to the package folder.
    /// </summary>
    /// <param name="packagePath">Path to the packaged game.</param>
    /// <param name="dlcList">List of DLC to unlock.</param>
    public async Task WriteDlcConfigAsync(string packagePath, List<DlcInfo> dlcList)
    {
        if (dlcList.Count == 0) return;

        // Find or create steam_settings folder
        var settingsPath = System.IO.Path.Combine(packagePath, "steam_settings");
        Directory.CreateDirectory(settingsPath);

        // Write DLC.txt
        var dlcConfig = GenerateGoldbergDlcConfig(dlcList);
        var dlcFilePath = System.IO.Path.Combine(settingsPath, "DLC.txt");
        await File.WriteAllTextAsync(dlcFilePath, dlcConfig);

        LogService.Instance.Info($"Wrote DLC config with {dlcList.Count} DLC to {dlcFilePath}", "DlcService");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Represents a single DLC item.
/// </summary>
public class DlcInfo
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public bool IsInstalled { get; set; }
    public bool IsFree { get; set; }
    public bool IsOwned { get; set; }
    
    /// <summary>
    /// For display purposes.
    /// </summary>
    public string DisplayStatus
    {
        get
        {
            if (IsInstalled) return "✓ Installed";
            if (IsOwned) return "Owned";
            if (IsFree) return "Free";
            return "Not owned";
        }
    }
}
