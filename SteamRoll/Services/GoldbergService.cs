using System.IO;
using System.Net.Http;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace SteamRoll.Services;

/// <summary>
/// Manages Goldberg Steam Emulator DLLs for game packaging.
/// Handles downloading, extracting, and applying Goldberg to game packages.
/// Uses the actively maintained gbe_fork for newer Steam SDK support.
/// </summary>
public class GoldbergService : IDisposable
{
    private readonly string _goldbergPath;
    private readonly HttpClient _httpClient;
    private readonly SettingsService? _settingsService;
    private bool _disposed;
    
    // Precompiled regex patterns for Steam interface detection (performance optimization)
    private static readonly Regex[] InterfacePatterns = new[]
    {
        new Regex(@"SteamClient\d+", RegexOptions.Compiled),
        new Regex(@"SteamUser\d+", RegexOptions.Compiled),
        new Regex(@"SteamFriends\d+", RegexOptions.Compiled),
        new Regex(@"SteamUtils\d+", RegexOptions.Compiled),
        new Regex(@"SteamMatchMaking\d+", RegexOptions.Compiled),
        new Regex(@"SteamUserStats\d+", RegexOptions.Compiled),
        new Regex(@"SteamApps\d+", RegexOptions.Compiled),
        new Regex(@"SteamNetworking\d+", RegexOptions.Compiled),
        new Regex(@"SteamRemoteStorage\d+", RegexOptions.Compiled),
        new Regex(@"SteamScreenshots\d+", RegexOptions.Compiled),
        new Regex(@"SteamHTTP\d+", RegexOptions.Compiled),
        new Regex(@"SteamController\d+", RegexOptions.Compiled),
        new Regex(@"SteamUGC\d+", RegexOptions.Compiled),
        new Regex(@"SteamAppList\d+", RegexOptions.Compiled),
        new Regex(@"SteamMusic\d+", RegexOptions.Compiled),
        new Regex(@"SteamMusicRemote\d+", RegexOptions.Compiled),
        new Regex(@"SteamHTMLSurface\d+", RegexOptions.Compiled),
        new Regex(@"SteamInventory\d+", RegexOptions.Compiled),
        new Regex(@"SteamVideo\d+", RegexOptions.Compiled),
        new Regex(@"SteamParentalSettings\d+", RegexOptions.Compiled),
        new Regex(@"SteamInput\d+", RegexOptions.Compiled),
        new Regex(@"SteamParties\d+", RegexOptions.Compiled),
        new Regex(@"SteamRemotePlay\d+", RegexOptions.Compiled),
        new Regex(@"SteamNetworkingMessages\d+", RegexOptions.Compiled),
        new Regex(@"SteamNetworkingSockets\d+", RegexOptions.Compiled),
        new Regex(@"SteamNetworkingUtils\d+", RegexOptions.Compiled),
        new Regex(@"SteamGameServer\d+", RegexOptions.Compiled),
        new Regex(@"SteamGameServerStats\d+", RegexOptions.Compiled),
    };
    
    /// <summary>
    /// Event for download progress updates.
    /// </summary>
    public event Action<string, int>? DownloadProgressChanged;
    
    /// <summary>
    /// Gets the path to the Goldberg installation directory.
    /// </summary>
    public string GoldbergPath => GetEffectiveGoldbergPath();


    public GoldbergService(SettingsService? settingsService = null)
    {
        _settingsService = settingsService;

        // Default Goldberg path
        _goldbergPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamRoll",
            "Goldberg"
        );
        
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SteamRoll/1.0");
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }

    private string GetEffectiveGoldbergPath()
    {
        if (_settingsService != null && !string.IsNullOrEmpty(_settingsService.Settings.CustomGoldbergPath))
        {
            return _settingsService.Settings.CustomGoldbergPath;
        }
        return _goldbergPath;
    }

    /// <summary>
    /// Checks if Goldberg DLLs are available locally.
    /// </summary>
    public bool IsGoldbergAvailable()
    {
        var path = GetEffectiveGoldbergPath();
        if (!Directory.Exists(path))
            return false;

        // Check for both 32-bit and 64-bit DLLs
        return File.Exists(Path.Combine(path, "steam_api.dll")) &&
               File.Exists(Path.Combine(path, "steam_api64.dll"));
    }

    /// <summary>
    /// Gets the path where Goldberg files are stored.
    /// </summary>
    public string GetGoldbergPath() => GetEffectiveGoldbergPath();

    /// <summary>
    /// Initializes the Goldberg directory.
    /// </summary>
    public void InitializeGoldbergDirectory()
    {
        // Only create default directory if custom path is not used
        if (GetEffectiveGoldbergPath() == _goldbergPath)
        {
            Directory.CreateDirectory(_goldbergPath);
        }
    }

    /// <summary>
    /// Gets the installed version of Goldberg Emulator.
    /// </summary>
    public string? GetInstalledVersion()
    {
        try
        {
            var versionPath = System.IO.Path.Combine(GetEffectiveGoldbergPath(), "version.txt");
            if (File.Exists(versionPath))
            {
                var rawVersion = File.ReadAllText(versionPath).Trim();
                return CleanupVersionString(rawVersion);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("Error reading Goldberg version", ex);
        }
        return null;
    }
    
    /// <summary>
    /// Cleans up a version string like "release-2025_11_27" to "2025.11.27".
    /// </summary>
    private static string CleanupVersionString(string rawVersion)
    {
        // Handle formats like "release-2025_11_27" or "nightly-2025_11_27"
        if (rawVersion.Contains('-'))
        {
            var parts = rawVersion.Split('-');
            if (parts.Length >= 2)
            {
                // Get the date part and convert underscores to dots
                var datePart = parts[^1].Replace('_', '.');
                return datePart;
            }
        }
        
        // Already clean or unknown format
        return rawVersion;
    }

    /// <summary>
    /// Downloads and extracts Goldberg Emulator automatically.
    /// </summary>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> DownloadGoldbergAsync()
    {
        try
        {
            ReportProgress("Checking for Goldberg releases...", 5);
            
            // Try GitHub fork first (actively maintained with newer Steam SDK support)
            LogService.Instance.Debug("Attempting to fetch from GitHub (gbe_fork)...", "GoldbergService");
            var (downloadUrl, version) = await GetGitHubReleaseInfoAsync();
            
            if (string.IsNullOrEmpty(downloadUrl))
            {
                // Fallback to original GitLab
                LogService.Instance.Warning("GitHub failed, trying original GitLab...", "GoldbergService");
                (downloadUrl, version) = await GetGitLabReleaseInfoAsync();
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                LogService.Instance.Error("Could not find Goldberg download URL from any source", category: "GoldbergService");
                ReportProgress("Download failed - no URL found", 0);
                return false;
            }
            
            LogService.Instance.Info($"Download URL: {downloadUrl}, Version: {version}", "GoldbergService");

            ReportProgress("Downloading Goldberg Emulator...", 10);
            
            // Determine file extension from URL
            var is7z = downloadUrl.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
            var tempArchivePath = System.IO.Path.Combine(Path.GetTempPath(), is7z ? "goldberg_temp.7z" : "goldberg_temp.zip");
            await DownloadFileAsync(downloadUrl, tempArchivePath);

            ReportProgress("Extracting Goldberg files...", 70);
            
            // Extract to temp folder first
            var tempExtractPath = System.IO.Path.Combine(Path.GetTempPath(), "goldberg_extract");
            if (Directory.Exists(tempExtractPath))
                Directory.Delete(tempExtractPath, true);
            Directory.CreateDirectory(tempExtractPath);

            // Use SharpCompress for 7z, built-in for zip
            if (is7z)
            {
                using var archive = ArchiveFactory.Open(tempArchivePath);
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    entry.WriteToDirectory(tempExtractPath, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                }
            }
            else
            {
                ZipFile.ExtractToDirectory(tempArchivePath, tempExtractPath);
            }

            ReportProgress("Installing Goldberg DLLs...", 85);
            
            // Find and copy the DLLs
            Directory.CreateDirectory(_goldbergPath);
            CopyGoldbergFiles(tempExtractPath, _goldbergPath);
            
            // Save version to version.txt for update checking
            if (!string.IsNullOrEmpty(version))
            {
                var versionPath = System.IO.Path.Combine(_goldbergPath, "version.txt");
                await File.WriteAllTextAsync(versionPath, version);
                LogService.Instance.Info($"Saved version {version} to {versionPath}", "GoldbergService");
            }

            // Cleanup - wrapped in try/catch to avoid failing the operation on cleanup errors
            ReportProgress("Cleaning up...", 95);
            try
            {
                if (File.Exists(tempArchivePath))
                    File.Delete(tempArchivePath);
                if (Directory.Exists(tempExtractPath))
                    Directory.Delete(tempExtractPath, true);
            }
            catch (Exception cleanupEx)
            {
                LogService.Instance.Debug($"Temp cleanup failed (non-critical): {cleanupEx.Message}", "GoldbergService");
            }


            ReportProgress("Goldberg Emulator ready!", 100);
            return IsGoldbergAvailable();
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error downloading Goldberg: {ex.Message}", ex, "GoldbergService");
            return false;
        }
    }


    /// <summary>
    /// Gets download URL and version from GitHub releases API.
    /// </summary>
    private async Task<(string? url, string? version)> GetGitHubReleaseInfoAsync()
    {
        try
        {
            LogService.Instance.Debug("Fetching GitHub releases...", "GoldbergService");
            var apiUrl = _settingsService?.Settings.GoldbergGitHubUrl ?? AppConstants.DEFAULT_GOLDBERG_GITHUB_URL;
            var response = await HttpRetryHelper.ExecuteWithRetryAsync(
                () => _httpClient.GetStringAsync(apiUrl),
                "GitHub Releases API",
                maxRetries: 2);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            
            // Get the version tag
            var version = root.TryGetProperty("tag_name", out var tagProp) 
                ? tagProp.GetString()?.TrimStart('v') 
                : null;

            if (root.TryGetProperty("assets", out var assets))
            {
                // Priority 1: Look for Windows release build (emu-win-release.7z or similar)
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    LogService.Instance.Debug($"GitHub asset: {name}", "GoldbergService");
                    
                    if (name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("release", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("debug", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("migrate", StringComparison.OrdinalIgnoreCase) &&
                        (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        var url = asset.GetProperty("browser_download_url").GetString();
                        LogService.Instance.Debug($"Selected: {url}", "GoldbergService");
                        return (url, version);
                    }
                }
                
                // Priority 2: Any Windows 7z/zip (including debug builds)
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.StartsWith("emu-win", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("migrate", StringComparison.OrdinalIgnoreCase) &&
                        (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        var url = asset.GetProperty("browser_download_url").GetString();
                        LogService.Instance.Debug($"Selected fallback: {url}", "GoldbergService");
                        return (url, version);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"GitHub API error: {ex.Message}", "GoldbergService");
        }
        return (null, null);
    }


    /// <summary>
    /// Gets download URL and version from GitLab releases API.
    /// </summary>
    private async Task<(string? url, string? version)> GetGitLabReleaseInfoAsync()
    {
        try
        {
            LogService.Instance.Debug("Fetching GitLab releases...", "GoldbergService");
            var apiUrl = _settingsService?.Settings.GoldbergGitLabUrl ?? AppConstants.DEFAULT_GOLDBERG_GITLAB_URL;
            var response = await HttpRetryHelper.ExecuteWithRetryAsync(
                () => _httpClient.GetStringAsync(apiUrl),
                "GitLab Releases API",
                maxRetries: 2);
            using var doc = JsonDocument.Parse(response);
            var releases = doc.RootElement;

            // Get the first (latest) release
            if (releases.ValueKind == JsonValueKind.Array && releases.GetArrayLength() > 0)
            {
                var latestRelease = releases[0];
                var version = latestRelease.TryGetProperty("tag_name", out var tagProp) 
                    ? tagProp.GetString()?.TrimStart('v') 
                    : null;
                LogService.Instance.Debug($"Latest release tag: {version}", "GoldbergService");
                
                // Look for assets/links
                if (latestRelease.TryGetProperty("assets", out var assets) &&
                    assets.TryGetProperty("links", out var links))
                {
                    foreach (var link in links.EnumerateArray())
                    {
                        var name = link.GetProperty("name").GetString() ?? "";
                        LogService.Instance.Debug($"Found asset: {name}", "GoldbergService");
                        
                        // Get the direct download URL (GitLab uses direct_asset_url)
                        string? url = null;
                        if (link.TryGetProperty("direct_asset_url", out var directUrl))
                        {
                            url = directUrl.GetString();
                        }
                        else if (link.TryGetProperty("url", out var normalUrl))
                        {
                            url = normalUrl.GetString();
                        }
                        
                        // Look for the main release zip (Goldberg_Lan_Steam_Emu_*.zip)
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            (name.Contains("Goldberg", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("Emu", StringComparison.OrdinalIgnoreCase)))
                        {
                            LogService.Instance.Debug($"Selected download URL: {url}", "GoldbergService");
                            return (url, version);
                        }
                    }
                    
                    // Fallback: just get any .zip file
                    foreach (var link in links.EnumerateArray())
                    {
                        var name = link.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            if (link.TryGetProperty("direct_asset_url", out var directUrl))
                                return (directUrl.GetString(), version);
                            if (link.TryGetProperty("url", out var normalUrl))
                                return (normalUrl.GetString(), version);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"GitLab API error: {ex.Message}", "GoldbergService");
        }
        return (null, null);
    }


    /// <summary>
    /// Downloads a file with progress reporting.
    /// </summary>
    private async Task DownloadFileAsync(string url, string destinationPath)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        var bytesDownloaded = 0L;

        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
        
        var buffer = new byte[8192];
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            bytesDownloaded += bytesRead;

            if (totalBytes > 0)
            {
                var progress = (int)(10 + ((double)bytesDownloaded / totalBytes * 60)); // 10-70%
                ReportProgress($"Downloading: {bytesDownloaded / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB", progress);
            }
        }
    }

    /// <summary>
    /// Copies Goldberg DLLs from extracted folder to destination.
    /// </summary>
    private void CopyGoldbergFiles(string sourcePath, string destPath)
    {
        var filesToCopy = new[] { "steam_api.dll", "steam_api64.dll", "steamclient.dll", "steamclient64.dll" };
        
        foreach (var fileName in filesToCopy)
        {
            var files = Directory.GetFiles(sourcePath, fileName, SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                // Prefer files in "release" or "experimental" folders if available
                var sourceFile = files
                    .OrderByDescending(f => f.Contains("release", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => f.Contains("experimental", StringComparison.OrdinalIgnoreCase))
                    .First();
                
                File.Copy(sourceFile, System.IO.Path.Combine(destPath, fileName), overwrite: true);
                LogService.Instance.Info($"Copied: {fileName}", "GoldbergService");
            }
        }
    }

    /// <summary>
    /// Ensures Goldberg is available, downloading if necessary.
    /// </summary>
    public async Task<bool> EnsureGoldbergAvailableAsync()
    {
        if (IsGoldbergAvailable())
            return true;

        return await DownloadGoldbergAsync();
    }

    private void ReportProgress(string status, int percentage)
    {
        DownloadProgressChanged?.Invoke(status, percentage);
    }

    /// <summary>
    /// Applies Goldberg Emulator to a game package directory.
    /// </summary>
    public bool ApplyGoldberg(string gameDir, int appId, GoldbergConfig? config = null)
    {
        if (!IsGoldbergAvailable())
        {
            LogService.Instance.Warning("Goldberg DLLs not available - skipping DLL replacement", "GoldbergService");
            return false;
        }

        try
        {
            var replacedCount = 0;
            var goldbergPath = GetEffectiveGoldbergPath();
            
            foreach (var originalDll in FindSteamApiDlls(gameDir))
            {
                var fileName = System.IO.Path.GetFileName(originalDll).ToLowerInvariant();
                var goldbergDll = fileName switch
                {
                    "steam_api.dll" => System.IO.Path.Combine(goldbergPath, "steam_api.dll"),
                    "steam_api64.dll" => System.IO.Path.Combine(goldbergPath, "steam_api64.dll"),
                    _ => null
                };

                if (goldbergDll != null && File.Exists(goldbergDll))
                {
                    // Backup original DLL
                    var backupPath = originalDll + ".original";
                    if (!File.Exists(backupPath))
                    {
                        File.Copy(originalDll, backupPath);
                    }

                    // Replace with Goldberg DLL
                    File.Copy(goldbergDll, originalDll, overwrite: true);
                    replacedCount++;
                }
            }

            // Create steam_appid.txt in game root
            File.WriteAllText(Path.Combine(gameDir, "steam_appid.txt"), appId.ToString());

            // Create basic steam_settings folder with user config
            CreateSteamSettings(gameDir, appId, config);

            return replacedCount > 0;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error applying Goldberg: {ex.Message}", ex, "GoldbergService");
            return false;
        }
    }

    private IEnumerable<string> FindSteamApiDlls(string directory)
    {
        var dlls = new List<string>();
        
        try
        {
            foreach (var pattern in new[] { "steam_api.dll", "steam_api64.dll" })
            {
                dlls.AddRange(Directory.GetFiles(directory, pattern, SearchOption.AllDirectories));
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Error searching for DLLs: {ex.Message}", "GoldbergService");
        }

        return dlls;
    }

    private void CreateSteamSettings(string gameDir, int appId, GoldbergConfig? config = null)
    {
        var settingsDir = System.IO.Path.Combine(gameDir, "steam_settings");
        Directory.CreateDirectory(settingsDir);

        // Use provided config or defaults
        var accountName = config?.AccountName ?? "Player";
        var disableNetworking = config?.DisableNetworking ?? true;
        var disableOverlay = config?.DisableOverlay ?? true;
        var enableLan = config?.EnableLan ?? false;

        // Main config - user settings
        File.WriteAllText(Path.Combine(settingsDir, "configs.user.ini"), $"""
            [user::general]
            account_name = {accountName}
            
            [user::saves]
            local_save_path = SteamRoll_Saves
            """);

        File.WriteAllText(
            System.IO.Path.Combine(settingsDir, "force_account_name.txt"),
            accountName
        );

        // --- Files to control Steam behavior ---
        
        // offline.txt - Run in offline mode to prevent Steam network calls
        if (disableNetworking)
        {
            File.WriteAllText(Path.Combine(settingsDir, "offline.txt"), "");
            File.WriteAllText(Path.Combine(settingsDir, "disable_networking.txt"), "");
        }
        
        // disable_overlay.txt - Disable Steam overlay (prevents hook conflicts)
        if (disableOverlay)
        {
            File.WriteAllText(Path.Combine(settingsDir, "disable_overlay.txt"), "");
        }
        
        // Main settings override
        File.WriteAllText(Path.Combine(settingsDir, "configs.main.ini"), $"""
            [main::connectivity]
            disable_networking={(disableNetworking ? 1 : 0)}
            disable_lan_only={(enableLan ? 0 : 1)}
            
            [main::general]
            disable_overlay={(disableOverlay ? 1 : 0)}
            """);
    }

    public List<string> DetectInterfaces(string steamApiPath)
    {
        var interfaces = new List<string>();
        
        if (!File.Exists(steamApiPath))
            return interfaces;

        try
        {
            // Sanity check: steam_api.dll shouldn't be massive
            // If it is, it's likely not the real DLL or something weird is going on.
            // Reading a 100MB+ file into a string for regex is bad for memory.
            var info = new FileInfo(steamApiPath);
            if (info.Length > 10 * 1024 * 1024) // 10MB limit
            {
                LogService.Instance.Warning($"Skipping interface detection for {steamApiPath} (size {info.Length} > 10MB)", "GoldbergService");
                return interfaces;
            }

            var bytes = File.ReadAllBytes(steamApiPath);
            var content = System.Text.Encoding.ASCII.GetString(bytes);
            
            // Use precompiled regex patterns for better performance
            foreach (var regex in InterfacePatterns)
            {
                var matches = regex.Matches(content);
                foreach (Match match in matches)
                {
                    if (!interfaces.Contains(match.Value))
                        interfaces.Add(match.Value);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error detecting interfaces in {steamApiPath}: {ex.Message}", ex, "GoldbergService");
        }

        return interfaces.OrderBy(i => i).ToList();
    }

    public void CreateInterfacesFile(string gameDir, List<string> interfaces)
    {
        if (interfaces.Count == 0)
            return;

        File.WriteAllLines(Path.Combine(gameDir, "steam_interfaces.txt"), interfaces);
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
