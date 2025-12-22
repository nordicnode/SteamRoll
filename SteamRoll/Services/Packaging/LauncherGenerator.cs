using System.IO;
using SteamRoll.Models;

namespace SteamRoll.Services.Packaging;

/// <summary>
/// Creates launcher scripts for packages.
/// </summary>
public class LauncherGenerator
{
    /// <summary>
    /// Creates a launcher batch file.
    /// </summary>
    public void CreateLauncher(string packageDir, InstalledGame game, string? customArgs = null)
    {
        // Find likely game executables
        var exeFiles = Directory.GetFiles(packageDir, "*.exe", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Contains("redist", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Contains("setup", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Try to find the main game executable
        var mainExe = exeFiles.FirstOrDefault(f =>
            System.IO.Path.GetFileNameWithoutExtension(f).Equals(game.InstallDir, StringComparison.OrdinalIgnoreCase)) ??
            exeFiles.FirstOrDefault(f =>
            System.IO.Path.GetFileNameWithoutExtension(f).Contains(game.Name.Split(' ')[0], StringComparison.OrdinalIgnoreCase)) ??
            exeFiles.FirstOrDefault();

        if (mainExe != null)
        {
            var exeDir = System.IO.Path.GetDirectoryName(mainExe)!;
            var exeName = System.IO.Path.GetFileName(mainExe);
            var relativeExePath = System.IO.Path.GetRelativePath(packageDir, mainExe);

            // Check if this is a Source engine game
            var sourceGameInfo = DetectSourceEngineGame(packageDir, exeDir);

            if (sourceGameInfo != null)
            {
                // Create Source engine specific launcher
                // Prefer hl2.exe if it exists in the package root
                var hl2Path = System.IO.Path.Combine(packageDir, "hl2.exe");
                var sourceExePath = relativeExePath;

                if (File.Exists(hl2Path))
                {
                    sourceExePath = "hl2.exe";
                }

                CreateSourceEngineLauncher(packageDir, game.Name, sourceExePath, sourceGameInfo.Value, customArgs);
            }
            else
            {
                // Standard launcher
                var relativeDir = System.IO.Path.GetRelativePath(packageDir, exeDir);
                var cdPath = relativeDir == "." ? "" : relativeDir;

                var args = !string.IsNullOrWhiteSpace(customArgs) ? $" {customArgs}" : "";

                // Windows Batch
                var launcherContent = $"""
                    @echo off
                    title {game.Name}
                    cd /d "%~dp0{cdPath}"
                    start "" "{exeName}"{args}
                    """;

                File.WriteAllText(Path.Combine(packageDir, "LAUNCH.bat"), launcherContent);

                // Linux Shell Script
                // Assuming standard Wine usage or just launching if native (but we're packaging exe, so likely wine)
                // We convert backslashes to forward slashes for Linux paths
                var linuxCdPath = cdPath.Replace("\\", "/");

                var shellScriptContent = $$"""
                    #!/bin/bash
                    # SteamRoll Launcher for {{game.Name}}

                    # Set working directory to script location + relative path
                    DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
                    cd "$DIR/{{linuxCdPath}}"

                    # Add current directory to library path (for Goldberg steam_api.so)
                    export LD_LIBRARY_PATH=".:$LD_LIBRARY_PATH"

                    echo "Launching {{game.Name}}..."

                    # Try to launch with wine if it's an exe, or directly if it's executable
                    if [[ "{{exeName}}" == *.exe ]]; then
                        if command -v wine &> /dev/null; then
                            wine "{{exeName}}"{{args}}
                        else
                            echo "Wine not found. Attempting to run directly..."
                            ./"{{exeName}}"{{args}}
                        fi
                    else
                         ./"{{exeName}}"{{args}}
                    fi
                    """;

                // Use LF line endings for Linux script
                shellScriptContent = shellScriptContent.Replace("\r\n", "\n");

                var shPath = Path.Combine(packageDir, "launch.sh");
                File.WriteAllText(shPath, shellScriptContent);
            }
        }
    }

    /// <summary>
    /// Detects if this is a Source engine game and returns the game content folder info.
    /// </summary>
    private (string ContentFolder, string ContentFolderRelativeToRoot)? DetectSourceEngineGame(string packageDir, string exeDir)
    {
        var options = new EnumerationOptions
        {
            MatchCasing = MatchCasing.CaseInsensitive,
            RecurseSubdirectories = true
        };

        var gameInfoFiles = Directory.GetFiles(packageDir, "gameinfo.txt", options);

        if (gameInfoFiles.Length == 0)
            return null;

        // Filter out common engine/system folders
        // Filter out common engine/system folders and sort by path depth (length)
        // We want the shallowest gameinfo.txt (closest to root) to avoid finding backups or deeply nested files
        var validGameInfo = gameInfoFiles
            .Select(path => new {
                Path = path,
                Dir = System.IO.Path.GetDirectoryName(path)!,
                Folder = new DirectoryInfo(Path.GetDirectoryName(path)!).Name
            })
            .Where(x => !x.Folder.Equals("platform", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.Folder.Equals("bin", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.Folder.Equals("hl2", StringComparison.OrdinalIgnoreCase)) // Skip base HL2 content
            // Count separators for depth to avoid array allocation overhead of Split()
            .OrderBy(x => x.Path.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
            .FirstOrDefault();

        if (validGameInfo == null)
            return null;

        // Get the folder name and its path relative to package root
        var relativeToRoot = System.IO.Path.GetRelativePath(packageDir, validGameInfo.Dir);

        var gameParam = validGameInfo.Folder;

        LogService.Instance.Info($"Source engine detected: content folder '{validGameInfo.Folder}' at '{relativeToRoot}'", "LauncherGenerator");

        return (gameParam, relativeToRoot);
    }

    /// <summary>
    /// Creates a launcher specifically for Source engine games.
    /// Source engine requires the working directory to be set correctly and -game to point to the content folder.
    /// </summary>
    private void CreateSourceEngineLauncher(string packageDir, string gameName, string relativeExePath, (string ContentFolder, string ContentFolderRelativeToRoot) sourceInfo, string? customArgs = null)
    {
        var args = !string.IsNullOrWhiteSpace(customArgs) ? $" {customArgs}" : "";

        // Windows Batch
        var launcherContent = $"""
            @echo off
            title {gameName}
            rem Source Engine Game Launcher
            rem Setting working directory to package root where game content folder exists
            cd /d "%~dp0"

            rem Launch game with -game pointing to content folder: {sourceInfo.ContentFolder}
            start "" "{relativeExePath}" -game "{sourceInfo.ContentFolderRelativeToRoot}"{args}
            """;

        File.WriteAllText(Path.Combine(packageDir, "LAUNCH.bat"), launcherContent);

        // Linux Shell Script
        var linuxRelativeExePath = relativeExePath.Replace("\\", "/");
        var linuxGamePath = sourceInfo.ContentFolderRelativeToRoot.Replace("\\", "/");

        var shellScriptContent = $$"""
            #!/bin/bash
            # SteamRoll Source Engine Launcher for {{gameName}}

            # Set working directory to package root
            DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
            cd "$DIR"

            # Add current directory to library path
            export LD_LIBRARY_PATH=".:$LD_LIBRARY_PATH"

            echo "Launching {{gameName}} (Source Engine)..."

            if command -v wine &> /dev/null; then
                wine "{{linuxRelativeExePath}}" -game "{{linuxGamePath}}"{{args}}
            else
                echo "Wine not found. Attempting to run directly..."
                ./"{{linuxRelativeExePath}}" -game "{{linuxGamePath}}"{{args}}
            fi
            """;

        // Use LF line endings
        shellScriptContent = shellScriptContent.Replace("\r\n", "\n");

        var shPath = Path.Combine(packageDir, "launch.sh");
        File.WriteAllText(shPath, shellScriptContent);

        LogService.Instance.Info($"Created Source engine launcher: {relativeExePath} -game \"{sourceInfo.ContentFolderRelativeToRoot}\"", "LauncherGenerator");
    }
}
