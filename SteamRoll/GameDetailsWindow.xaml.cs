using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamRoll.Models;
using SteamRoll.Services;

namespace SteamRoll;

/// <summary>
/// Detailed game information window showing DRM, DLC, and package status.
/// </summary>
public partial class GameDetailsWindow : Window
{
    private readonly InstalledGame _game;
    private readonly Action<InstalledGame, PackageMode>? _packageCallback;
    private readonly SteamStoreService _storeService;

    public GameDetailsWindow(InstalledGame game, Action<InstalledGame>? packageCallback = null)
    {
        InitializeComponent();
        _game = game;
        // Use shared singleton instance to avoid multiple HttpClient instances
        _storeService = SteamStoreService.Instance;
        _packageCallback = packageCallback != null 
            ? (g, m) => packageCallback(g) 
            : null;
        
        LoadGameDetails();
        EmulatorModeCombo.SelectionChanged += EmulatorModeCombo_SelectionChanged;
    }

    
    public GameDetailsWindow(InstalledGame game, Action<InstalledGame, PackageMode>? packageCallback)
    {
        InitializeComponent();
        _game = game;
        // Use shared singleton instance to avoid multiple HttpClient instances
        _storeService = SteamStoreService.Instance;
        _packageCallback = packageCallback;
        
        LoadGameDetails();
        EmulatorModeCombo.SelectionChanged += EmulatorModeCombo_SelectionChanged;
    }


    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadSteamStoreDataAsync();
    }

    private async Task LoadSteamStoreDataAsync()
    {
        try
        {
            LoadingText.Visibility = Visibility.Visible;
            
            var details = await _storeService.GetGameDetailsAsync(_game.AppId);
            
            if (details != null)
            {
                // Update description
                DescriptionText.Text = !string.IsNullOrEmpty(details.Description) 
                    ? details.Description 
                    : "No description available.";
                
                // Update genres
                GenresText.Text = !string.IsNullOrEmpty(details.GenresDisplay) 
                    ? details.GenresDisplay 
                    : "";
                
                // Update developer
                DeveloperText.Text = details.Developers.Count > 0 
                    ? $"Developer: {details.DevelopersDisplay}" 
                    : "";
                
                // Update release date
                ReleaseDateText.Text = !string.IsNullOrEmpty(details.ReleaseDate) 
                    ? $"Released: {details.ReleaseDate}" 
                    : "";
                
                // Update metacritic
                if (details.MetacriticScore.HasValue)
                {
                    MetacriticBadge.Visibility = Visibility.Visible;
                    MetacriticText.Text = details.MetacriticScore.Value.ToString();
                    
                    // Color based on score
                    MetacriticBadge.Background = details.MetacriticScore.Value switch
                    {
                        >= 75 => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)), // Green
                        >= 50 => new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22)), // Yellow
                        _ => new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49)) // Red
                    };
                }

                // Update Steam Reviews
                if (details.ReviewPositivePercent.HasValue)
                {
                    ReviewBadge.Visibility = Visibility.Visible;
                    ReviewText.Text = details.ReviewDisplay;

                    // Color based on percent
                    var percent = details.ReviewPositivePercent.Value;
                    if (percent >= 70) // Mostly Positive or better
                        ReviewText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xC0, 0xF4)); // Steam Blue
                    else if (percent >= 40) // Mixed
                        ReviewText.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0xA0, 0x74)); // Steam Mixed/Orange
                    else // Negative
                        ReviewText.Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0x4C, 0x2E)); // Steam Red
                }
                
                // Load background image
                if (!string.IsNullOrEmpty(details.BackgroundImage))
                {
                    try
                    {
                        var bitmap = new BitmapImage(new Uri(details.BackgroundImage));
                        BackgroundImage.Source = bitmap;
                    }
                    catch { }
                }
                
                // Load header image
                if (!string.IsNullOrEmpty(details.HeaderImage))
                {
                    try
                    {
                        var bitmap = new BitmapImage(new Uri(details.HeaderImage));
                        HeaderImageBrush.ImageSource = bitmap;
                    }
                    catch { }
                }
                
                // Load screenshots
                if (details.Screenshots.Count > 0)
                {
                    ScreenshotsList.ItemsSource = details.Screenshots.Take(6);
                }
                else
                {
                    NoScreenshotsText.Visibility = Visibility.Visible;
                }
                
                // Load features
                if (details.Features.Count > 0)
                {
                    FeaturesList.ItemsSource = details.Features.Take(12);
                }
            }
            else
            {
                DescriptionText.Text = "Unable to load game details from Steam.";
                NoScreenshotsText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            DescriptionText.Text = $"Error loading Steam data: {ex.Message}";
        }
        finally
        {
            LoadingText.Visibility = Visibility.Collapsed;
        }
    }
    
    private void EmulatorModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Mode selection changed - no action needed yet
    }
    
    private PackageMode GetSelectedMode()
    {
        var selectedItem = EmulatorModeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem;
        var tag = selectedItem?.Tag?.ToString();
        return tag == "CreamApi" ? PackageMode.CreamApi : PackageMode.Goldberg;
    }

    private void LoadGameDetails()
    {
        // Basic info
        GameTitle.Text = _game.Name;
        AppIdText.Text = $"#{_game.AppId}";
        SizeText.Text = _game.FormattedSize;

        // DRM info
        var drmName = _game.PrimaryDrm.ToString();
        PrimaryDrmText.Text = drmName;
        PrimaryDrmText.Foreground = _game.PrimaryDrm switch
        {
            DrmType.SteamStub => (Brush)FindResource("SuccessBrush"),
            DrmType.Denuvo => (Brush)FindResource("ErrorBrush"),
            _ => (Brush)FindResource("TextSecondaryBrush")
        };
        
        CompatScoreText.Text = $"{_game.CompatibilityScore}%";
        StatusReasonText.Text = _game.CompatibilityReason;

        // Compatibility badge
        if (_game.IsPackageable)
        {
            CompatBadge.Background = new SolidColorBrush(Color.FromArgb(40, 0x3F, 0xB9, 0x50));
            CompatDot.Fill = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
            CompatText.Text = "Compatible";
        }
        else
        {
            CompatBadge.Background = new SolidColorBrush(Color.FromArgb(40, 0xF8, 0x51, 0x49));
            CompatDot.Fill = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
            CompatText.Text = "Not Compatible";
        }

        // DLC
        if (_game.HasDlc && _game.AvailableDlc?.Count > 0)
        {
            DlcList.ItemsSource = _game.AvailableDlc;
            var installed = _game.AvailableDlc.Count(d => d.IsInstalled);
            DlcCountText.Text = $" ({installed}/{_game.AvailableDlc.Count} installed)";
        }
        else
        {
            NoDlcText.Visibility = Visibility.Visible;
            DlcCountText.Text = "";
        }

        // Package status
        if (_game.IsPackaged)
        {
            PackageBtn.Content = "📂 Open Package";
            PackageActionsPanel.Visibility = Visibility.Visible;
        }
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (!_game.IsPackaged || string.IsNullOrEmpty(_game.PackagePath)) return;

        DiagnosticsBtn.IsEnabled = false;
        DiagnosticsBtn.Content = "⏳ Scanning...";

        try
        {
            var report = await DiagnosticService.Instance.AnalyzePackageAsync(_game.PackagePath);

            // Format report for display
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Health Report for {_game.Name}");
            sb.AppendLine($"Status: {report.StatusSummary}");
            sb.AppendLine("----------------------------------------");
            sb.AppendLine($"Main Executable: {report.MainExecutable}");
            sb.AppendLine($"Architecture: {report.Architecture}");
            sb.AppendLine();

            if (report.Issues.Count == 0)
            {
                sb.AppendLine("No issues detected! The package is healthy.");
            }
            else
            {
                sb.AppendLine($"Issues Found ({report.Issues.Count}):");
                foreach (var issue in report.Issues)
                {
                    var icon = issue.Severity switch
                    {
                        IssueSeverity.Error => "❌",
                        IssueSeverity.Warning => "⚠️",
                        _ => "ℹ️"
                    };
                    sb.AppendLine($"{icon} [{issue.Severity}] {issue.Title}");
                    sb.AppendLine($"   {issue.Description}");
                    if (issue.CanFix && !string.IsNullOrEmpty(issue.FixAction))
                    {
                         sb.AppendLine($"   💡 Fix available: {issue.FixAction}");
                    }
                    sb.AppendLine();
                }
            }

            Dispatcher.Invoke(() =>
            {
                DiagnosticsBtn.IsEnabled = true;
                DiagnosticsBtn.Content = "🩺 Health";

                // Using standard message box for now - could be a custom dialog later
                var icon = report.ErrorCount > 0 ? MessageBoxImage.Error :
                           report.WarningCount > 0 ? MessageBoxImage.Warning :
                           MessageBoxImage.Information;

                MessageBox.Show(sb.ToString(), "Package Health Report", MessageBoxButton.OK, icon);
            });
        }
        catch (Exception ex)
        {
             Dispatcher.Invoke(() =>
             {
                 DiagnosticsBtn.IsEnabled = true;
                 DiagnosticsBtn.Content = "🩺 Health";
                 MessageBox.Show($"Diagnostic failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
             });
        }
    }

    private void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (!_game.IsPackaged || string.IsNullOrEmpty(_game.PackagePath)) return;

        VerifyBtn.IsEnabled = false;
        VerifyBtn.Content = "⏳ Verifying...";

        Task.Run(() =>
        {
            try
            {
                var (isValid, mismatches) = PackageBuilder.VerifyIntegrity(_game.PackagePath);

                Dispatcher.Invoke(() =>
                {
                    VerifyBtn.IsEnabled = true;
                    VerifyBtn.Content = "🛡️ Verify";

                    if (isValid)
                    {
                        MessageBox.Show("Package integrity verified successfully! All files match.",
                            "Verification Passed", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show($"Verification failed!\n\nFound {mismatches.Count} issues:\n" +
                            string.Join("\n", mismatches.Take(10)) + (mismatches.Count > 10 ? "\n..." : ""),
                            "Verification Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    VerifyBtn.IsEnabled = true;
                    VerifyBtn.Content = "🛡️ Verify";
                    MessageBox.Show($"Verification error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        });
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!_game.IsPackaged || string.IsNullOrEmpty(_game.PackagePath)) return;

        var launchPath = Path.Combine(_game.PackagePath, "LAUNCH.bat");
        if (File.Exists(launchPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = launchPath,
                    UseShellExecute = true,
                    WorkingDirectory = _game.PackagePath
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch game: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            MessageBox.Show("LAUNCH.bat not found in package folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BackupSaves_Click(object sender, RoutedEventArgs e)
    {
        var saveService = new SaveGameService(new SettingsService());
        var saveDir = saveService.FindSaveDirectory(_game.AppId, _game.PackagePath);

        if (string.IsNullOrEmpty(saveDir))
        {
            MessageBox.Show($"Could not find save games for AppID {_game.AppId}.", "No Saves Found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Backup Saves",
            FileName = $"{_game.Name}_Saves.zip",
            DefaultExt = ".zip",
            Filter = "Zip Files (*.zip)|*.zip"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                await saveService.BackupSavesAsync(_game.AppId, dialog.FileName, _game.PackagePath);
                MessageBox.Show($"Saves backed up to {dialog.FileName}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to backup saves: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void RestoreSaves_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Save Backup",
            Filter = "Zip Files (*.zip)|*.zip"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var saveService = new SaveGameService(new SettingsService());
                await saveService.RestoreSavesAsync(dialog.FileName, _game.AppId, _game.PackagePath);
                MessageBox.Show("Saves restored successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to restore saves: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OpenSteamStore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://store.steampowered.com/app/{_game.AppId}",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OpenSteamDb_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://steamdb.info/app/{_game.AppId}/",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Package_Click(object sender, RoutedEventArgs e)
    {
        if (_game.IsPackaged && !string.IsNullOrEmpty(_game.PackagePath))
        {
            // Open the package folder
            try
            {
                Process.Start("explorer.exe", _game.PackagePath);
            }
            catch { }
        }
        else
        {
            // Trigger package creation via callback with selected mode
            _packageCallback?.Invoke(_game, GetSelectedMode());
            Close();
        }
    }
}
