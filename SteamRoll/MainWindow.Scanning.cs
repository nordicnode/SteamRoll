using System.IO;
using System.Windows;
using SteamRoll.Controls;
using SteamRoll.Models;
using SteamRoll.Services;

namespace SteamRoll;

public partial class MainWindow
{
    private async Task AnalyzeGamesForDrmAsync(List<InstalledGame> games)
    {
        var total = games.Count;
        var completed = 0;

        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };

        await Task.Run(() =>
        {
            Parallel.ForEach(games, options, game =>
            {
                game.Analyze();

                var current = Interlocked.Increment(ref completed);

                if (current % 10 == 0 || current == total)
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetStatus($"Analyzing DRM: {current}/{total} games...");
                    });
                }
            });
        });
    }

    private async Task FetchDlcForGamesAsync(List<InstalledGame> games)
    {
        var total = games.Count;
        var completed = 0;
        var gamesWithDlc = 0;

        foreach (var game in games)
        {
            try
            {
                var dlcList = await _dlcService.GetDlcListAsync(game.AppId);
                game.AvailableDlc = dlcList;
                game.DlcFetched = true;

                if (dlcList.Count > 0 && !string.IsNullOrEmpty(game.LibraryPath))
                {
                    _dlcService.CheckInstalledDlc(game.FullPath, game.LibraryPath, dlcList);
                    gamesWithDlc++;
                }

                completed++;

                if (completed % 5 == 0 || completed == total)
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetStatus($"Fetching DLC: {completed}/{total} games ({gamesWithDlc} with DLC)...");
                        GameLibraryViewControl.RefreshList();
                    });
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Error fetching DLC for {game.Name}", ex);
                game.DlcFetched = true; // Mark as fetched even on error
                completed++;
            }
        }

        Dispatcher.Invoke(() =>
        {
            var allGames = _libraryManager.Games;
            var packageableCount = allGames.Count(g => g.IsPackageable);
            var totalDlc = allGames.Sum(g => g.TotalDlcCount);
            SetStatus($"✓ {allGames.Count} games • {packageableCount} packageable • {totalDlc} DLC available");
            GameLibraryViewControl.RefreshList();

            foreach (var game in games)
            {
                _cacheService.UpdateCache(game);
            }
            _cacheService.SaveCache();
        });
    }

    private void CheckExistingPackages(List<InstalledGame> games)
    {
        string[] packageDirs = Array.Empty<string>();

        if (Directory.Exists(_outputPath))
        {
            try
            {
                packageDirs = Directory.GetDirectories(_outputPath);
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Error scanning output directory: {ex.Message}", ex, "MainWindow");
            }
        }

        var packageMap = ScanForPackages(packageDirs);

        foreach (var game in games)
        {
            var wasPackaged = game.IsPackaged;
            game.IsPackaged = false;
            game.PackagePath = null;
            game.LastPackaged = null;

            if (packageDirs.Length == 0) continue;

            string? matchingDir = null;
            if (packageMap.TryGetValue(game.AppId, out var appIdMatch))
            {
                matchingDir = appIdMatch;
            }

            if (matchingDir == null)
            {
                var sanitizedName = FormatUtils.SanitizeFileName(game.Name);
                if (!string.IsNullOrEmpty(sanitizedName))
                {
                    matchingDir = packageDirs.FirstOrDefault(d =>
                        Path.GetFileName(d).Equals(sanitizedName, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (matchingDir != null)
            {
                var hasGoldberg = Directory.Exists(Path.Combine(matchingDir, "steam_settings")) ||
                                 File.Exists(Path.Combine(matchingDir, "steam_api_o.dll")) ||
                                 File.Exists(Path.Combine(matchingDir, "steam_api64_o.dll"));

                if (hasGoldberg)
                {
                    game.IsPackaged = true;
                    game.PackagePath = matchingDir;
                    game.LastPackaged = Directory.GetLastWriteTime(matchingDir);

                    game.IsReceivedPackage = File.Exists(Path.Combine(matchingDir, ".steamroll_received"));

                    var metadataPath = Path.Combine(matchingDir, "steamroll.json");
                    if (File.Exists(metadataPath))
                    {
                        try
                        {
                            var json = File.ReadAllText(metadataPath);
                            var metadata = System.Text.Json.JsonSerializer.Deserialize<SteamRoll.Models.PackageMetadata>(json);
                            if (metadata != null)
                            {
                                game.PackageBuildId = metadata.BuildId;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.Instance.Warning($"Failed to read metadata for {game.Name}: {ex.Message}", "MainWindow");
                        }
                    }
                }
            }

            if (wasPackaged != game.IsPackaged)
            {
                _cacheService.UpdateCache(game);
            }
        }

        _cacheService.SaveCache();
    }

    private Dictionary<int, string> ScanForPackages(string[] packageDirs)
    {
        var map = new Dictionary<int, string>();
        if (packageDirs == null) return map;

        foreach (var dir in packageDirs)
        {
            try
            {
                int appId = -1;

                var rootAppIdPath = Path.Combine(dir, "steam_appid.txt");
                if (File.Exists(rootAppIdPath))
                {
                    if (int.TryParse(File.ReadLines(rootAppIdPath).FirstOrDefault() ?? "", out var id)) appId = id;
                }

                if (appId == -1)
                {
                    var goldbergIdPath = Path.Combine(dir, "steam_settings", "steam_appid.txt");
                    if (File.Exists(goldbergIdPath))
                    {
                        if (int.TryParse(File.ReadLines(goldbergIdPath).FirstOrDefault() ?? "", out var id)) appId = id;
                    }
                }

                if (appId == -1)
                {
                    var metadataPath = Path.Combine(dir, "steamroll.json");
                    if (File.Exists(metadataPath))
                    {
                        try
                        {
                            var json = File.ReadAllText(metadataPath);
                            var metadata = System.Text.Json.JsonSerializer.Deserialize<SteamRoll.Models.PackageMetadata>(json);
                            if (metadata != null && metadata.AppId > 0)
                            {
                                appId = metadata.AppId;
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                if (appId > 0 && !map.ContainsKey(appId))
                {
                    map.Add(appId, dir);
                }

            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Error scanning package {dir}", ex);
            }
        }
        return map;
    }

    private List<InstalledGame> ScanReceivedPackages(List<InstalledGame> existingGames)
    {
        var receivedGames = new List<InstalledGame>();

        if (!Directory.Exists(_outputPath))
            return receivedGames;

        try
        {
            var existingNames = new HashSet<string>(
                existingGames.Select(g => FormatUtils.SanitizeFileName(g.Name)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var dir in Directory.GetDirectories(_outputPath))
            {
                var dirName = Path.GetFileName(dir);

                if (existingNames.Contains(dirName))
                    continue;

                var markerPath = Path.Combine(dir, ".steamroll_received");
                if (!File.Exists(markerPath))
                    continue;

                var hasGoldberg = Directory.Exists(Path.Combine(dir, "steam_settings")) ||
                                 File.Exists(Path.Combine(dir, "steam_api_o.dll")) ||
                                 File.Exists(Path.Combine(dir, "steam_api64_o.dll"));

                if (!hasGoldberg)
                    continue;

                var appId = 0;
                var steamAppIdPath = Path.Combine(dir, "steam_settings", "steam_appid.txt");
                if (File.Exists(steamAppIdPath))
                {
                    var appIdContent = File.ReadAllText(steamAppIdPath).Trim();
                    int.TryParse(appIdContent, out appId);
                }

                long sizeOnDisk = 0;
                try
                {
                    sizeOnDisk = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);
                }
                catch (Exception sizeEx)
                {
                    LogService.Instance.Debug($"Could not calculate size for {dir}: {sizeEx.Message}", "MainWindow");
                }

                var receivedGame = new InstalledGame
                {
                    AppId = appId,
                    Name = dirName,
                    InstallDir = dirName,
                    FullPath = dir,
                    LibraryPath = _outputPath,
                    SizeOnDisk = sizeOnDisk,
                    StateFlags = 4,
                    IsPackaged = true,
                    PackagePath = dir,
                    IsReceivedPackage = true,
                    LastPackaged = Directory.GetLastWriteTime(dir),
                    LastAnalyzed = DateTime.Now
                };

                receivedGames.Add(receivedGame);
                LogService.Instance.Info($"Found received package: {dirName} (AppID: {appId})", "MainWindow");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error scanning received packages: {ex.Message}", ex, "MainWindow");
        }

        return receivedGames;
    }
}
