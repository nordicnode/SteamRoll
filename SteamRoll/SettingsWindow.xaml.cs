using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        _ = LoadGoldbergVersionsAsync();
        UpdateDefenderStatus();
    }

    private async Task LoadGoldbergVersionsAsync()
    {
        try
        {
            DownloadGoldbergBtn.IsEnabled = false;

            // Get current version
            var currentVersion = new GoldbergService(_settingsService).GetInstalledVersion();
            CurrentGoldbergVersionText.Text = string.IsNullOrEmpty(currentVersion)
                ? "Current: Not Installed"
                : $"Current: {currentVersion}";

            // Load available versions
            var versions = await new GoldbergService(_settingsService).GetAvailableVersionsAsync();

            GoldbergVersionCombo.Items.Clear();
            if (versions.Count > 0)
            {
                foreach (var v in versions)
                {
                    GoldbergVersionCombo.Items.Add(v);
                }
                GoldbergVersionCombo.SelectedIndex = 0;
                DownloadGoldbergBtn.IsEnabled = true;
            }
            else
            {
                GoldbergVersionCombo.Items.Add("No versions found");
                GoldbergVersionCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error loading Goldberg versions: {ex.Message}", ex, "SettingsWindow");
        }
    }

    private void UpdateDefenderStatus()
    {
        // This can be slow, so run on background
        Task.Run(() =>
        {
            try
            {
                // Simple check if exclusions are present for all paths
                var needsExclusions = DefenderExclusionHelper.NeedsExclusions();

                Dispatcher.Invoke(() =>
                {
                    if (needsExclusions)
                    {
                        DefenderStatusText.Text = "Status: Not configured";
                        DefenderStatusText.Foreground = System.Windows.Media.Brushes.Orange;
                        AddExclusionBtn.IsEnabled = true;
                        AddExclusionBtn.Content = "Add Exclusions";
                    }
                    else
                    {
                        DefenderStatusText.Text = "Status: Active";
                        DefenderStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                        AddExclusionBtn.IsEnabled = false;
                        AddExclusionBtn.Content = "Active";
                    }
                });
            }
            catch (Exception ex)
            {
                LogService.Instance.Error("Error checking Defender status", ex, "Settings");
            }
        });
    }

    private async void DownloadGoldberg_Click(object sender, RoutedEventArgs e)
    {
        var selectedVersion = GoldbergVersionCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(selectedVersion) || selectedVersion == "No versions found")
            return;

        DownloadGoldbergBtn.IsEnabled = false;
        DownloadGoldbergBtn.Content = "⏳ Downloading...";

        try
        {
            var service = new GoldbergService(_settingsService);
            // Wire up progress
            service.DownloadProgressChanged += (status, percent) =>
            {
                Dispatcher.Invoke(() => DownloadGoldbergBtn.Content = $"{percent}%");
            };

            var success = await service.DownloadGoldbergAsync(selectedVersion);

            if (success)
            {
                ToastService.Instance.ShowSuccess("Goldberg Updated", $"Successfully installed version {selectedVersion}");
                CurrentGoldbergVersionText.Text = $"Current: {selectedVersion}";
            }
            else
            {
                MessageBox.Show("Failed to download Goldberg Emulator. Check logs for details.", "Download Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error downloading: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DownloadGoldbergBtn.Content = "⬇️ Download";
            DownloadGoldbergBtn.IsEnabled = true;
        }
    }
    
    /// <summary>
    /// Loads current settings into the UI.
    /// </summary>
    private void LoadSettings()
    {
        var settings = _settingsService.Settings;
        
        OutputPathBox.Text = settings.OutputPath;
        CustomGoldbergPathBox.Text = settings.CustomGoldbergPath;
        CustomCreamApiPathBox.Text = settings.CustomCreamApiPath;
        
        // Set package mode combo
        foreach (ComboBoxItem item in PackageModeCombo.Items)
        {
            if (item.Tag?.ToString() == settings.DefaultPackageMode.ToString())
            {
                PackageModeCombo.SelectedItem = item;
                break;
            }
        }
        
        AutoAnalyzeCheck.IsChecked = settings.AutoAnalyzeOnScan;
        ToastNotificationsCheck.IsChecked = settings.ShowToastNotifications;
        EnableCompressionCheck.IsChecked = settings.EnableTransferCompression;

        DiscoveryPortBox.Text = settings.LanDiscoveryPort.ToString();
        TransferPortBox.Text = settings.TransferPort.ToString();

        // Set bandwidth limit combo
        bool found = false;
        foreach (ComboBoxItem item in BandwidthLimitCombo.Items)
        {
            if (item.Tag?.ToString() == settings.TransferSpeedLimit.ToString())
            {
                BandwidthLimitCombo.SelectedItem = item;
                found = true;
                break;
            }
        }

        if (!found)
        {
            // If custom value not in list, select unlimited (or closest, but simplifed to Unlimited for now)
             BandwidthLimitCombo.SelectedIndex = 0;
        }
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
    
    private void BrowseGoldberg_Click(object sender, RoutedEventArgs e)
    {
         var dialog = new OpenFolderDialog
        {
            Title = "Select Local Goldberg Emulator Folder",
            InitialDirectory = string.IsNullOrEmpty(CustomGoldbergPathBox.Text) ? null : CustomGoldbergPathBox.Text
        };

        if (dialog.ShowDialog() == true)
        {
            CustomGoldbergPathBox.Text = dialog.FolderName;
        }
    }

    private void BrowseCreamApi_Click(object sender, RoutedEventArgs e)
    {
         var dialog = new OpenFolderDialog
        {
            Title = "Select Local CreamAPI Folder",
            InitialDirectory = string.IsNullOrEmpty(CustomCreamApiPathBox.Text) ? null : CustomCreamApiPathBox.Text
        };

        if (dialog.ShowDialog() == true)
        {
            CustomCreamApiPathBox.Text = dialog.FolderName;
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

    private void AddExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (DefenderExclusionHelper.IsRunningAsAdmin())
        {
            var paths = DefenderExclusionHelper.GetSteamRollExclusionPaths();
            if (DefenderExclusionHelper.AddExclusions(paths))
            {
                ToastService.Instance.ShowSuccess("Success", "Windows Defender exclusions added.");
                UpdateDefenderStatus();
            }
            else
            {
                MessageBox.Show("Failed to add exclusions. Check logs.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            var result = MessageBox.Show(
                "Adding exclusions requires Administrator privileges.\n\nRestart SteamRoll as Administrator?",
                "Administrator Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                if (DefenderExclusionHelper.RequestElevation())
                {
                    Application.Current.Shutdown();
                }
            }
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
        
        // Validate ports
        if (!int.TryParse(DiscoveryPortBox.Text, out var discoveryPort) || discoveryPort < 1 || discoveryPort > 65535)
        {
             MessageBox.Show("Invalid LAN Discovery Port. Must be 1-65535.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
             return;
        }

        if (!int.TryParse(TransferPortBox.Text, out var transferPort) || transferPort < 1 || transferPort > 65535)
        {
             MessageBox.Show("Invalid File Transfer Port. Must be 1-65535.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
             return;
        }

        // Save settings
        _settingsService.Update(settings =>
        {
            settings.OutputPath = OutputPathBox.Text;
            settings.CustomGoldbergPath = CustomGoldbergPathBox.Text;
            settings.CustomCreamApiPath = CustomCreamApiPathBox.Text;
            
            // Get selected package mode
            if (PackageModeCombo.SelectedItem is ComboBoxItem selectedItem)
            {
                var modeTag = selectedItem.Tag?.ToString();
                if (Enum.TryParse<PackageMode>(modeTag, out var mode))
                {
                    settings.DefaultPackageMode = mode;
                }
            }
            
            settings.AutoAnalyzeOnScan = AutoAnalyzeCheck.IsChecked ?? true;
            settings.ShowToastNotifications = ToastNotificationsCheck.IsChecked ?? true;
            settings.EnableTransferCompression = EnableCompressionCheck.IsChecked ?? true;

            settings.LanDiscoveryPort = discoveryPort;
            settings.TransferPort = transferPort;

            // Get selected bandwidth limit
            if (BandwidthLimitCombo.SelectedItem is ComboBoxItem limitItem)
            {
                if (long.TryParse(limitItem.Tag?.ToString(), out var limit))
                {
                    settings.TransferSpeedLimit = limit;
                }
            }
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

    private async void CleanupLibrary_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn != null) btn.IsEnabled = false;

        try
        {
            var cleanupService = new LibraryCleanupService(_settingsService.Settings.OutputPath);

            // This might take a moment if the library is huge, so we run async
            var orphans = await cleanupService.ScanForOrphansAsync();

            if (orphans.Count == 0)
            {
                MessageBox.Show("No orphaned files found. Your library is clean!", "Library Cleanup", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var totalSize = orphans.Sum(o => o.Size);
            var fileCount = orphans.Count(o => !o.IsDirectory);
            var dirCount = orphans.Count(o => o.IsDirectory);

            var message = $"Found {fileCount} orphaned files and {dirCount} folders.\n" +
                          $"Total size: {FormatUtils.FormatBytes(totalSize)}\n\n" +
                          "These items are not part of any valid SteamRoll package in your library.\n" +
                          "Do you want to delete them permanently?";

            var result = MessageBox.Show(message, "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                await cleanupService.CleanupAsync(orphans);
                ToastService.Instance.ShowSuccess("Cleanup Complete", $"Recovered {FormatUtils.FormatBytes(totalSize)} of disk space.");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cleanup failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
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
    
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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

    private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
    {
        Regex regex = new Regex("[^0-9]+");
        e.Handled = regex.IsMatch(e.Text);
    }
}
