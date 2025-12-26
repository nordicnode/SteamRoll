using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SteamRoll.Services;

/// <summary>
/// Service for checking for updates to Goldberg Emulator and SteamRoll.
/// </summary>
public class UpdateService : IDisposable
{
    private const string GOLDBERG_RELEASES_API = "https://api.github.com/repos/Detanup01/gbe_fork/releases/latest";
    private const string STEAMROLL_RELEASES_API = "https://api.github.com/repos/NordicNode/steamroll/releases/latest";
    private const string USER_AGENT = "SteamRoll/1.1.0";
    
    private readonly HttpClient _httpClient;
    private readonly string _goldbergPath;
    private bool _disposed;
    
    /// <summary>
    /// Event fired when an update is available.
    /// </summary>
    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;
    
    public UpdateService(string goldbergPath)
    {
        _goldbergPath = goldbergPath;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", USER_AGENT);
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }
    
    /// <summary>
    /// Checks for Goldberg Emulator updates.
    /// </summary>
    /// <returns>Update info if available, null otherwise.</returns>
    public async Task<UpdateInfo?> CheckGoldbergUpdateAsync()
    {
        try
        {
            var currentVersion = GetCurrentGoldbergVersion();
            if (string.IsNullOrEmpty(currentVersion))
            {
                LogService.Instance.Warning("No Goldberg installation found", "UpdateService");
                return null;
            }
            
            var response = await _httpClient.GetStringAsync(GOLDBERG_RELEASES_API);
            var release = JsonSerializer.Deserialize<GitHubRelease>(response, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            if (release == null)
                return null;
            
            var latestVersion = release.TagName?.TrimStart('v') ?? "";
            
            if (IsNewerVersion(latestVersion, currentVersion))
            {
                var updateInfo = new UpdateInfo
                {
                    Name = "Goldberg Emulator",
                    CurrentVersion = currentVersion,
                    LatestVersion = latestVersion,
                    ReleaseUrl = release.HtmlUrl ?? "",
                    ReleaseNotes = release.Body ?? ""
                };
                
                UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs { Update = updateInfo });
                return updateInfo;
            }
            
            LogService.Instance.Debug($"Goldberg is up to date: {currentVersion}", "UpdateService");
            return null;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Update check failed: {ex.Message}", "UpdateService");
            return null;
        }
    }
    
    /// <summary>
    /// Checks for SteamRoll application updates.
    /// </summary>
    /// <returns>Update info if available, null otherwise.</returns>
    public async Task<UpdateInfo?> CheckSteamRollUpdateAsync()
    {
        try
        {
            var currentVersion = GetCurrentSteamRollVersion();
            if (string.IsNullOrEmpty(currentVersion))
            {
                LogService.Instance.Warning("Could not determine SteamRoll version", "UpdateService");
                return null;
            }
            
            var response = await _httpClient.GetStringAsync(STEAMROLL_RELEASES_API);
            var release = JsonSerializer.Deserialize<GitHubRelease>(response, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            if (release == null)
                return null;
            
            var latestVersion = release.TagName?.TrimStart('v') ?? "";
            
            if (IsNewerVersion(latestVersion, currentVersion))
            {
                var updateInfo = new UpdateInfo
                {
                    Name = "SteamRoll",
                    CurrentVersion = currentVersion,
                    LatestVersion = latestVersion,
                    ReleaseUrl = release.HtmlUrl ?? "",
                    ReleaseNotes = release.Body ?? ""
                };
                
                UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs { Update = updateInfo });
                LogService.Instance.Info($"SteamRoll update available: {currentVersion} -> {latestVersion}", "UpdateService");
                return updateInfo;
            }
            
            LogService.Instance.Debug($"SteamRoll is up to date: {currentVersion}", "UpdateService");
            return null;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"SteamRoll update check failed: {ex.Message}", "UpdateService");
            return null;
        }
    }
    
    /// <summary>
    /// Gets the current SteamRoll application version from assembly.
    /// </summary>
    private static string GetCurrentSteamRollVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "";
    }
    
    /// <summary>
    /// Gets the currently installed Goldberg version from version file.
    /// </summary>
    private string GetCurrentGoldbergVersion()
    {
        var versionFile = System.IO.Path.Combine(_goldbergPath, "version.txt");
        if (File.Exists(versionFile))
        {
            var content = File.ReadAllText(versionFile).Trim();
            return content.TrimStart('v');
        }
        
        // Try to detect from DLL file date or other markers
        var dllPath = System.IO.Path.Combine(_goldbergPath, "steam_api.dll");
        if (File.Exists(dllPath))
        {
            return "installed"; // Unknown version but installed
        }
        
        return "";
    }
    
    /// <summary>
    /// Compares two version strings to determine if latest is newer.
    /// </summary>
    private static bool IsNewerVersion(string latest, string current)
    {
        // Handle special case where current version is unknown
        if (current == "installed")
            return true; // Suggest update since we can't verify version
            
        // Try semantic version comparison
        if (Version.TryParse(NormalizeVersion(latest), out var latestVer) &&
            Version.TryParse(NormalizeVersion(current), out var currentVer))
        {
            return latestVer > currentVer;
        }
        
        // Fallback to string comparison
        return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
    }
    
    /// <summary>
    /// Normalizes a version string for comparison.
    /// </summary>
    private static string NormalizeVersion(string version)
    {
        // Remove common prefixes and suffixes
        version = version.TrimStart('v').Trim();
        
        // Extract just the numeric portion
        var parts = version.Split('-', '_')[0];
        
        // Ensure at least major.minor format
        var segments = parts.Split('.');
        if (segments.Length == 1)
            return $"{segments[0]}.0.0.0";
        if (segments.Length == 2)
            return $"{segments[0]}.{segments[1]}.0.0";
        if (segments.Length == 3)
            return $"{segments[0]}.{segments[1]}.{segments[2]}.0";
        
        return parts;
    }
    
    /// <summary>
    /// Disposes of managed resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _httpClient?.Dispose();
    }
}

/// <summary>
/// Information about an available update.
/// </summary>
public class UpdateInfo
{
    public string Name { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
}

/// <summary>
/// Event args for update availability notification.
/// </summary>
public class UpdateAvailableEventArgs : EventArgs
{
    public required UpdateInfo Update { get; set; }
}

/// <summary>
/// GitHub release API response (partial).
/// </summary>
internal class GitHubRelease
{
    public string? TagName { get; set; }
    public string? Name { get; set; }
    public string? Body { get; set; }
    public string? HtmlUrl { get; set; }
    public DateTime? PublishedAt { get; set; }
}
