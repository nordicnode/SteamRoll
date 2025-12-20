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
