using System.IO;

namespace SteamRoll.Services.Goldberg;

/// <summary>
/// Applies Goldberg Emulator files and configuration to game directories.
/// </summary>
public class GoldbergPatcher
{
    private readonly string _goldbergPath;
    private readonly GoldbergScanner _scanner = new();

    public GoldbergPatcher(string goldbergPath)
    {
        _goldbergPath = goldbergPath;
    }

    /// <summary>
    /// Applies Goldberg Emulator to a game package directory.
    /// Scans original DLLs for Steam interfaces before replacement to ensure stability.
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
            
            // Track all detected interfaces across all Steam DLLs
            var allInterfaces = new HashSet<string>();

            foreach (var originalDll in FindSteamApiDlls(gameDir))
            {
                var fileName = System.IO.Path.GetFileName(originalDll).ToLowerInvariant();
                
                // CRITICAL: Scan the ORIGINAL DLL for interfaces BEFORE replacement
                // This is essential for stability on older games (Source Engine, Unreal 3)
                // where Goldberg's auto-detection fails and causes silent crashes
                try
                {
                    var interfaces = _scanner.DetectInterfaces(originalDll);
                    foreach (var iface in interfaces)
                    {
                        allInterfaces.Add(iface);
                    }
                    
                    if (interfaces.Count > 0)
                    {
                        LogService.Instance.Debug($"Detected {interfaces.Count} interfaces in {fileName}", "GoldbergPatcher");
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning($"Failed to scan {fileName} for interfaces: {ex.Message}", "GoldbergPatcher");
                }

                // Now perform the backup and replacement
                var goldbergDll = fileName switch
                {
                    "steam_api.dll" => System.IO.Path.Combine(_goldbergPath, "steam_api.dll"),
                    "steam_api64.dll" => System.IO.Path.Combine(_goldbergPath, "steam_api64.dll"),
                    _ => null
                };

                if (goldbergDll != null && File.Exists(goldbergDll))
                {
                    // Backup original DLL - but warn if it looks already modified
                    var backupPath = originalDll + ".original";
                    if (!File.Exists(backupPath))
                    {
                        // Heuristic: Goldberg DLLs are larger than Steam originals
                        // Steam's steam_api.dll is typically ~250KB, Goldberg is ~1.5MB+
                        // If the "original" is already large, it might be already patched
                        var originalFileInfo = new FileInfo(originalDll);
                        var goldbergFileInfo = new FileInfo(goldbergDll);
                        
                        // If original is within 10% of Goldberg size, it's likely already patched
                        if (originalFileInfo.Length > goldbergFileInfo.Length * 0.9)
                        {
                            LogService.Instance.Warning(
                                $"{fileName} appears already modified (size: {originalFileInfo.Length / 1024}KB vs Goldberg: {goldbergFileInfo.Length / 1024}KB). " +
                                "Backup may not contain original Steam DLL.", "GoldbergPatcher");
                        }
                        
                        File.Copy(originalDll, backupPath);
                    }

                    // Replace with Goldberg DLL
                    File.Copy(goldbergDll, originalDll, overwrite: true);
                    replacedCount++;
                }
            }

            if (replacedCount > 0)
            {
                // Create settings directory FIRST (needed for interfaces file)
                var settingsDir = Path.Combine(gameDir, "steam_settings");
                Directory.CreateDirectory(settingsDir);

                // Write steam_interfaces.txt inside steam_settings folder (canonical location)
                if (allInterfaces.Count > 0)
                {
                    var interfacePath = Path.Combine(settingsDir, "steam_interfaces.txt");
                    File.WriteAllLines(interfacePath, allInterfaces.OrderBy(i => i));
                    LogService.Instance.Info($"Generated steam_interfaces.txt with {allInterfaces.Count} interfaces", "GoldbergPatcher");
                }

                // Create steam_appid.txt in game root
                File.WriteAllText(Path.Combine(gameDir, "steam_appid.txt"), appId.ToString());

                // Create user config in steam_settings folder
                CreateSteamSettings(gameDir, appId, config);
            }

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
