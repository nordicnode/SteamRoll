using System.IO;
using System.Text.Json;

namespace SteamRoll.Services;

/// <summary>
/// Manages application settings persistence using JSON storage.
/// </summary>
public class SettingsService
{
    private static readonly string SettingsPath = GetSettingsPath();
    
    /// <summary>
    /// Gets the path to the settings file, supporting portable mode.
    /// If settings.json exists next to the executable, uses that (portable mode).
    /// Otherwise uses LocalAppData (installed mode).
    /// </summary>
    private static string GetSettingsPath()
    {
        // Check for portable mode (settings.json next to executable)
        var portablePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        if (File.Exists(portablePath))
        {
            return portablePath;
        }
        
        // Default to LocalAppData
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamRoll",
            "settings.json");
    }
    
    private AppSettings _settings = new();
    
    /// <summary>
    /// Gets the current application settings.
    /// </summary>
    public AppSettings Settings => _settings;
    
    /// <summary>
    /// Loads settings from disk, or creates default settings if none exist.
    /// </summary>
    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                LogService.Instance.Info($"Settings loaded from {SettingsPath}", "Settings");
                
                // Validate loaded settings
                ValidateSettings();
            }
            else
            {
                _settings = new AppSettings();
                Save(); // Create default settings file
                LogService.Instance.Info("Created default settings file", "Settings");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error loading settings", ex, "Settings");

            // Backup corrupt settings file if it exists
            if (File.Exists(SettingsPath))
            {
                try
                {
                    var backupPath = SettingsPath + ".bak";
                    File.Copy(SettingsPath, backupPath, true);
                    LogService.Instance.Warning($"Backed up corrupt settings to {backupPath}", "Settings");
                }
                catch (Exception backupEx)
                {
                    LogService.Instance.Error($"Failed to backup corrupt settings", backupEx, "Settings");
                }
            }

            _settings = new AppSettings();
        }
    }
    
    /// <summary>
    /// Validates loaded settings and resets invalid values to defaults.
    /// </summary>
    private void ValidateSettings()
    {
        var defaults = new AppSettings();
        var needsSave = false;
        
        // Validate port numbers (1-65535)
        if (_settings.LanDiscoveryPort < 1 || _settings.LanDiscoveryPort > 65535)
        {
            LogService.Instance.Warning($"Invalid LanDiscoveryPort {_settings.LanDiscoveryPort}, resetting to default", "Settings");
            _settings.LanDiscoveryPort = defaults.LanDiscoveryPort;
            needsSave = true;
        }
        
        if (_settings.TransferPort < 1 || _settings.TransferPort > 65535)
        {
            LogService.Instance.Warning($"Invalid TransferPort {_settings.TransferPort}, resetting to default", "Settings");
            _settings.TransferPort = defaults.TransferPort;
            needsSave = true;
        }
        
        // Validate window dimensions
        if (_settings.WindowWidth < 400 || _settings.WindowWidth > 10000)
        {
            _settings.WindowWidth = defaults.WindowWidth;
            needsSave = true;
        }
        
        if (_settings.WindowHeight < 300 || _settings.WindowHeight > 10000)
        {
            _settings.WindowHeight = defaults.WindowHeight;
            needsSave = true;
        }
        
        // Validate transfer speed limit (0 = unlimited, or positive value)
        if (_settings.TransferSpeedLimit < 0)
        {
            _settings.TransferSpeedLimit = 0;
            needsSave = true;
        }
        
        if (needsSave)
        {
            Save();
            LogService.Instance.Info("Settings were corrected and saved", "Settings");
        }
    }
    
    /// <summary>
    /// Saves current settings to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var json = JsonSerializer.Serialize(_settings, options);
            File.WriteAllText(SettingsPath, json);
            
            LogService.Instance.Info($"Settings saved to {SettingsPath}", "Settings");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error saving settings", ex, "Settings");
        }
    }
    
    /// <summary>
    /// Updates a setting and saves immediately.
    /// </summary>
    public void Update(Action<AppSettings> updateAction)
    {
        updateAction(_settings);
        Save();
    }

    /// <summary>
    /// Exports current settings to the specified file System.IO.Path.
    /// </summary>
    public void Export(string filePath)
    {
        try
        {
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var json = JsonSerializer.Serialize(_settings, options);
            File.WriteAllText(filePath, json);
            LogService.Instance.Info($"Settings exported to {filePath}", "Settings");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error exporting settings to {filePath}", ex, "Settings");
            throw; // Re-throw to let UI handle the error
        }
    }

    /// <summary>
    /// Imports settings from the specified file System.IO.Path.
    /// </summary>
    public void Import(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var importedSettings = JsonSerializer.Deserialize<AppSettings>(json);
            
            if (importedSettings != null)
            {
                _settings = importedSettings;
                Save(); // Persist imported settings
                LogService.Instance.Info($"Settings imported from {filePath}", "Settings");
            }
            else
            {
                throw new Exception("Invalid settings file format.");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error importing settings from {filePath}", ex, "Settings");
            throw; // Re-throw to let UI handle the error
        }
    }
}

/// <summary>
/// Application settings that are persisted to disk.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Unique device identifier for vector clock synchronization.
    /// Auto-generated on first run, persisted for consistent identification.
    /// </summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    
    /// <summary>
    /// Output directory for packaged games.
    /// </summary>
    public string OutputPath { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "SteamRoll Packages"
    );
    
    /// <summary>
    /// Default emulator mode for packaging.
    /// </summary>
    public PackageMode DefaultPackageMode { get; set; } = PackageMode.Goldberg;
    
    /// <summary>
    /// Default file hash mode for package integrity verification.
    /// CriticalOnly provides best performance while still verifying Steam DLLs.
    /// </summary>
    public FileHashMode DefaultFileHashMode { get; set; } = FileHashMode.CriticalOnly;
    
    /// <summary>
    /// Whether to auto-analyze games on scan.
    /// </summary>
    public bool AutoAnalyzeOnScan { get; set; } = true;
    
    /// <summary>
    /// Whether to show toast notifications.
    /// </summary>
    public bool ShowToastNotifications { get; set; } = true;
    
    /// <summary>
    /// LAN discovery port.
    /// </summary>
    public int LanDiscoveryPort { get; set; } = AppConstants.DEFAULT_DISCOVERY_PORT;
    
    /// <summary>
    /// File transfer port.
    /// </summary>
    public int TransferPort { get; set; } = AppConstants.DEFAULT_TRANSFER_PORT;
    
    /// <summary>
    /// Last window width.
    /// </summary>
    public double WindowWidth { get; set; } = 1200;
    
    /// <summary>
    /// Last window height.
    /// </summary>
    public double WindowHeight { get; set; } = 800;

    /// <summary>
    /// Last window top position.
    /// </summary>
    public double WindowTop { get; set; } = double.NaN;

    /// <summary>
    /// Last window left position.
    /// </summary>
    public double WindowLeft { get; set; } = double.NaN;

    /// <summary>
    /// Last window state (Maximized, Normal, etc).
    /// </summary>
    public string WindowState { get; set; } = "Maximized";
    
    /// <summary>
    /// Whether Defender exclusions have been added.
    /// </summary>
    public bool DefenderExclusionsAdded { get; set; } = false;
    
    /// <summary>
    /// Fallback URL for CreamAPI if GitHub API fails.
    /// </summary>
    public string CreamApiFallbackUrl { get; set; } = AppConstants.DEFAULT_CREAMAPI_FALLBACK_URL;

    /// <summary>
    /// Custom URL for Goldberg Emulator GitLab releases API.
    /// </summary>
    public string GoldbergGitLabUrl { get; set; } = AppConstants.DEFAULT_GOLDBERG_GITLAB_URL;

    /// <summary>
    /// Custom URL for Goldberg Emulator GitHub fork releases API.
    /// </summary>
    public string GoldbergGitHubUrl { get; set; } = AppConstants.DEFAULT_GOLDBERG_GITHUB_URL;

    /// <summary>
    /// Custom path for local Goldberg Emulator files (overrides download).
    /// </summary>
    public string CustomGoldbergPath { get; set; } = "";

    /// <summary>
    /// Custom path for local CreamAPI files (overrides download).
    /// </summary>
    public string CustomCreamApiPath { get; set; } = "";

    /// <summary>
    /// Transfer speed limit in bytes per second (0 = unlimited).
    /// </summary>
    public long TransferSpeedLimit { get; set; } = 0;

    /// <summary>
    /// Whether to compress file transfers using GZip.
    /// </summary>
    public bool EnableTransferCompression { get; set; } = true;

    /// <summary>
    /// Whether to enable automatic background save synchronization.
    /// When enabled, saves are automatically synced between peers.
    /// When disabled, use the "Sync Now" button to manually sync saves.
    /// </summary>
    public bool AutoSaveSync { get; set; } = false;

    /// <summary>
    /// Whether to show network availability badges on games available from LAN peers.
    /// </summary>
    public bool ShowNetworkBadges { get; set; } = true;

    /// <summary>
    /// When true, network services bind only to local LAN interface instead of all interfaces.
    /// Improves security on public networks (e.g., cafes, airports).
    /// </summary>
    public bool BindToLocalIpOnly { get; set; } = false;

    /// <summary>
    /// Whether to require encryption for file transfers.
    /// When enabled, only paired devices can send/receive files.
    /// </summary>
    public bool RequireTransferEncryption { get; set; } = false;

    /// <summary>
    /// Friendly device name shown to other peers during pairing.
    /// </summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Whether to remember and auto-restore direct connect peers on startup.
    /// When enabled, manually added peers persist across app restarts.
    /// </summary>
    public bool RememberDirectConnectPeers { get; set; } = true;

    /// <summary>
    /// List of manually configured peer addresses for direct connection.
    /// Used for VPN/VLAN scenarios where UDP broadcast doesn't work.
    /// Only used if RememberDirectConnectPeers is enabled.
    /// </summary>
    public List<DirectConnectPeer> DirectConnectPeers { get; set; } = new();

    /// <summary>
    /// Per-game Goldberg Emulator configurations.
    /// Persisted to retain user customizations across sessions.
    /// </summary>
    public Dictionary<int, GoldbergConfig> GameGoldbergConfigs { get; set; } = new();
}

/// <summary>
/// A manually configured peer for direct connection.
/// </summary>
public class DirectConnectPeer
{
    /// <summary>
    /// IP address of the peer.
    /// </summary>
    public string IpAddress { get; set; } = "";

    /// <summary>
    /// Transfer port of the peer.
    /// </summary>
    public int Port { get; set; } = AppConstants.DEFAULT_TRANSFER_PORT;

    /// <summary>
    /// Optional friendly name for this peer.
    /// </summary>
    public string? DisplayName { get; set; }
}
