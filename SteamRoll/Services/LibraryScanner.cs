using System.IO;
using SteamRoll.Models;
using SteamRoll.Parsers;

namespace SteamRoll.Services;

/// <summary>
/// Scans Steam library folders for installed games and parses their metadata.
/// </summary>
public class LibraryScanner
{
    private readonly SteamLocator _steamLocator;

    public LibraryScanner(SteamLocator steamLocator)
    {
        _steamLocator = steamLocator;
    }

    /// <summary>
    /// Scans all Steam libraries and returns installed games.
    /// </summary>
    /// <returns>List of installed games with metadata.</returns>
    public List<InstalledGame> ScanAllLibraries()
    {
        var games = new List<InstalledGame>();
        var libraries = _steamLocator.GetLibraryFolders();

        foreach (var library in libraries)
        {
            var libraryGames = ScanLibrary(library);
            games.AddRange(libraryGames);
        }

        return games.OrderBy(g => g.Name).ToList();
    }

    /// <summary>
    /// Scans a single Steam library folder for installed games.
    /// </summary>
    /// <param name="libraryPath">Path to the Steam library folder.</param>
    /// <returns>List of games found in this library.</returns>
    public List<InstalledGame> ScanLibrary(string libraryPath)
    {
        var games = new List<InstalledGame>();
        var steamappsPath = System.IO.Path.Combine(libraryPath, "steamapps");

        if (!Directory.Exists(steamappsPath))
            return games;

        // Find all appmanifest files
        var manifestFiles = Directory.GetFiles(steamappsPath, "appmanifest_*.acf");

        foreach (var manifestPath in manifestFiles)
        {
            try
            {
                var game = ParseManifest(manifestPath, libraryPath);
                if (game != null)
                {
                    games.Add(game);
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Error parsing {manifestPath}: {ex.Message}", ex, "LibraryScanner");
            }
        }

        return games;
    }

    /// <summary>
    /// Parses a single appmanifest ACF file.
    /// </summary>
    private InstalledGame? ParseManifest(string manifestPath, string libraryPath)
    {
        var vdf = VdfParser.ParseFile(manifestPath);

        // Get AppState section (root of game manifest)
        var appState = VdfParser.GetSection(vdf, "AppState");
        if (appState == null)
            return null;

        // Extract required fields
        var appIdStr = VdfParser.GetValue(appState, "appid");
        var name = VdfParser.GetValue(appState, "name");
        var installDir = VdfParser.GetValue(appState, "installdir");
        var stateFlagsStr = VdfParser.GetValue(appState, "StateFlags");

        if (string.IsNullOrEmpty(appIdStr) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installDir))
            return null;

        if (!int.TryParse(appIdStr, out var appId))
            return null;

        var fullPath = System.IO.Path.Combine(libraryPath, "steamapps", "common", installDir);

        var game = new InstalledGame
        {
            AppId = appId,
            Name = name,
            InstallDir = installDir,
            FullPath = fullPath,
            LibraryPath = libraryPath
        };

        // Parse optional fields
        if (int.TryParse(stateFlagsStr, out var stateFlags))
            game.StateFlags = stateFlags;

        var sizeStr = VdfParser.GetValue(appState, "SizeOnDisk");
        if (long.TryParse(sizeStr, out var size))
            game.SizeOnDisk = size;

        var buildIdStr = VdfParser.GetValue(appState, "buildid");
        if (int.TryParse(buildIdStr, out var buildId))
            game.BuildId = buildId;

        var lastUpdatedStr = VdfParser.GetValue(appState, "LastUpdated");
        if (long.TryParse(lastUpdatedStr, out var timestamp) && timestamp > 0)
            game.LastUpdated = DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime;

        // Verify installation directory exists
        if (!Directory.Exists(fullPath))
        {
            // Mark as not fully installed if directory missing
            game.StateFlags = game.StateFlags & ~4;
        }

        return game;
    }

    /// <summary>
    /// Gets summary statistics for all scanned games.
    /// </summary>
    public (int total, int fullyInstalled, long totalSize) GetLibraryStats(List<InstalledGame> games)
    {
        return (
            games.Count,
            games.Count(g => g.IsFullyInstalled),
            games.Sum(g => g.SizeOnDisk)
        );
    }
}
