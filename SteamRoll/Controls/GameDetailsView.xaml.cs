using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SteamRoll.Models;
using SteamRoll.Services;

namespace SteamRoll.Controls;

/// <summary>
/// In-app game details view with Steam Store data, screenshots, trailers, and DLC info.
/// </summary>
public partial class GameDetailsView : UserControl
{
    private InstalledGame? _game;
    private readonly SteamStoreService _storeService;
    
    public event EventHandler? BackRequested;
    public event EventHandler<(InstalledGame Game, PackageMode Mode)>? PackageRequested;

    public GameDetailsView()
    {
        InitializeComponent();
        // Use shared singleton instance to avoid multiple HttpClient instances
        _storeService = SteamStoreService.Instance;
    }



    /// <summary>
    /// Load and display details for the specified game.
    /// </summary>
    public async Task LoadGameAsync(InstalledGame game)
    {
        _game = game;
        LoadingOverlay.Visibility = Visibility.Visible;
        ContentGrid.Opacity = 0;
        
        try
        {
            LoadBasicDetails();
            await LoadSteamStoreDataAsync();
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            AnimateContentIn();
        }
    }
    
    private void AnimateContentIn()
    {
        // Fade and slide in animation
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slideUp = new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        
        ContentGrid.BeginAnimation(OpacityProperty, fadeIn);
        ContentTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    private void LoadBasicDetails()
    {
        if (_game == null) return;
        
        // Title and meta
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
        
        CompatScoreText.Text = $"{_game.CompatibilityScore * 100:F0}%";
        StatusReasonText.Text = _game.CompatibilityReason;
        
        // Update progress bar width (assuming max width of ~200 from container)
        var scorePercent = _game.CompatibilityScore;
        CompatProgressBar.Width = Math.Max(8, 180 * scorePercent); // Min 8px for visibility

        // Compatibility badge
        if (_game.IsPackageable)
        {
            CompatBadge.Background = new SolidColorBrush(Color.FromArgb(50, 0x3F, 0xB9, 0x50));
            CompatDot.Fill = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
            CompatText.Text = "Compatible";
            CompatScoreText.Foreground = (Brush)FindResource("SuccessBrush");
        }
        else
        {
            CompatBadge.Background = new SolidColorBrush(Color.FromArgb(50, 0xF8, 0x51, 0x49));
            CompatDot.Fill = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
            CompatText.Text = "Not Compatible";
            CompatScoreText.Foreground = (Brush)FindResource("ErrorBrush");
        }

        // DLC
        if (_game.HasDlc && _game.AvailableDlc?.Count > 0)
        {
            DlcList.ItemsSource = _game.AvailableDlc;
            var installed = _game.AvailableDlc.Count(d => d.IsInstalled);
            DlcCountText.Text = $" ({installed}/{_game.AvailableDlc.Count} installed)";
            NoDlcText.Visibility = Visibility.Collapsed;
        }
        else
        {
            DlcList.ItemsSource = null;
            NoDlcText.Visibility = Visibility.Visible;
            DlcCountText.Text = "";
        }

        // Package button state
        if (_game.IsPackaged)
        {
            PackageBtnText.Text = "Open";
            VerifyIntegrityBtn.Visibility = Visibility.Visible;
        }
        else
        {
            PackageBtnText.Text = "Build";
            VerifyIntegrityBtn.Visibility = Visibility.Collapsed;
        }
        
        // Load header image with retry logic
        LoadHeaderImageWithRetry();
    }

    private async void LoadHeaderImageWithRetry()
    {
        if (_game == null) return;
        
        // Capture current game to detect if it changes during async load
        var targetAppId = _game.AppId;
        var targetUrl = _game.HeaderImageUrl;
        
        // Clear previous image immediately to prevent showing wrong game's image
        HeaderImageBrush.ImageSource = null;
        
        int retries = 3;
        while (retries > 0)
        {
            // Check if game changed during loading
            if (_game == null || _game.AppId != targetAppId)
            {
                LogService.Instance.Debug($"Game changed during image load, aborting for AppId {targetAppId}", "GameDetailsView");
                return;
            }
            
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(targetUrl);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // Ensure fresh load on retry
                bitmap.EndInit();

                // Wait for download to complete if it's async
                if (bitmap.IsDownloading)
                {
                    var tcs = new TaskCompletionSource<bool>();
                    bitmap.DownloadCompleted += (s, e) => tcs.TrySetResult(true);
                    bitmap.DownloadFailed += (s, e) => tcs.TrySetException(new Exception("Download failed"));
                    bitmap.DecodeFailed += (s, e) => tcs.TrySetException(new Exception("Decode failed"));
                    await tcs.Task;
                }

                // Final check before setting the image - game might have changed during download
                if (_game == null || _game.AppId != targetAppId)
                {
                    LogService.Instance.Debug($"Game changed during image download, aborting for AppId {targetAppId}", "GameDetailsView");
                    return;
                }

                HeaderImageBrush.ImageSource = bitmap;
                return; // Success
            }
            catch (Exception ex)
            {
                retries--;
                LogService.Instance.Debug($"Failed to load header image (Remaining retries: {retries}): {ex.Message}", "GameDetailsView");
                if (retries > 0) await Task.Delay(1000);
            }
        }
        
        // Final fallback: Use a placeholder or leave empty
        LogService.Instance.Warning($"All retries failed for header image (AppId: {targetAppId}).", "GameDetailsView");
    }

    private async Task LoadSteamStoreDataAsync()
    {
        if (_game == null) return;
        
        try
        {
            var details = await _storeService.GetGameDetailsAsync(_game.AppId);
            
            if (details != null)
            {
                // Description
                DescriptionText.Text = !string.IsNullOrEmpty(details.Description) 
                    ? details.Description 
                    : "No description available.";
                
                // Genres
                GenresText.Text = !string.IsNullOrEmpty(details.GenresDisplay) 
                    ? details.GenresDisplay 
                    : "";
                
                // Developer
                DeveloperText.Text = details.Developers.Count > 0 
                    ? $"Developer: {details.DevelopersDisplay}" 
                    : "";
                
                // Release date
                ReleaseDateText.Text = !string.IsNullOrEmpty(details.ReleaseDate) 
                    ? $"Released: {details.ReleaseDate}" 
                    : "";
                
                // Metacritic
                if (details.MetacriticScore.HasValue)
                {
                    MetacriticBadge.Visibility = Visibility.Visible;
                    MetacriticText.Text = details.MetacriticScore.Value.ToString();
                    
                    var scoreColor = details.MetacriticScore.Value switch
                    {
                        >= 75 => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)), // Green
                        >= 50 => new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22)), // Yellow
                        _ => new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49))      // Red
                    };
                    MetacriticText.Foreground = scoreColor;
                }
                
                // Steam Reviews
                if (details.ReviewPositivePercent.HasValue)
                {
                    SteamReviewsBadge.Visibility = Visibility.Visible;
                    ReviewDescriptionText.Text = details.ReviewDescription ?? "Reviews";
                    ReviewPercentText.Text = $" ({details.ReviewPositivePercent}%)";
                    
                    var reviewColor = details.ReviewPositivePercent.Value switch
                    {
                        >= 70 => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)), // Green
                        >= 40 => new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22)), // Yellow
                        _ => new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49))      // Red
                    };
                    
                    ReviewDescriptionText.Foreground = reviewColor;
                    ReviewPercentText.Foreground = reviewColor; // Make it all match for readability
                }
                
                // Update header image from API response if it wasn't loaded
                // The API provides a more reliable header_image URL than the CDN fallback
                if (HeaderImageBrush.ImageSource == null && !string.IsNullOrEmpty(details.HeaderImage))
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(details.HeaderImage);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        
                        if (bitmap.IsDownloading)
                        {
                            var tcs = new TaskCompletionSource<bool>();
                            bitmap.DownloadCompleted += (s, e) => tcs.TrySetResult(true);
                            bitmap.DownloadFailed += (s, e) => tcs.TrySetResult(false);
                            bitmap.DecodeFailed += (s, e) => tcs.TrySetResult(false);
                            
                            if (await tcs.Task && _game != null)
                            {
                                HeaderImageBrush.ImageSource = bitmap;
                                // Cache the working URL for future use
                                _game.ResolvedHeaderImageUrl = details.HeaderImage;
                            }
                        }
                        else
                        {
                            HeaderImageBrush.ImageSource = bitmap;
                            if (_game != null)
                            {
                                _game.ResolvedHeaderImageUrl = details.HeaderImage;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.Debug($"Failed to load header image from API URL: {ex.Message}", "GameDetailsView");
                    }
                }

                // Background image
                if (!string.IsNullOrEmpty(details.BackgroundImage))
                {
                    try
                    {
                        var bitmap = new BitmapImage(new Uri(details.BackgroundImage));
                        BackgroundImage.Source = bitmap;
                    }
                    catch (Exception ex)
                {
                    LogService.Instance.Debug($"Failed to load background image: {ex.Message}", "GameDetailsView");
                }
                }
                
                // Screenshots
                if (details.Screenshots.Count > 0)
                {
                    ScreenshotsList.ItemsSource = details.Screenshots.Take(8);
                    NoScreenshotsText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    NoScreenshotsText.Visibility = Visibility.Visible;
                }
                
                // Features
                if (details.Features.Count > 0)
                {
                    FeaturesList.ItemsSource = details.Features.Take(15);
                }
                
                // Trailer
                if (details.Trailers.Count > 0 && !string.IsNullOrEmpty(details.Trailers[0].VideoUrl))
                {
                    TrailerSection.Visibility = Visibility.Visible;
                    try
                    {
                        TrailerPlayer.Source = new Uri(details.Trailers[0].VideoUrl);
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.Debug($"Failed to load trailer: {ex.Message}", "GameDetailsView");
                        TrailerSection.Visibility = Visibility.Collapsed;
                    }
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
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        // Stop any playing media
        TrailerPlayer.Stop();
        TrailerPlayer.Source = null;
        
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Package_Click(object sender, RoutedEventArgs e)
    {
        if (_game == null) return;
        
        if (_game.IsPackaged && !string.IsNullOrEmpty(_game.PackagePath))
        {
            // Open the package folder
            try
            {
                Process.Start("explorer.exe", _game.PackagePath);
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Failed to open package folder: {ex.Message}", ex, "GameDetailsView");
            }
        }
        else
        {
            var selectedItem = EmulatorModeCombo.SelectedItem as ComboBoxItem;
            var tag = selectedItem?.Tag?.ToString();
            var mode = tag == "CreamApi" ? PackageMode.CreamApi : PackageMode.Goldberg;
            
            PackageRequested?.Invoke(this, (_game, mode));
        }
    }

    private void PlayTrailer_Click(object sender, RoutedEventArgs e)
    {
        TrailerPlayer.Play();
    }

    private void StopTrailer_Click(object sender, RoutedEventArgs e)
    {
        TrailerPlayer.Stop();
    }

    private void Screenshot_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string url)
        {
            // Open screenshot in browser for full view
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Failed to open screenshot URL: {ex.Message}", ex, "GameDetailsView");
            }
        }
    }

    private void OpenSteamStore_Click(object sender, RoutedEventArgs e)
    {
        if (_game == null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://store.steampowered.com/app/{_game.AppId}",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Could not open Steam Store: {ex.Message}", "GameDetailsView");
        }
    }

    private void OpenSteamDb_Click(object sender, RoutedEventArgs e)
    {
        if (_game == null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://steamdb.info/app/{_game.AppId}/",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Could not open SteamDB: {ex.Message}", "GameDetailsView");
        }
    }

    private void VerifyIntegrity_Click(object sender, RoutedEventArgs e)
    {
        if (_game == null || string.IsNullOrEmpty(_game.PackagePath)) return;
        
        try
        {
            var (isValid, mismatches) = PackageBuilder.VerifyIntegrity(_game.PackagePath);
            
            if (isValid)
            {
                ToastService.Instance.ShowSuccess("Integrity Verified", "All files are intact.");
            }
            else
            {
                var message = mismatches.Count > 5
                    ? $"{mismatches.Count} files failed verification"
                    : string.Join("\n", mismatches);
                    
                MessageBox.Show(
                    $"Package integrity check failed:\n\n{message}",
                    "Integrity Check Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Integrity verification error: {ex.Message}", ex, "GameDetailsView");
            MessageBox.Show($"Error verifying integrity: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
