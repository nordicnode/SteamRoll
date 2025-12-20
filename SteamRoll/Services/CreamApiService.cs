using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace SteamRoll.Services;

/// <summary>
/// Service for downloading and applying CreamAPI to game packages.
/// CreamAPI is a steam_api.dll proxy that unlocks DLC while maintaining Steam integration.
/// </summary>
public class CreamApiService : IDisposable
{
    private const string CREAMAPI_FOLDER = "CreamAPI";
    
    private readonly string _creamApiPath;
    private readonly HttpClient _httpClient;
    private readonly SettingsService? _settingsService;
    private bool _disposed;

    public event Action<string, int>? DownloadProgressChanged;

    public CreamApiService(SettingsService? settingsService = null)
    {
        _settingsService = settingsService;
        _creamApiPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamRoll",
            CREAMAPI_FOLDER
        );
        
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SteamRoll/1.0");
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }
    


    private string GetEffectiveCreamApiPath()
    {
        if (_settingsService != null && !string.IsNullOrEmpty(_settingsService.Settings.CustomCreamApiPath))
        {
            return _settingsService.Settings.CustomCreamApiPath;
        }
        return _creamApiPath;
    }

    /// <summary>
    /// Checks if CreamAPI files are available locally.
    /// </summary>
    public bool IsCreamApiAvailable()
    {
        var path = GetEffectiveCreamApiPath();
        return File.Exists(Path.Combine(path, "steam_api.dll")) ||
               File.Exists(Path.Combine(path, "steam_api64.dll"));
    }

    /// <summary>
    /// Gets the installed version of CreamAPI.
    /// </summary>
    public string? GetInstalledVersion()
    {
        try
        {
            var versionPath = System.IO.Path.Combine(GetEffectiveCreamApiPath(), "version.txt");
            if (File.Exists(versionPath))
            {
                return File.ReadAllText(versionPath).Trim();
            }
        }
        catch
        {
            // Ignore errors
        }
        return null;
    }

    /// <summary>
    /// Gets the latest CreamAPI download URL from GitHub releases API.
    /// </summary>
    private async Task<(string? Url, string? Sha256, string? Tag)?> GetLatestReleaseAsync()
    {
        try
        {
            LogService.Instance.Info("Fetching latest CreamAPI release from GitHub...", "CreamAPI");
            
            var apiUrl = AppConstants.DEFAULT_CREAMAPI_GITHUB_URL;
            var response = await _httpClient.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(response);
            var release = doc.RootElement;
            
            var tagName = release.GetProperty("tag_name").GetString();
            LogService.Instance.Info($"Found CreamAPI release: {tagName}", "CreamAPI");
            
            if (release.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    
                    // Look for zip file
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("CreamAPI", StringComparison.OrdinalIgnoreCase))
                    {
                        var url = asset.GetProperty("browser_download_url").GetString();
                        
                        // Check for checksum in release body
                        string? sha256 = null;
                        if (release.TryGetProperty("body", out var body))
                        {
                            var bodyText = body.GetString() ?? "";
                            // Look for SHA256 hash in release notes (format: SHA256: xxx or sha256: xxx)
                            var sha256Match = System.Text.RegularExpressions.Regex.Match(
                                bodyText, 
                                @"sha256[:\s]+([a-fA-F0-9]{64})", 
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (sha256Match.Success)
                            {
                                sha256 = sha256Match.Groups[1].Value.ToLowerInvariant();
                            }
                        }
                        
                        return (url, sha256, tagName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("Failed to fetch CreamAPI release", ex, "CreamAPI");
        }
        
        return null;
    }

    /// <summary>
    /// Downloads CreamAPI if not already available.
    /// </summary>
    public async Task<bool> DownloadCreamApiAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_creamApiPath);
            
            DownloadProgressChanged?.Invoke("Checking for latest CreamAPI release...", 5);
            
            // Try to get latest release from GitHub
            var releaseInfo = await GetLatestReleaseAsync();
            var fallbackUrl = _settingsService?.Settings.CreamApiFallbackUrl ?? AppConstants.DEFAULT_CREAMAPI_FALLBACK_URL;
            var downloadUrl = releaseInfo?.Url ?? fallbackUrl;
            var expectedSha256 = releaseInfo?.Sha256;
            
            LogService.Instance.Info($"Downloading CreamAPI from: {downloadUrl}", "CreamAPI");
            DownloadProgressChanged?.Invoke("Downloading CreamAPI...", 10);
            
            ct.ThrowIfCancellationRequested();
            
            var response = await _httpClient.GetAsync(downloadUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogService.Instance.Error($"CreamAPI download failed: {response.StatusCode}", category: "CreamAPI");
                return false;
            }

            var zipPath = System.IO.Path.Combine(_creamApiPath, "creamapi.zip");
            
            DownloadProgressChanged?.Invoke("Saving CreamAPI archive...", 50);
            
            await using (var fs = File.Create(zipPath))
            {
                await response.Content.CopyToAsync(fs, ct);
            }
            
            ct.ThrowIfCancellationRequested();

            // Verify checksum if available
            if (!string.IsNullOrEmpty(expectedSha256))
            {
                DownloadProgressChanged?.Invoke("Verifying checksum...", 60);
                var actualSha256 = await ChecksumUtils.ComputeSha256Async(zipPath, ct);
                
                if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Instance.Error($"Checksum mismatch! Expected: {expectedSha256}, Got: {actualSha256}", category: "CreamAPI");
                    File.Delete(zipPath);
                    return false;
                }
                
                LogService.Instance.Info("Checksum verified successfully", "CreamAPI");
            }

            DownloadProgressChanged?.Invoke("Extracting CreamAPI...", 70);
            
            ct.ThrowIfCancellationRequested();
            
            // Extract the archive
            using (var archive = ArchiveFactory.Open(zipPath))
            {
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    entry.WriteToDirectory(_creamApiPath, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                }
            }

            // Clean up zip
            File.Delete(zipPath);

            // Save version
            if (releaseInfo?.Tag != null)
            {
                var versionPath = System.IO.Path.Combine(_creamApiPath, "version.txt");
                await File.WriteAllTextAsync(versionPath, releaseInfo.Value.Tag, ct);
            }

            DownloadProgressChanged?.Invoke("CreamAPI ready!", 100);
            LogService.Instance.Info("CreamAPI downloaded and extracted successfully", "CreamAPI");
            return true;
        }
        catch (OperationCanceledException)
        {
            LogService.Instance.Warning("CreamAPI download cancelled", "CreamAPI");
            throw;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("CreamAPI download error", ex, "CreamAPI");
            return false;
        }
    }

    /// <summary>
    /// Applies CreamAPI to a game package directory.
    /// </summary>
    /// <param name="packagePath">The package directory containing the game</param>
    /// <param name="appId">The Steam AppID of the game</param>
    /// <param name="dlcList">List of DLC AppIDs to unlock</param>
    /// <param name="gameName">Name of the game for config</param>
    public async Task<bool> ApplyCreamApiAsync(string packagePath, int appId, List<int> dlcList, string gameName, CancellationToken ct = default)
    {
        try
        {
            if (!IsCreamApiAvailable())
            {
                var downloaded = await DownloadCreamApiAsync(ct);
                if (!downloaded) return false;
            }

            // Find where steam_api.dll is in the package
            var steamApiLocations = FindSteamApiFiles(packagePath);
            
            if (steamApiLocations.Count == 0)
            {
                LogService.Instance.Warning("No steam_api.dll found in package", "CreamAPI");
                return false;
            }

            foreach (var location in steamApiLocations)
            {
                ct.ThrowIfCancellationRequested();
                ApplyCreamApiToLocation(location, appId, dlcList, gameName);
            }
            
            LogService.Instance.Info($"CreamAPI applied to {steamApiLocations.Count} location(s)", "CreamAPI");
            return true;
        }
        catch (OperationCanceledException)
        {
            LogService.Instance.Warning("CreamAPI application cancelled", "CreamAPI");
            throw;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("Failed to apply CreamAPI", ex, "CreamAPI");
            return false;
        }
    }

    private List<string> FindSteamApiFiles(string path)
    {
        var results = new List<string>();
        
        try
        {
            var api32 = Directory.GetFiles(path, "steam_api.dll", SearchOption.AllDirectories);
            var api64 = Directory.GetFiles(path, "steam_api64.dll", SearchOption.AllDirectories);
            
            // Get unique directories containing steam_api files
            var dirs = api32.Concat(api64)
                           .Select(Path.GetDirectoryName)
                           .Where(d => d != null)
                           .Distinct()
                           .ToList();
            
            results.AddRange(dirs!);
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("Error finding steam_api files", ex, "CreamAPI");
        }
        
        return results;
    }

    private void ApplyCreamApiToLocation(string location, int appId, List<int> dlcList, string gameName)
    {
        var creamApiPath = GetEffectiveCreamApiPath();

        // Backup and replace steam_api.dll
        var api32Path = System.IO.Path.Combine(location, "steam_api.dll");
        var api64Path = System.IO.Path.Combine(location, "steam_api64.dll");
        
        if (File.Exists(api32Path))
        {
            var backupPath = System.IO.Path.Combine(location, "steam_api_o.dll");
            if (!File.Exists(backupPath))
                File.Move(api32Path, backupPath);
            
            var creamApi32 = System.IO.Path.Combine(creamApiPath, "steam_api.dll");
            if (File.Exists(creamApi32))
                File.Copy(creamApi32, api32Path, true);
        }
        
        if (File.Exists(api64Path))
        {
            var backupPath = System.IO.Path.Combine(location, "steam_api64_o.dll");
            if (!File.Exists(backupPath))
                File.Move(api64Path, backupPath);
            
            var creamApi64 = System.IO.Path.Combine(creamApiPath, "steam_api64.dll");
            if (File.Exists(creamApi64))
                File.Copy(creamApi64, api64Path, true);
        }
        
        // Create cream_api.ini
        var iniPath = System.IO.Path.Combine(location, "cream_api.ini");
        var iniContent = GenerateCreamApiIni(appId, dlcList, gameName);
        File.WriteAllText(iniPath, iniContent);
    }

    private string GenerateCreamApiIni(int appId, List<int> dlcList, string gameName)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("[steam]");
        sb.AppendLine($"; {gameName}");
        sb.AppendLine($"appid = {appId}");
        sb.AppendLine("unlockall = false");
        sb.AppendLine("orgapi = steam_api_o.dll");
        sb.AppendLine("orgapi64 = steam_api64_o.dll");
        sb.AppendLine();
        sb.AppendLine("[steam_misc]");
        sb.AppendLine("disableuseragentcheck = false");
        sb.AppendLine("disableleaderboardsinit = false");
        sb.AppendLine();
        sb.AppendLine("[dlc]");
        
        foreach (var dlcId in dlcList)
        {
            sb.AppendLine($"{dlcId} = DLC");
        }
        
        return sb.ToString();
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
/// Utility class for computing file checksums.
/// </summary>
public static class ChecksumUtils
{
    /// <summary>
    /// Computes the SHA256 hash of a file asynchronously.
    /// </summary>
    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        
        var buffer = new byte[81920]; // 80KB buffer
        int bytesRead;
        
        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, buffer, 0);
        }
        
        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        
        return BitConverter.ToString(sha256.Hash!).Replace("-", "").ToLowerInvariant();
    }
    
    /// <summary>
    /// Verifies a file's SHA256 hash matches the expected value.
    /// </summary>
    public static async Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default)
    {
        var actualHash = await ComputeSha256Async(filePath, ct);
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}
