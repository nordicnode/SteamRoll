using System.Windows;
using Microsoft.Win32;
using SteamRoll.Services;

namespace SteamRoll;

/// <summary>
/// Settings dialog for configuring SteamRoll application options.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly CacheService? _cacheService;
    
    /// <summary>
    /// Gets whether the user saved changes.
    /// </summary>
    public bool ChangesSaved { get; private set; }
    
    /// <summary>
    /// Creates a new settings window.
    /// </summary>
    /// <param name="settingsService">The settings service to use.</param>
    /// <param name="cacheService">Optional cache service for cache management.</param>
    public SettingsWindow(SettingsService settingsService, CacheService? cacheService = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _cacheService = cacheService;
        
        LoadSettings();
        UpdateCacheStats();
    }
    
    /// <summary>
    /// Loads current settings into the UI.
    /// </summary>
    private void LoadSettings()
    {
        var settings = _settingsService.Settings;
        
        OutputPathBox.Text = settings.OutputPath;
        
        // Set package mode combo
        foreach (System.Windows.Controls.ComboBoxItem item in PackageModeCombo.Items)
        {
            if (item.Tag?.ToString() == settings.DefaultPackageMode.ToString())
            {
                PackageModeCombo.SelectedItem = item;
                break;
            }
        }
        
        AutoAnalyzeCheck.IsChecked = settings.AutoAnalyzeOnScan;
        ToastNotificationsCheck.IsChecked = settings.ShowToastNotifications;
    }
    
    /// <summary>
    /// Updates the cache statistics display.
    /// </summary>
    private void UpdateCacheStats()
    {
        if (_cacheService != null)
        {
            var (count, size) = _cacheService.GetStats();
            CacheStatsText.Text = $"{count} games cached";
        }
        else
        {
            CacheStatsText.Text = "Cache stats unavailable";
        }
    }
    
    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Output Folder",
            InitialDirectory = OutputPathBox.Text
        };
        
        if (dialog.ShowDialog() == true)
        {
            OutputPathBox.Text = dialog.FolderName;
        }
    }
    
    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (_cacheService == null)
        {
            MessageBox.Show("Cache service is not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        var result = MessageBox.Show(
            "Are you sure you want to clear the game cache?\n\nThis will require re-scanning your library on next refresh.",
            "Clear Cache",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        
        if (result == MessageBoxResult.Yes)
        {
            _cacheService.ClearCache();
            UpdateCacheStats();
            ToastService.Instance.ShowSuccess("Cache Cleared", "Game cache has been cleared.");
        }
    }
    
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Validate output path
        if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
        {
            MessageBox.Show("Please select a valid output folder.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        // Save settings
        _settingsService.Update(settings =>
        {
            settings.OutputPath = OutputPathBox.Text;
            
            // Get selected package mode
            if (PackageModeCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
            {
                var modeTag = selectedItem.Tag?.ToString();
                if (Enum.TryParse<PackageMode>(modeTag, out var mode))
                {
                    settings.DefaultPackageMode = mode;
                }
            }
            
            settings.AutoAnalyzeOnScan = AutoAnalyzeCheck.IsChecked ?? true;
            settings.ShowToastNotifications = ToastNotificationsCheck.IsChecked ?? true;
        });
        
        ChangesSaved = true;
        DialogResult = true;
        Close();
    }
    
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
    
    private void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        var logViewer = new LogViewerWindow
        {
            Owner = this
        };
        logViewer.ShowDialog();
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Settings",
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            FileName = "steamroll_settings.json",
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                _settingsService.Export(dialog.FileName);
                ToastService.Instance.ShowSuccess("Settings Exported", $"Configuration saved to {System.IO.Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export settings: {ex.Message}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Settings",
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                _settingsService.Import(dialog.FileName);
                LoadSettings(); // Refresh UI
                ToastService.Instance.ShowSuccess("Settings Imported", "Configuration loaded successfully.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to import settings: {ex.Message}", "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
    
    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

