using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SteamRoll.Services;

namespace SteamRoll.Controls;

/// <summary>
/// Dedicated page view for playtime statistics.
/// </summary>
public partial class PlaytimeStatsView : UserControl
{
    /// <summary>
    /// Event raised when back button is clicked.
    /// </summary>
    public event RoutedEventHandler? BackClicked;

    /// <summary>
    /// Event raised when a game is clicked.
    /// </summary>
    public event EventHandler<int>? GameClicked;

    public PlaytimeStatsView()
    {
        InitializeComponent();
        Loaded += PlaytimeStatsView_Loaded;
        
        // Subscribe to playtime updates
        PlaytimeService.Instance.PlaytimeUpdated += (s, e) => 
        {
            Dispatcher.Invoke(RefreshStats);
        };
    }

    private void PlaytimeStatsView_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshStats();
    }

    /// <summary>
    /// Refreshes all statistics.
    /// </summary>
    public void RefreshStats()
    {
        var playtimeService = PlaytimeService.Instance;

        // Summary stats
        var totalMinutes = playtimeService.GetTotalPlaytimeMinutes();
        var hours = totalMinutes / 60;
        TotalPlaytimeText.Text = hours > 0 ? $"{hours}h" : $"{totalMinutes}m";

        var allPlaytimes = playtimeService.GetAllPlaytimes();
        GamesPlayedText.Text = allPlaytimes.Count.ToString();

        // Calculate sessions
        var allSessions = allPlaytimes.SelectMany(p => p.Sessions).ToList();
        TotalSessionsText.Text = allSessions.Count.ToString();

        if (allSessions.Count > 0)
        {
            var avgMinutes = allSessions.Average(s => s.DurationMinutes);
            AvgSessionText.Text = avgMinutes >= 60
                ? $"{(int)(avgMinutes / 60)}h"
                : $"{(int)avgMinutes}m";
        }
        else
        {
            AvgSessionText.Text = "0m";
        }

        // Top games
        var topGames = playtimeService.GetTopGames(10)
            .Select((p, i) => new TopGameItem
            {
                Rank = i + 1,
                GameName = p.GameName,
                TotalPlaytimeDisplay = p.TotalPlaytimeDisplay,
                LastPlayedDisplay = p.LastPlayed.HasValue
                    ? $"Last: {p.LastPlayed:MMM d}"
                    : "Never"
            })
            .ToList();
        TopGamesList.ItemsSource = topGames;

        // Recent sessions
        var recentSessions = allPlaytimes
            .SelectMany(p => p.Sessions.Select(s => new RecentSessionItem
            {
                GameName = p.GameName,
                DateDisplay = s.StartTime.ToString("MMM d, h:mm tt"),
                DurationDisplay = s.DurationMinutes >= 60
                    ? $"{s.DurationMinutes / 60}h {s.DurationMinutes % 60}m"
                    : $"{s.DurationMinutes}m",
                StartTime = s.StartTime
            }))
            .OrderByDescending(s => s.StartTime)
            .Take(15)
            .ToList();
        RecentSessionsList.ItemsSource = recentSessions;

        // Recently played games
        var recentGames = playtimeService.GetRecentGames(8)
            .Select(p => new RecentGameDisplayItem
            {
                AppId = p.AppId,
                GameName = p.GameName,
                TotalPlaytimeDisplay = p.TotalPlaytimeDisplay,
                HeaderImageUrl = $"https://steamcdn-a.akamaihd.net/steam/apps/{p.AppId}/capsule_184x69.jpg"
            })
            .ToList();
        RecentGamesList.ItemsSource = recentGames;
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        BackClicked?.Invoke(this, e);
    }

    private void RecentGame_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is RecentGameDisplayItem item)
        {
            GameClicked?.Invoke(this, item.AppId);
        }
    }
}

// Re-use display item classes from Windows namespace
public class TopGameItem
{
    public int Rank { get; set; }
    public string GameName { get; set; } = "";
    public string TotalPlaytimeDisplay { get; set; } = "";
    public string LastPlayedDisplay { get; set; } = "";
}

public class RecentSessionItem
{
    public string GameName { get; set; } = "";
    public string DateDisplay { get; set; } = "";
    public string DurationDisplay { get; set; } = "";
    public DateTime StartTime { get; set; }
}
