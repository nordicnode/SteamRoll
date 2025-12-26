using System.IO;
using System.Text;
using System.Text.Json;
using SteamRoll.Models;

namespace SteamRoll.Services.Packaging;

/// <summary>
/// Creates launcher metadata and README for packages.
/// </summary>
public class LauncherGenerator
{
    private class LauncherConfig
    {
        public string Executable { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string AppId { get; set; } = "";
    }

    /// <summary>
    /// Creates launcher metadata (JSON) and README for a package.
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
            LauncherConfig config;

            if (sourceGameInfo != null)
            {
                var hl2Path = System.IO.Path.Combine(packageDir, "hl2.exe");
                var sourceExePath = File.Exists(hl2Path) ? "hl2.exe" : relativeExePath;
                var sourceArgs = !string.IsNullOrWhiteSpace(customArgs) ? $" {customArgs}" : "";
                
                config = new LauncherConfig
                {
                    Executable = sourceExePath,
                    Arguments = $"-game \"{sourceGameInfo.Value.ContentFolderRelativeToRoot}\"{sourceArgs}",
                    WorkingDirectory = ".",
                    AppId = game.AppId.ToString()
                };
            }
            else
            {
                var relativeDir = System.IO.Path.GetRelativePath(packageDir, exeDir);
                var cdPath = relativeDir == "." ? "" : relativeDir;

                config = new LauncherConfig
                {
                    Executable = relativeExePath,
                    Arguments = customArgs ?? "",
                    WorkingDirectory = cdPath,
                    AppId = game.AppId.ToString()
                };
            }

            // 1. Write the Config JSON (metadata for SteamRoll)
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            var jsonContent = JsonSerializer.Serialize(config, jsonOptions);
            File.WriteAllText(Path.Combine(packageDir, "launcher.json"), jsonContent);

            // 2. Create Linux Shell Script (for Wine/Proton users)
            // Note: README.txt is created by PackageMetadataGenerator with full game details
            CreateLinuxLauncher(packageDir, game, relativeExePath, sourceGameInfo, customArgs);
        }
    }

    private void CreateLinuxLauncher(string packageDir, InstalledGame game, string relativeExePath, 
        (string ContentFolder, string ContentFolderRelativeToRoot)? sourceInfo, string? customArgs)
    {
        var exeName = Path.GetFileName(relativeExePath);
        var args = !string.IsNullOrWhiteSpace(customArgs) ? $" {customArgs}" : "";
        var linuxRelativeExePath = relativeExePath.Replace("\\", "/");
        var shellScriptContent = "";
        
        if (sourceInfo != null)
        {
            var linuxGamePath = sourceInfo.Value.ContentFolderRelativeToRoot.Replace("\\", "/");

            shellScriptContent = $$"""
                #!/bin/bash
                # SteamRoll Source Engine Launcher for {{game.Name}}
                
                DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
                cd "$DIR"
                export LD_LIBRARY_PATH=".:$LD_LIBRARY_PATH"
                
                echo "Launching {{game.Name}} (Source Engine)..."
                
                find_wine() {
                    if command -v wine &> /dev/null; then echo "wine"; return 0; fi
                    for wine_path in "$HOME/.local/bin/wine" "/usr/local/bin/wine"; do if [ -x "$wine_path" ]; then echo "$wine_path"; return 0; fi; done
                    STEAM_PROTON="$HOME/.steam/steam/steamapps/common"
                    if [ -d "$STEAM_PROTON" ]; then PROTON_DIR=$(ls -d "$STEAM_PROTON/Proton"* 2>/dev/null | sort -V | tail -1); if [ -n "$PROTON_DIR" ] && [ -x "$PROTON_DIR/dist/bin/wine" ]; then echo "$PROTON_DIR/dist/bin/wine"; return 0; fi; fi
                    return 1
                }
                
                WINE_CMD=$(find_wine)
                if [ -n "$WINE_CMD" ]; then
                    "$WINE_CMD" "{{linuxRelativeExePath}}" -game "{{linuxGamePath}}"{{args}}
                else
                    echo "ERROR: Wine/Proton not found."
                    exit 1
                fi
                """;
        }
        else
        {
           var exeDir = Path.GetDirectoryName(relativeExePath);
           var linuxCdPath = exeDir?.Replace("\\", "/") ?? ".";
           
           shellScriptContent = $$"""
                #!/bin/bash
                # SteamRoll Launcher for {{game.Name}}
                
                DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
                cd "$DIR/{{linuxCdPath}}"
                export LD_LIBRARY_PATH=".:$LD_LIBRARY_PATH"
                
                echo "Launching {{game.Name}}..."
                
                find_wine() {
                    if command -v wine &> /dev/null; then echo "wine"; return 0; fi
                    for wine_path in "$HOME/.local/bin/wine" "/usr/local/bin/wine"; do if [ -x "$wine_path" ]; then echo "$wine_path"; return 0; fi; done
                    STEAM_PROTON="$HOME/.steam/steam/steamapps/common"
                    if [ -d "$STEAM_PROTON" ]; then PROTON_DIR=$(ls -d "$STEAM_PROTON/Proton"* 2>/dev/null | sort -V | tail -1); if [ -n "$PROTON_DIR" ] && [ -x "$PROTON_DIR/dist/bin/wine" ]; then echo "$PROTON_DIR/dist/bin/wine"; return 0; fi; fi
                    return 1
                }
                
                if [[ "{{exeName}}" == *.exe ]]; then
                    WINE_CMD=$(find_wine)
                    if [ -n "$WINE_CMD" ]; then
                        "$WINE_CMD" "{{exeName}}"{{args}}
                    else
                        echo "ERROR: Wine/Proton not found."
                        exit 1
                    fi
                else
                     ./"{{exeName}}"{{args}}
                fi
                """;
        }

        shellScriptContent = shellScriptContent.Replace("\r\n", "\n");
        File.WriteAllText(Path.Combine(packageDir, "launch.sh"), shellScriptContent);
    }

    /// <summary>
    /// Detects if this is a Source engine game.
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

        var validGameInfo = gameInfoFiles
            .Select(path => new {
                Path = path,
                Dir = System.IO.Path.GetDirectoryName(path)!,
                Folder = new DirectoryInfo(Path.GetDirectoryName(path)!).Name
            })
            .Where(x => !x.Folder.Equals("platform", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.Folder.Equals("bin", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.Folder.Equals("hl2", StringComparison.OrdinalIgnoreCase)) 
            .OrderBy(x => x.Path.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
            .FirstOrDefault();

        if (validGameInfo == null)
            return null;

        var relativeToRoot = System.IO.Path.GetRelativePath(packageDir, validGameInfo.Dir);
        LogService.Instance.Info($"Source engine detected: '{validGameInfo.Folder}' at '{relativeToRoot}'", "LauncherGenerator");

        return (validGameInfo.Folder, relativeToRoot);
    }
}
