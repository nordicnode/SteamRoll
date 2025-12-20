using System.IO;
using System.Text.Json;

namespace SteamRoll.Services;

/// <summary>
/// Manages application settings persistence using JSON storage.
/// </summary>
public class SettingsService
{
    private static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamRoll",
        "settings.json"
    );
    
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
            _settings = new AppSettings();
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
    public string WindowState { get; set; } = "Normal";
    
    /// <summary>
    /// Whether Defender exclusions have been added.
    /// </summary>
    /// <summary>
    /// Whether Defender exclusions have been added.
    /// </summary>
    public bool DefenderExclusionsAdded { get; set; } = false;
    
    /// <summary>
    /// Fallback URL for CreamAPI if GitHub API fails.
    /// </summary>
    public string CreamApiFallbackUrl { get; set; } = "https://github.com/deadmau5v/CreamAPI/releases/download/2024.12.08/CreamAPI.zip";
}
