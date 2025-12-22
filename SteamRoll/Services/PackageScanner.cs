using System.IO;
using SteamRoll.Models;
using SteamRoll.Parsers;

namespace SteamRoll.Services;

/// <summary>
/// Scans the package output directory for processed games.
/// </summary>
public class PackageScanner
{
    private readonly SettingsService _settingsService;

    public PackageScanner(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Scans the configured output directory for packaged games.
    /// </summary>
    /// <returns>List of installed games representing packages.</returns>
    public List<InstalledGame> ScanPackages(CancellationToken ct = default)
    {
        var games = new List<InstalledGame>();
        var outputPath = _settingsService.Settings.OutputPath;

        if (!Directory.Exists(outputPath))
        {
            return games;
        }

        try
        {
            var directories = Directory.GetDirectories(outputPath);
            foreach (var dir in directories)
            {
                ct.ThrowIfCancellationRequested();
                var game = ParsePackage(dir);
                if (game != null)
                {
                    games.Add(game);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error scanning packages: {ex.Message}", ex, "PackageScanner");
        }

        return games.OrderBy(g => g.Name).ToList();
    }

    private InstalledGame? ParsePackage(string packagePath)
    {
        // A valid package should have LAUNCH.bat OR Goldberg markers
        var hasLaunchBat = File.Exists(System.IO.Path.Combine(packagePath, "LAUNCH.bat"));
        var hasSteamSettings = Directory.Exists(System.IO.Path.Combine(packagePath, "steam_settings"));
        var hasSteamAppId = File.Exists(System.IO.Path.Combine(packagePath, "steam_appid.txt"));
        var hasSteamrollJson = File.Exists(System.IO.Path.Combine(packagePath, "steamroll.json"));
        
        if (!hasLaunchBat && !hasSteamSettings && !hasSteamAppId && !hasSteamrollJson)
        {
            return null;
        }

        var dirName = System.IO.Path.GetFileName(packagePath);
        int appId = 0;
        string name = dirName;

        // Try to find AppID - check both root and steam_settings directory
        // GoldbergService writes to root, but some packages have it in steam_settings
        var appIdPaths = new[]
        {
            System.IO.Path.Combine(packagePath, "steam_appid.txt"),  // Root (where GoldbergService puts it)
            System.IO.Path.Combine(packagePath, "steam_settings", "steam_appid.txt")  // steam_settings subdirectory
        };
        
        foreach (var appIdPath in appIdPaths)
        {
            if (File.Exists(appIdPath))
            {
                var content = File.ReadAllText(appIdPath).Trim();
                if (int.TryParse(content, out var id))
                {
                    appId = id;
                    LogService.Instance.Debug($"Found AppId {appId} for package {name} at {appIdPath}", "PackageScanner");
                    break;
                }
                else
                {
                    LogService.Instance.Warning($"Could not parse AppId from {appIdPath}: '{content}'", "PackageScanner");
                }
            }
        }
        
        if (appId == 0)
        {
            LogService.Instance.Warning($"No steam_appid.txt found for package {name}", "PackageScanner");
        }

        bool isReceived = false;
        var receivedMarkerPath = System.IO.Path.Combine(packagePath, ".steamroll_received");
        if (File.Exists(receivedMarkerPath))
        {
            isReceived = true;
        }

        long size = 0;
        try
        {
            // Approximate size
             size = new DirectoryInfo(packagePath).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Could not calculate package size for {packagePath}: {ex.Message}", "PackageScanner");
        }

        var game = new InstalledGame
        {
            AppId = appId,
            Name = name,
            InstallDir = dirName,
            FullPath = packagePath,
            LibraryPath = _settingsService.Settings.OutputPath,
            SizeOnDisk = size,
            IsPackaged = true,
            PackagePath = packagePath,
            IsReceivedPackage = isReceived,
            StateFlags = 4 // Considered installed
        };

        return game;
    }
}
