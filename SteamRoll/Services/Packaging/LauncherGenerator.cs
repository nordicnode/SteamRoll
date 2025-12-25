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

                // Windows Batch - with playtime tracking via marker file
                var launcherContent = $"""
                    @echo off
                    title {game.Name}
                    
                    rem Create launch marker for SteamRoll playtime tracking
                    echo {game.AppId}|{DateTime.Now:o} > "%~dp0.steamroll_playing"
                    
                    cd /d "%~dp0{cdPath}"
                    
                    rem Start game and wait for it to exit
                    "{exeName}"{args}
                    
                    rem Remove marker when game exits
                    del "%~dp0.steamroll_playing" 2>nul
                    """;

                File.WriteAllText(Path.Combine(packageDir, "LAUNCH.bat"), launcherContent);

                // Linux Shell Script
                // Assuming standard Wine usage or just launching if native (but we're packaging exe, so likely wine)
                // We convert backslashes to forward slashes for Linux paths
                var linuxCdPath = cdPath.Replace("\\", "/");

                var shellScriptContent = $$"""
                    #!/bin/bash
                    # SteamRoll Launcher for {{game.Name}}
                    
                    # Get script directory
                    DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
                    
                    # Cleanup function to remove marker on exit
                    cleanup() {
                        rm -f "$DIR/.steamroll_playing"
                    }
                    trap cleanup EXIT
                    
                    # Create launch marker for SteamRoll playtime tracking
                    echo "{{game.AppId}}|$(date -Iseconds)" > "$DIR/.steamroll_playing"
                    
                    # Set working directory
                    cd "$DIR/{{linuxCdPath}}"

                    # Add current directory to library path (for Goldberg steam_api.so)
                    export LD_LIBRARY_PATH=".:$LD_LIBRARY_PATH"

                    echo "Launching {{game.Name}}..."

                    # Find wine/proton with fallback paths for Steam Deck and Flatpak
                    find_wine() {
                        # Check standard wine
                        if command -v wine &> /dev/null; then
                            echo "wine"
                            return 0
                        fi
                        
                        # Check common local paths
                        for wine_path in "$HOME/.local/bin/wine" "/usr/local/bin/wine"; do
                            if [ -x "$wine_path" ]; then
                                echo "$wine_path"
                                return 0
                            fi
                        done
                        
                        # Steam Deck / SteamOS: Try Proton
                        STEAM_PROTON="$HOME/.steam/steam/steamapps/common"
                        if [ -d "$STEAM_PROTON" ]; then
                            # Find newest Proton version
                            PROTON_DIR=$(ls -d "$STEAM_PROTON/Proton"* 2>/dev/null | sort -V | tail -1)
                            if [ -n "$PROTON_DIR" ] && [ -x "$PROTON_DIR/dist/bin/wine" ]; then
                                echo "$PROTON_DIR/dist/bin/wine"
                                return 0
                            fi
                        fi
                        
                        # Flatpak Steam location
                        FLATPAK_PROTON="$HOME/.var/app/com.valvesoftware.Steam/.steam/steam/steamapps/common"
                        if [ -d "$FLATPAK_PROTON" ]; then
                            PROTON_DIR=$(ls -d "$FLATPAK_PROTON/Proton"* 2>/dev/null | sort -V | tail -1)
                            if [ -n "$PROTON_DIR" ] && [ -x "$PROTON_DIR/dist/bin/wine" ]; then
                                echo "$PROTON_DIR/dist/bin/wine"
                                return 0
                            fi
                        fi
                        
                        return 1
                    }

                    # Try to launch with wine if it's an exe
                    if [[ "{{exeName}}" == *.exe ]]; then
                        WINE_CMD=$(find_wine)
                        if [ -n "$WINE_CMD" ]; then
                            "$WINE_CMD" "{{exeName}}"{{args}}
                        else
                            echo "ERROR: Wine/Proton not found."
                            echo "Install wine or Steam's Proton compatibility tool."
                            echo ""
                            echo "Searched locations:"
                            echo "  - wine (system PATH)"
                            echo "  - ~/.local/bin/wine"
                            echo "  - ~/.steam/steam/steamapps/common/Proton*/dist/bin/wine"
                            echo "  - ~/.var/app/com.valvesoftware.Steam/... (Flatpak)"
                            exit 1
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

            # Find wine/proton with fallback paths for Steam Deck and Flatpak
            find_wine() {
                # Check standard wine
                if command -v wine &> /dev/null; then
                    echo "wine"
                    return 0
                fi
                
                # Check common local paths
                for wine_path in "$HOME/.local/bin/wine" "/usr/local/bin/wine"; do
                    if [ -x "$wine_path" ]; then
                        echo "$wine_path"
                        return 0
                    fi
                done
                
                # Steam Deck / SteamOS: Try Proton
                STEAM_PROTON="$HOME/.steam/steam/steamapps/common"
                if [ -d "$STEAM_PROTON" ]; then
                    PROTON_DIR=$(ls -d "$STEAM_PROTON/Proton"* 2>/dev/null | sort -V | tail -1)
                    if [ -n "$PROTON_DIR" ] && [ -x "$PROTON_DIR/dist/bin/wine" ]; then
                        echo "$PROTON_DIR/dist/bin/wine"
                        return 0
                    fi
                fi
                
                # Flatpak Steam location
                FLATPAK_PROTON="$HOME/.var/app/com.valvesoftware.Steam/.steam/steam/steamapps/common"
                if [ -d "$FLATPAK_PROTON" ]; then
                    PROTON_DIR=$(ls -d "$FLATPAK_PROTON/Proton"* 2>/dev/null | sort -V | tail -1)
                    if [ -n "$PROTON_DIR" ] && [ -x "$PROTON_DIR/dist/bin/wine" ]; then
                        echo "$PROTON_DIR/dist/bin/wine"
                        return 0
                    fi
                fi
                
                return 1
            }

            WINE_CMD=$(find_wine)
            if [ -n "$WINE_CMD" ]; then
                "$WINE_CMD" "{{linuxRelativeExePath}}" -game "{{linuxGamePath}}"{{args}}
            else
                echo "ERROR: Wine/Proton not found."
                echo "Install wine or Steam's Proton compatibility tool."
                exit 1
            fi
            """;

        // Use LF line endings
        shellScriptContent = shellScriptContent.Replace("\r\n", "\n");

        var shPath = Path.Combine(packageDir, "launch.sh");
        File.WriteAllText(shPath, shellScriptContent);

        LogService.Instance.Info($"Created Source engine launcher: {relativeExePath} -game \"{sourceInfo.ContentFolderRelativeToRoot}\"", "LauncherGenerator");
    }
}
