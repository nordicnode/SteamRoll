using System.IO;
using SteamRoll.Models;
using SteamRoll.Parsers;

namespace SteamRoll.Services;

/// <summary>
/// Checks if packaged games have updates available by comparing
/// package Build IDs against current Steam library Build IDs.
/// </summary>
public class PackageUpdateChecker
{
    private readonly SteamLocator _steamLocator;

    public PackageUpdateChecker(SteamLocator steamLocator)
    {
        _steamLocator = steamLocator;
    }

    /// <summary>
    /// Cross-references packages with Steam library games to populate current BuildId.
    /// This enables the UpdateAvailable property to work correctly.
    /// </summary>
    /// <param name="packages">List of packaged games (IsPackaged = true).</param>
    /// <param name="steamGames">List of all games including Steam library games.</param>
    public void PopulateSteamBuildIds(List<InstalledGame> packages, List<InstalledGame> steamGames)
    {
        // Build a lookup from AppId to Steam BuildId
        var steamBuildIds = steamGames
            .Where(g => !g.IsPackaged && g.BuildId > 0)
            .GroupBy(g => g.AppId)
            .ToDictionary(g => g.Key, g => g.First().BuildId);

        foreach (var package in packages.Where(p => p.IsPackaged && p.AppId > 0))
        {
            if (steamBuildIds.TryGetValue(package.AppId, out var steamBuildId))
            {
                package.BuildId = steamBuildId;
                
                LogService.Instance.Debug(
                    $"Package {package.Name}: PackageBuildId={package.PackageBuildId}, SteamBuildId={steamBuildId}, UpdateAvailable={package.UpdateAvailable}",
                    "PackageUpdateChecker");
            }
        }
    }

    /// <summary>
    /// Gets the current Steam Build ID for an AppId by directly reading the ACF manifest.
    /// Useful for looking up Build IDs without a full library scan.
    /// </summary>
    /// <param name="appId">Steam App ID to look up.</param>
    /// <returns>Current Build ID, or null if not found.</returns>
    public int? GetSteamBuildId(int appId)
    {
        var libraries = _steamLocator.GetLibraryFolders();

        foreach (var library in libraries)
        {
            var manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var vdf = VdfParser.ParseFile(manifestPath);
                var appState = VdfParser.GetSection(vdf, "AppState");
                
                if (appState != null)
                {
                    var buildIdStr = VdfParser.GetValue(appState, "buildid");
                    if (int.TryParse(buildIdStr, out var buildId))
                    {
                        return buildId;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning($"Error reading manifest for AppId {appId}: {ex.Message}", "PackageUpdateChecker");
            }
        }

        return null;
    }

    /// <summary>
    /// Gets a summary of available updates for packaged games.
    /// </summary>
    /// <param name="packages">List of packaged games to check.</param>
    /// <returns>Tuple with count of updates available.</returns>
    public int GetUpdateSummary(List<InstalledGame> packages)
    {
        return packages.Count(p => p.UpdateAvailable);
    }

    /// <summary>
    /// Finds the corresponding Steam installation for a packaged game.
    /// </summary>
    /// <param name="package">The packaged game.</param>
    /// <param name="steamGames">List of all games from Steam library.</param>
    /// <returns>The matching Steam installation, or null if not found.</returns>
    public InstalledGame? FindSteamInstallation(InstalledGame package, List<InstalledGame> steamGames)
    {
        if (package.AppId <= 0)
            return null;

        return steamGames.FirstOrDefault(g => 
            !g.IsPackaged && 
            g.AppId == package.AppId && 
            g.IsFullyInstalled);
    }
}
