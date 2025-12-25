using System.Windows.Input;
using SteamRoll.Services;

namespace SteamRoll.ViewModels;

/// <summary>
/// ViewModel for the settings window.
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    
    private string _outputPath = "";
    private bool _autoAnalyzeOnScan = true;
    private bool _showToastNotifications = true;
    private long _transferSpeedLimit;
    private bool _enableTransferCompression = true;
    private PackageMode _defaultPackageMode = PackageMode.Goldberg;
    private FileHashMode _defaultFileHashMode = FileHashMode.CriticalOnly;

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;

        // Initialize commands
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke(this, EventArgs.Empty));
        BrowseOutputPathCommand = new RelayCommand(BrowseOutputPath);
        ResetToDefaultsCommand = new RelayCommand(ResetToDefaults);

        // Load settings
        LoadSettings();
    }

    #region Properties

    public string OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
    }

    public bool AutoAnalyzeOnScan
    {
        get => _autoAnalyzeOnScan;
        set => SetProperty(ref _autoAnalyzeOnScan, value);
    }

    public bool ShowToastNotifications
    {
        get => _showToastNotifications;
        set => SetProperty(ref _showToastNotifications, value);
    }

    public long TransferSpeedLimit
    {
        get => _transferSpeedLimit;
        set => SetProperty(ref _transferSpeedLimit, value);
    }

    public bool EnableTransferCompression
    {
        get => _enableTransferCompression;
        set => SetProperty(ref _enableTransferCompression, value);
    }

    public PackageMode DefaultPackageMode
    {
        get => _defaultPackageMode;
        set => SetProperty(ref _defaultPackageMode, value);
    }

    public FileHashMode DefaultFileHashMode
    {
        get => _defaultFileHashMode;
        set => SetProperty(ref _defaultFileHashMode, value);
    }

    #endregion

    #region Commands

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseOutputPathCommand { get; }
    public ICommand ResetToDefaultsCommand { get; }

    #endregion

    #region Events

    /// <summary>
    /// Raised when save completes.
    /// </summary>
    public event EventHandler? SaveCompleted;

    /// <summary>
    /// Raised when cancel is requested.
    /// </summary>
    public event EventHandler? CancelRequested;

    /// <summary>
    /// Raised when output path browse is requested (View handles dialog).
    /// </summary>
    public event EventHandler<string>? BrowseOutputPathRequested;

    #endregion

    #region Methods

    private void LoadSettings()
    {
        var settings = _settingsService.Settings;
        OutputPath = settings.OutputPath;
        AutoAnalyzeOnScan = settings.AutoAnalyzeOnScan;
        ShowToastNotifications = settings.ShowToastNotifications;
        TransferSpeedLimit = settings.TransferSpeedLimit;
        EnableTransferCompression = settings.EnableTransferCompression;
        DefaultPackageMode = settings.DefaultPackageMode;
        DefaultFileHashMode = settings.DefaultFileHashMode;
    }

    private void Save()
    {
        var settings = _settingsService.Settings;
        settings.OutputPath = OutputPath;
        settings.AutoAnalyzeOnScan = AutoAnalyzeOnScan;
        settings.ShowToastNotifications = ShowToastNotifications;
        settings.TransferSpeedLimit = TransferSpeedLimit;
        settings.EnableTransferCompression = EnableTransferCompression;
        settings.DefaultPackageMode = DefaultPackageMode;
        settings.DefaultFileHashMode = DefaultFileHashMode;

        _settingsService.Save();
        SaveCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void BrowseOutputPath()
    {
        BrowseOutputPathRequested?.Invoke(this, OutputPath);
    }

    /// <summary>
    /// Called by View after folder dialog to set the new path.
    /// </summary>
    public void SetOutputPath(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            OutputPath = path;
        }
    }

    private void ResetToDefaults()
    {
        OutputPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 
            "SteamRoll Packages");
        AutoAnalyzeOnScan = true;
        ShowToastNotifications = true;
        TransferSpeedLimit = 0;
        EnableTransferCompression = true;
        DefaultPackageMode = PackageMode.Goldberg;
        DefaultFileHashMode = FileHashMode.CriticalOnly;
    }

    #endregion
}
