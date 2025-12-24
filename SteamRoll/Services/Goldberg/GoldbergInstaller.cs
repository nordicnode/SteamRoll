using System.IO;
using System.Net.Http;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;
using System.IO.Compression;

namespace SteamRoll.Services.Goldberg;

/// <summary>
/// Handles downloading and installing Goldberg Emulator.
/// </summary>
public class GoldbergInstaller
{
    private readonly HttpClient _httpClient;
    private readonly SettingsService? _settingsService;
    private readonly string _goldbergPath;

    public event Action<string, int>? ProgressChanged;

    public GoldbergInstaller(HttpClient httpClient, SettingsService? settingsService, string goldbergPath)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _goldbergPath = goldbergPath;
    }

    /// <summary>
    /// Gets the installed version of Goldberg Emulator.
    /// </summary>
    public string? GetInstalledVersion()
    {
        try
        {
            var versionPath = System.IO.Path.Combine(_goldbergPath, "version.txt");
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
    /// Downloads and extracts Goldberg Emulator automatically.
    /// </summary>
    public async Task<bool> DownloadGoldbergAsync(string? specificVersion = null)
    {
        try
        {
            ReportProgress("Checking for Goldberg releases...", 5);

            string? downloadUrl = null;
            string? version = null;

            // Try GitHub fork first (actively maintained with newer Steam SDK support)
            LogService.Instance.Debug("Attempting to fetch from GitHub (gbe_fork)...", "GoldbergInstaller");
            (downloadUrl, version) = await GetGitHubReleaseInfoAsync(specificVersion);

            if (string.IsNullOrEmpty(downloadUrl) && string.IsNullOrEmpty(specificVersion))
            {
                // Fallback to original GitLab (only if no specific version requested)
                LogService.Instance.Warning("GitHub failed, trying original GitLab...", "GoldbergInstaller");
                (downloadUrl, version) = await GetGitLabReleaseInfoAsync();
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                LogService.Instance.Error("Could not find Goldberg download URL from any source", category: "GoldbergInstaller");
                ReportProgress("Download failed - no URL found", 0);
                return false;
            }

            LogService.Instance.Info($"Download URL: {downloadUrl}, Version: {version}", "GoldbergInstaller");

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
                LogService.Instance.Info($"Saved version {version} to {versionPath}", "GoldbergInstaller");
            }

            // Cleanup
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
                LogService.Instance.Debug($"Temp cleanup failed (non-critical): {cleanupEx.Message}", "GoldbergInstaller");
            }


            ReportProgress("Goldberg Emulator ready!", 100);
            // Check if files exist
            return File.Exists(Path.Combine(_goldbergPath, "steam_api.dll")) &&
                   File.Exists(Path.Combine(_goldbergPath, "steam_api64.dll"));
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error downloading Goldberg: {ex.Message}", ex, "GoldbergInstaller");
            return false;
        }
    }

    /// <summary>
    /// Gets available versions from GitHub releases.
    /// </summary>
    public async Task<List<string>> GetAvailableVersionsAsync()
    {
        var versions = new List<string>();
        try
        {
            var apiUrl = _settingsService?.Settings.GoldbergGitHubUrl ?? AppConstants.DEFAULT_GOLDBERG_GITHUB_URL;
            // The default URL might be /releases/latest, we want /releases for list
            if (apiUrl.EndsWith("/latest"))
                apiUrl = apiUrl.Substring(0, apiUrl.Length - 7);

            var response = await HttpRetryHelper.ExecuteWithRetryAsync(
                () => _httpClient.GetStringAsync(apiUrl),
                "GitHub Releases API",
                maxRetries: 2);

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var release in root.EnumerateArray())
                {
                    if (release.TryGetProperty("tag_name", out var tagProp))
                    {
                        var tag = tagProp.GetString()?.TrimStart('v');
                        if (!string.IsNullOrEmpty(tag)) versions.Add(tag);
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("tag_name", out var tagProp))
                {
                    var tag = tagProp.GetString()?.TrimStart('v');
                    if (!string.IsNullOrEmpty(tag)) versions.Add(tag);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to fetch available versions: {ex.Message}", "GoldbergInstaller");
        }
        return versions;
    }

    private async Task<(string? url, string? version)> GetGitHubReleaseInfoAsync(string? specificVersion = null)
    {
        try
        {
            LogService.Instance.Debug($"Fetching GitHub releases{(specificVersion != null ? $" for version {specificVersion}" : "")}...", "GoldbergInstaller");
            var apiUrl = _settingsService?.Settings.GoldbergGitHubUrl ?? AppConstants.DEFAULT_GOLDBERG_GITHUB_URL;

            if (!string.IsNullOrEmpty(specificVersion))
            {
                if (apiUrl.EndsWith("/latest"))
                {
                    apiUrl = apiUrl.Replace("/latest", $"/tags/v{specificVersion}");
                }
            }

            var response = await HttpRetryHelper.ExecuteWithRetryAsync(
                () => _httpClient.GetStringAsync(apiUrl),
                "GitHub Releases API",
                maxRetries: 2);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            JsonElement releaseElement = root;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (!string.IsNullOrEmpty(specificVersion))
                {
                     bool found = false;
                     foreach (var item in root.EnumerateArray())
                     {
                         if (item.TryGetProperty("tag_name", out var t) && (t.GetString() == specificVersion || t.GetString() == $"v{specificVersion}"))
                         {
                             releaseElement = item;
                             found = true;
                             break;
                         }
                     }
                     if (!found) return (null, null);
                }
                else
                {
                    if (root.GetArrayLength() > 0)
                        releaseElement = root[0];
                    else
                        return (null, null);
                }
            }

            var version = releaseElement.TryGetProperty("tag_name", out var tagProp)
                ? tagProp.GetString()?.TrimStart('v')
                : null;

            if (releaseElement.TryGetProperty("assets", out var assets))
            {
                // First pass: Look for specific naming patterns (most reliable)
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";

                    if (name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("release", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("debug", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("migrate", StringComparison.OrdinalIgnoreCase) &&
                        (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        var url = asset.GetProperty("browser_download_url").GetString();
                        return (url, version);
                    }
                }

                // Second pass: Look for emu-win pattern
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.StartsWith("emu-win", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("migrate", StringComparison.OrdinalIgnoreCase) &&
                        (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        var url = asset.GetProperty("browser_download_url").GetString();
                        return (url, version);
                    }
                }

                // Fallback: Grab any .zip or .7z that isn't source code
                // This handles cases where maintainer changes naming convention
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if ((name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || 
                         name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) &&
                        !name.Contains("source", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("mac", StringComparison.OrdinalIgnoreCase))
                    {
                        var url = asset.GetProperty("browser_download_url").GetString();
                        LogService.Instance.Debug($"Using fallback asset: {name}", "GoldbergInstaller");
                        return (url, version);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"GitHub API error: {ex.Message}", "GoldbergInstaller");
        }
        return (null, null);
    }

    private async Task<(string? url, string? version)> GetGitLabReleaseInfoAsync()
    {
        try
        {
            LogService.Instance.Debug("Fetching GitLab releases...", "GoldbergInstaller");
            var apiUrl = _settingsService?.Settings.GoldbergGitLabUrl ?? AppConstants.DEFAULT_GOLDBERG_GITLAB_URL;
            var response = await HttpRetryHelper.ExecuteWithRetryAsync(
                () => _httpClient.GetStringAsync(apiUrl),
                "GitLab Releases API",
                maxRetries: 2);
            using var doc = JsonDocument.Parse(response);
            var releases = doc.RootElement;

            if (releases.ValueKind == JsonValueKind.Array && releases.GetArrayLength() > 0)
            {
                var latestRelease = releases[0];
                var version = latestRelease.TryGetProperty("tag_name", out var tagProp)
                    ? tagProp.GetString()?.TrimStart('v')
                    : null;

                if (latestRelease.TryGetProperty("assets", out var assets) &&
                    assets.TryGetProperty("links", out var links))
                {
                    foreach (var link in links.EnumerateArray())
                    {
                        var name = link.GetProperty("name").GetString() ?? "";

                        string? url = null;
                        if (link.TryGetProperty("direct_asset_url", out var directUrl))
                            url = directUrl.GetString();
                        else if (link.TryGetProperty("url", out var normalUrl))
                            url = normalUrl.GetString();

                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            (name.Contains("Goldberg", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("Emu", StringComparison.OrdinalIgnoreCase)))
                        {
                            return (url, version);
                        }
                    }

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
            LogService.Instance.Warning($"GitLab API error: {ex.Message}", "GoldbergInstaller");
        }
        return (null, null);
    }

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

    private void CopyGoldbergFiles(string sourcePath, string destPath)
    {
        var filesToCopy = new[] { "steam_api.dll", "steam_api64.dll", "steamclient.dll", "steamclient64.dll" };

        foreach (var fileName in filesToCopy)
        {
            var files = Directory.GetFiles(sourcePath, fileName, SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                var sourceFile = files
                    .OrderByDescending(f => f.Contains("release", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => f.Contains("experimental", StringComparison.OrdinalIgnoreCase))
                    .First();

                File.Copy(sourceFile, System.IO.Path.Combine(destPath, fileName), overwrite: true);
                LogService.Instance.Info($"Copied: {fileName}", "GoldbergInstaller");
            }
        }
    }

    private static string CleanupVersionString(string rawVersion)
    {
        if (rawVersion.Contains('-'))
        {
            var parts = rawVersion.Split('-');
            if (parts.Length >= 2)
            {
                var datePart = parts[^1].Replace('_', '.');
                return datePart;
            }
        }
        return rawVersion;
    }

    private void ReportProgress(string status, int percentage)
    {
        ProgressChanged?.Invoke(status, percentage);
    }
}
