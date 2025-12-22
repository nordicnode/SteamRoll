using System.IO;

namespace SteamRoll.Services.Goldberg;

/// <summary>
/// Applies Goldberg Emulator files and configuration to game directories.
/// </summary>
public class GoldbergPatcher
{
    private readonly string _goldbergPath;

    public GoldbergPatcher(string goldbergPath)
    {
        _goldbergPath = goldbergPath;
    }

    /// <summary>
    /// Applies Goldberg Emulator to a game package directory.
    /// </summary>
    public bool ApplyGoldberg(string gameDir, int appId, GoldbergConfig? config = null)
    {
        if (!IsGoldbergAvailable())
        {
            LogService.Instance.Warning("Goldberg DLLs not available - skipping DLL replacement", "GoldbergPatcher");
            return false;
        }

        try
        {
            var replacedCount = 0;

            foreach (var originalDll in FindSteamApiDlls(gameDir))
            {
                var fileName = System.IO.Path.GetFileName(originalDll).ToLowerInvariant();
                var goldbergDll = fileName switch
                {
                    "steam_api.dll" => System.IO.Path.Combine(_goldbergPath, "steam_api.dll"),
                    "steam_api64.dll" => System.IO.Path.Combine(_goldbergPath, "steam_api64.dll"),
                    _ => null
                };

                if (goldbergDll != null && File.Exists(goldbergDll))
                {
                    // Backup original DLL
                    var backupPath = originalDll + ".original";
                    if (!File.Exists(backupPath))
                    {
                        File.Copy(originalDll, backupPath);
                    }

                    // Replace with Goldberg DLL
                    File.Copy(goldbergDll, originalDll, overwrite: true);
                    replacedCount++;
                }
            }

            // Create steam_appid.txt in game root
            File.WriteAllText(Path.Combine(gameDir, "steam_appid.txt"), appId.ToString());

            // Create basic steam_settings folder with user config
            CreateSteamSettings(gameDir, appId, config);

            return replacedCount > 0;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error applying Goldberg: {ex.Message}", ex, "GoldbergPatcher");
            return false;
        }
    }

    private bool IsGoldbergAvailable()
    {
        if (!Directory.Exists(_goldbergPath)) return false;
        return File.Exists(Path.Combine(_goldbergPath, "steam_api.dll")) &&
               File.Exists(Path.Combine(_goldbergPath, "steam_api64.dll"));
    }

    private IEnumerable<string> FindSteamApiDlls(string directory)
    {
        var dlls = new List<string>();

        try
        {
            foreach (var pattern in new[] { "steam_api.dll", "steam_api64.dll" })
            {
                dlls.AddRange(Directory.GetFiles(directory, pattern, SearchOption.AllDirectories));
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Error searching for DLLs: {ex.Message}", "GoldbergPatcher");
        }

        return dlls;
    }

    private void CreateSteamSettings(string gameDir, int appId, GoldbergConfig? config = null)
    {
        var settingsDir = System.IO.Path.Combine(gameDir, "steam_settings");
        Directory.CreateDirectory(settingsDir);

        // Use provided config or defaults
        var accountName = config?.AccountName ?? "Player";
        var disableNetworking = config?.DisableNetworking ?? true;
        var disableOverlay = config?.DisableOverlay ?? true;
        var enableLan = config?.EnableLan ?? false;

        // Main config - user settings
        File.WriteAllText(Path.Combine(settingsDir, "configs.user.ini"), $"""
            [user::general]
            account_name = {accountName}

            [user::saves]
            local_save_path = SteamRoll_Saves
            """);

        File.WriteAllText(
            System.IO.Path.Combine(settingsDir, "force_account_name.txt"),
            accountName
        );

        // --- Files to control Steam behavior ---

        // offline.txt - Run in offline mode to prevent Steam network calls
        if (disableNetworking)
        {
            File.WriteAllText(Path.Combine(settingsDir, "offline.txt"), "");
            File.WriteAllText(Path.Combine(settingsDir, "disable_networking.txt"), "");
        }

        // disable_overlay.txt - Disable Steam overlay (prevents hook conflicts)
        if (disableOverlay)
        {
            File.WriteAllText(Path.Combine(settingsDir, "disable_overlay.txt"), "");
        }

        // Main settings override
        File.WriteAllText(Path.Combine(settingsDir, "configs.main.ini"), $"""
            [main::connectivity]
            disable_networking={(disableNetworking ? 1 : 0)}
            disable_lan_only={(enableLan ? 0 : 1)}

            [main::general]
            disable_overlay={(disableOverlay ? 1 : 0)}
            """);
    }

    public void CreateInterfacesFile(string gameDir, List<string> interfaces)
    {
        if (interfaces.Count == 0)
            return;

        File.WriteAllLines(Path.Combine(gameDir, "steam_interfaces.txt"), interfaces);
    }
}
