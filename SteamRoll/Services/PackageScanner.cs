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
        // A valid package must have LAUNCH.bat
        if (!File.Exists(System.IO.Path.Combine(packagePath, "LAUNCH.bat")))
        {
            return null;
        }

        var dirName = System.IO.Path.GetFileName(packagePath);
        int appId = 0;
        string name = dirName;

        // Try to find AppID from steam_settings (Goldberg)
        var appIdPath = System.IO.Path.Combine(packagePath, "steam_settings", "steam_appid.txt");
        if (File.Exists(appIdPath))
        {
            if (int.TryParse(File.ReadAllText(appIdPath).Trim(), out var id))
            {
                appId = id;
            }
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
        catch { }

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
