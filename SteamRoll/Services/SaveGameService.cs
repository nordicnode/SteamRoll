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
    /// Checks standard Goldberg locations.
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
        }

        return null;
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

        // Determine restore location
        // If we can find an existing save dir, overwrite it.
        // If not, we need to decide where to put it.
        // Default to AppData/Goldberg SteamEmu Saves/{AppID} as it's the most standard global location if the game isn't packaged with local saves.

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
            ZipFile.ExtractToDirectory(zipPath, saveDir, true); // Overwrite
        });
    }
}
