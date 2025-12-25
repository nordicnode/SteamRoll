using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SteamRoll.Services;

namespace SteamRoll.Controls;

/// <summary>
/// Displays recently played games in a horizontal carousel.
/// </summary>
public partial class RecentGamesWidget : UserControl
{
    /// <summary>
    /// Event raised when a game is clicked.
    /// </summary>
    public event EventHandler<int>? GameClicked;

    /// <summary>
    /// Number of recent games to display.
    /// </summary>
    public int MaxGames { get; set; } = 6;

    public RecentGamesWidget()
    {
        InitializeComponent();
        Loaded += RecentGamesWidget_Loaded;
        
        // Subscribe to playtime updates
        PlaytimeService.Instance.PlaytimeUpdated += (s, e) => RefreshGames();
    }

    private void RecentGamesWidget_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshGames();
    }

    /// <summary>
    /// Refreshes the list of recent games.
    /// </summary>
    public void RefreshGames()
    {
        try
        {
            var recentGames = PlaytimeService.Instance.GetRecentGames(MaxGames);
            
            if (recentGames.Count == 0)
            {
                RecentGamesList.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Visible;
                TotalPlaytime.Text = "";
            }
            else
            {
                // Convert to display models with image URLs
                var displayItems = recentGames.Select(p => new RecentGameDisplayItem
                {
                    AppId = p.AppId,
                    GameName = p.GameName,
                    TotalPlaytimeDisplay = p.TotalPlaytimeDisplay,
                    LastPlayed = p.LastPlayed,
                    HeaderImageUrl = $"https://steamcdn-a.akamaihd.net/steam/apps/{p.AppId}/capsule_184x69.jpg"
                }).ToList();

                RecentGamesList.ItemsSource = displayItems;
                RecentGamesList.Visibility = Visibility.Visible;
                EmptyState.Visibility = Visibility.Collapsed;

                // Show total playtime
                var totalMinutes = PlaytimeService.Instance.GetTotalPlaytimeMinutes();
                if (totalMinutes > 0)
                {
                    var hours = totalMinutes / 60;
                    TotalPlaytime.Text = hours > 0 ? $"Total: {hours}h" : $"Total: {totalMinutes}m";
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to load recent games: {ex.Message}", "RecentGamesWidget");
        }
    }

    private void RecentGame_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is RecentGameDisplayItem item)
        {
            GameClicked?.Invoke(this, item.AppId);
        }
    }
}

/// <summary>
/// Display model for recent games.
/// </summary>
public class RecentGameDisplayItem
{
    public int AppId { get; set; }
    public string GameName { get; set; } = "";
    public string TotalPlaytimeDisplay { get; set; } = "";
    public DateTime? LastPlayed { get; set; }
    public string HeaderImageUrl { get; set; } = "";
}
