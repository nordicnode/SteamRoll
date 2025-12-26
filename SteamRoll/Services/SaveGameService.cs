using System.IO;
using System.IO.Compression;

namespace SteamRoll.Services;

/// <summary>
/// Service to manage backing up and restoring game saves for Goldberg Emulator games.
/// </summary>
public class SaveGameService
{
    private readonly SettingsService _settingsService;

    // Standard Goldberg save paths
    private const string SAVES_FOLDER_NAME = "SteamRoll_Saves"; // Configured in GoldbergService
    private const string APPDATA_FOLDER_NAME = "Goldberg SteamEmu Saves"; // Standard Goldberg default

    public SaveGameService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Locates the save directory for a specific AppID.
    /// Checks standard Goldberg locations. Creates the default directory if none exists.
    /// </summary>
    public string? FindSaveDirectory(int appId, string? gamePackagePath = null)
    {
        // 1. Check local AppData (standard Goldberg location)
        var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            APPDATA_FOLDER_NAME, appId.ToString());

        if (Directory.Exists(appDataPath))
            return appDataPath;

        // 2. Check local AppData (SteamRoll specific location if configured)
        // Since we configure local_save_path = SteamRoll_Saves in configs.user.ini,
        // Goldberg usually puts this relative to the game executable or in AppData/Roaming/SteamRoll_Saves depending on version/config.
        // Actually, if local_save_path is relative, it's relative to the game folder.

        if (!string.IsNullOrEmpty(gamePackagePath))
        {
            var relativeSavePath = Path.Combine(gamePackagePath, SAVES_FOLDER_NAME, appId.ToString());
             if (Directory.Exists(relativeSavePath))
                return relativeSavePath;

            // Check steam_settings/saves
             var settingsSavePath = Path.Combine(gamePackagePath, "steam_settings", "saves", appId.ToString());
             if (Directory.Exists(settingsSavePath))
                return settingsSavePath;
            
            // Create the relative save path in the game package for new games
            Directory.CreateDirectory(relativeSavePath);
            return relativeSavePath;
        }

        // 3. Create the default AppData location if no gamePackagePath provided
        Directory.CreateDirectory(appDataPath);
        return appDataPath;
    }

    /// <summary>
    /// Backs up saves for a given AppID to a zip file.
    /// </summary>
    public async Task<string> BackupSavesAsync(int appId, string destinationPath, string? gamePackagePath = null)
    {
        var saveDir = FindSaveDirectory(appId, gamePackagePath);
        if (string.IsNullOrEmpty(saveDir))
            throw new DirectoryNotFoundException($"Could not find save directory for AppID {appId}");

        await Task.Run(() =>
        {
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);

            ZipFile.CreateFromDirectory(saveDir, destinationPath);
        });

        return destinationPath;
    }

    /// <summary>
    /// Restores saves from a zip file for a given AppID.
    /// </summary>
    public async Task RestoreSavesAsync(string zipPath, int appId, string? gamePackagePath = null)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Backup file not found", zipPath);

        await RestoreSavesFromStreamAsync(File.OpenRead(zipPath), appId, gamePackagePath);
    }

    /// <summary>
    /// Restores saves from a zip stream for a given AppID.
    /// </summary>
    public async Task RestoreSavesFromStreamAsync(Stream zipStream, int appId, string? gamePackagePath = null)
    {
        // Determine restore location
        var saveDir = FindSaveDirectory(appId, gamePackagePath);

        if (string.IsNullOrEmpty(saveDir))
        {
            if (!string.IsNullOrEmpty(gamePackagePath))
            {
                // Create in package
                saveDir = Path.Combine(gamePackagePath, SAVES_FOLDER_NAME, appId.ToString());
            }
            else
            {
                // Create in AppData
                saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    APPDATA_FOLDER_NAME, appId.ToString());
            }
        }

        await Task.Run(() =>
        {
            Directory.CreateDirectory(saveDir);

            // Backup existing if not empty
            if (Directory.GetFiles(saveDir).Length > 0)
            {
                var backupPath = Path.Combine(Path.GetDirectoryName(saveDir)!, $"{appId}_backup_{DateTime.Now:yyyyMMddHHmmss}.zip");
                try { ZipFile.CreateFromDirectory(saveDir, backupPath); } catch {}
            }

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            archive.ExtractToDirectory(saveDir, true); // Overwrite
        });
    }

    /// <summary>
    /// Exports saves for a given AppID to a memory stream (zip format).
    /// </summary>
    public async Task<MemoryStream> ExportSavesToStreamAsync(int appId, string? gamePackagePath = null)
    {
        var saveDir = FindSaveDirectory(appId, gamePackagePath);
        if (string.IsNullOrEmpty(saveDir))
            throw new DirectoryNotFoundException($"Could not find save directory for AppID {appId}");

        var ms = new MemoryStream();
        await Task.Run(() =>
        {
            using var archive = new ZipArchive(ms, ZipArchiveMode.Create, true);
            var files = Directory.GetFiles(saveDir, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var relPath = Path.GetRelativePath(saveDir, file);
                archive.CreateEntryFromFile(file, relPath);
            }
        });

        ms.Position = 0;
        return ms;
    }
}
