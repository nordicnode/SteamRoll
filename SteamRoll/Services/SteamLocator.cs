using System.IO;
using Microsoft.Win32;

namespace SteamRoll.Services;

/// <summary>
/// Locates Steam installation and library folders on the system.
/// </summary>
public class SteamLocator
{
    private const string STEAM_REGISTRY_KEY = @"SOFTWARE\WOW6432Node\Valve\Steam";
    private const string STEAM_REGISTRY_KEY_32 = @"SOFTWARE\Valve\Steam";

    /// <summary>
    /// Gets the Steam installation path from the Windows registry.
    /// </summary>
    /// <returns>Steam installation path, or null if not found.</returns>
    public string? GetSteamInstallPath()
    {
        // Try 64-bit registry first
        using var key64 = Registry.LocalMachine.OpenSubKey(STEAM_REGISTRY_KEY);
        if (key64?.GetValue("InstallPath") is string path64 && Directory.Exists(path64))
        {
            return path64;
        }

        // Try 32-bit registry
        using var key32 = Registry.LocalMachine.OpenSubKey(STEAM_REGISTRY_KEY_32);
        if (key32?.GetValue("InstallPath") is string path32 && Directory.Exists(path32))
        {
            return path32;
        }

        // Try current user
        using var keyUser = Registry.CurrentUser.OpenSubKey(STEAM_REGISTRY_KEY_32);
        if (keyUser?.GetValue("SteamPath") is string pathUser && Directory.Exists(pathUser))
        {
            return pathUser;
        }

        // Fallback to common locations
        var defaultPaths = new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
        };

        return defaultPaths.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// Gets all Steam library folders (including the main Steam folder).
    /// </summary>
    /// <returns>List of library folder paths.</returns>
    public List<string> GetLibraryFolders()
    {
        var libraries = new List<string>();
        var steamPath = GetSteamInstallPath();

        if (steamPath == null)
            return libraries;

        // Main Steam library is always at steamPath/steamapps
        var mainLibrary = System.IO.Path.Combine(steamPath, "steamapps");
        if (Directory.Exists(mainLibrary))
        {
            libraries.Add(steamPath);
        }

        // Parse libraryfolders.vdf for additional libraries
        var libraryVdfPaths = new[]
        {
            System.IO.Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"),
            System.IO.Path.Combine(steamPath, "config", "libraryfolders.vdf"),
        };

        foreach (var vdfPath in libraryVdfPaths)
        {
            if (!File.Exists(vdfPath))
                continue;

            try
            {
                var vdf = Parsers.VdfParser.ParseFile(vdfPath);
                
                // The root key is "libraryfolders" containing numbered entries
                if (vdf.TryGetValue("libraryfolders", out var libraryFoldersObj) &&
                    libraryFoldersObj is Dictionary<string, object> libraryFolders)
                {
                    foreach (var entry in libraryFolders)
                    {
                        string? libraryPath = null;

                        if (entry.Value is Dictionary<string, object> folderInfo)
                        {
                            // New format: nested object with "path" key
                            if (folderInfo.TryGetValue("path", out var pathObj))
                            {
                                libraryPath = pathObj.ToString();
                            }
                        }
                        else if (entry.Value is string pathStr)
                        {
                            // Old format: direct path string
                            libraryPath = pathStr;
                        }

                        if (!string.IsNullOrEmpty(libraryPath) && 
                            Directory.Exists(libraryPath) &&
                            !libraries.Contains(libraryPath, StringComparer.OrdinalIgnoreCase))
                        {
                            libraries.Add(libraryPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Error parsing {vdfPath}: {ex.Message}", ex, "SteamLocator");
            }

            break; // Only need to parse one successfully
        }

        return libraries;
    }

    /// <summary>
    /// Checks if Steam is currently running.
    /// </summary>
    public bool IsSteamRunning()
    {
        return System.Diagnostics.Process.GetProcessesByName("steam").Length > 0;
    }
}
