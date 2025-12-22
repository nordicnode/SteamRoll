using System.IO;
using System.Net.Http;
using SteamRoll.Services.Goldberg;

namespace SteamRoll.Services;

/// <summary>
/// Manages Goldberg Steam Emulator DLLs for game packaging.
/// Handles downloading, extracting, and applying Goldberg to game packages.
/// Uses the actively maintained gbe_fork for newer Steam SDK support.
/// </summary>
public class GoldbergService : IDisposable
{
    private readonly string _goldbergPath;
    private readonly HttpClient _httpClient;
    private readonly SettingsService? _settingsService;
    private bool _disposed;
    
    private readonly GoldbergScanner _scanner;
    private readonly GoldbergInstaller _installer;
    private readonly GoldbergPatcher _patcher;

    /// <summary>
    /// Event for download progress updates.
    /// </summary>
    public event Action<string, int>? DownloadProgressChanged;
    
    /// <summary>
    /// Gets the path to the Goldberg installation directory.
    /// </summary>
    public string GoldbergPath => GetEffectiveGoldbergPath();


    public GoldbergService(SettingsService? settingsService = null)
    {
        _settingsService = settingsService;

        // Default Goldberg path
        _goldbergPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamRoll",
            "Goldberg"
        );
        
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SteamRoll/1.0");
        _httpClient.Timeout = TimeSpan.FromMinutes(5);

        _scanner = new GoldbergScanner();
        _installer = new GoldbergInstaller(_httpClient, settingsService, GetEffectiveGoldbergPath());
        _patcher = new GoldbergPatcher(GetEffectiveGoldbergPath());

        _installer.ProgressChanged += (status, percentage) => DownloadProgressChanged?.Invoke(status, percentage);
    }

    private string GetEffectiveGoldbergPath()
    {
        if (_settingsService != null && !string.IsNullOrEmpty(_settingsService.Settings.CustomGoldbergPath))
        {
            return _settingsService.Settings.CustomGoldbergPath;
        }
        return _goldbergPath;
    }

    /// <summary>
    /// Checks if Goldberg DLLs are available locally.
    /// </summary>
    public bool IsGoldbergAvailable()
    {
        var path = GetEffectiveGoldbergPath();
        if (!Directory.Exists(path))
            return false;

        // Check for both 32-bit and 64-bit DLLs
        return File.Exists(Path.Combine(path, "steam_api.dll")) &&
               File.Exists(Path.Combine(path, "steam_api64.dll"));
    }

    /// <summary>
    /// Gets the path where Goldberg files are stored.
    /// </summary>
    public string GetGoldbergPath() => GetEffectiveGoldbergPath();

    /// <summary>
    /// Initializes the Goldberg directory.
    /// </summary>
    public void InitializeGoldbergDirectory()
    {
        // Only create default directory if custom path is not used
        if (GetEffectiveGoldbergPath() == _goldbergPath)
        {
            Directory.CreateDirectory(_goldbergPath);
        }
    }

    /// <summary>
    /// Gets the installed version of Goldberg Emulator.
    /// </summary>
    public string? GetInstalledVersion() => _installer.GetInstalledVersion();

    /// <summary>
    /// Gets available versions from GitHub releases.
    /// </summary>
    public Task<List<string>> GetAvailableVersionsAsync() => _installer.GetAvailableVersionsAsync();
    
    /// <summary>
    /// Downloads and extracts Goldberg Emulator automatically.
    /// </summary>
    /// <returns>True if successful, false otherwise.</returns>
    public Task<bool> DownloadGoldbergAsync(string? specificVersion = null) => _installer.DownloadGoldbergAsync(specificVersion);

    /// <summary>
    /// Ensures Goldberg is available, downloading if necessary.
    /// </summary>
    public async Task<bool> EnsureGoldbergAvailableAsync()
    {
        if (IsGoldbergAvailable())
            return true;

        return await DownloadGoldbergAsync();
    }

    /// <summary>
    /// Applies Goldberg Emulator to a game package directory.
    /// </summary>
    public bool ApplyGoldberg(string gameDir, int appId, GoldbergConfig? config = null) => _patcher.ApplyGoldberg(gameDir, appId, config);

    public List<string> DetectInterfaces(string steamApiPath) => _scanner.DetectInterfaces(steamApiPath);

    public void CreateInterfacesFile(string gameDir, List<string> interfaces) => _patcher.CreateInterfacesFile(gameDir, interfaces);

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
