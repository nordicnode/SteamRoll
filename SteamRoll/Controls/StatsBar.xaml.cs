using System.Windows;
using System.Windows.Controls;

namespace SteamRoll.Controls;

public partial class StatsBar : UserControl
{
    // Events for filters and sorting
    public event RoutedEventHandler FilterFavoritesClicked;
    public event RoutedEventHandler FilterReadyClicked;
    public event RoutedEventHandler FilterPackagedClicked;
    public event RoutedEventHandler FilterDlcClicked;
    public event RoutedEventHandler FilterUpdateClicked;
    public event SelectionChangedEventHandler SortBoxSelectionChanged;

    public StatsBar()
    {
        InitializeComponent();

        // Initialize Sort Box default selection
        SortBox.SelectedIndex = 0; // Default to Name
    }

    private void FilterFavorites_Click(object sender, RoutedEventArgs e) => FilterFavoritesClicked?.Invoke(this, e);
    private void FilterReady_Click(object sender, RoutedEventArgs e) => FilterReadyClicked?.Invoke(this, e);
    private void FilterPackaged_Click(object sender, RoutedEventArgs e) => FilterPackagedClicked?.Invoke(this, e);
    private void FilterDlc_Click(object sender, RoutedEventArgs e) => FilterDlcClicked?.Invoke(this, e);
    private void FilterUpdate_Click(object sender, RoutedEventArgs e) => FilterUpdateClicked?.Invoke(this, e);

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SortBoxSelectionChanged?.Invoke(this, e);
    }

    // Public methods to update UI state

    public void UpdateStats(int totalGames, int packageableGames, string formattedSize)
    {
        TotalGamesText.Text = totalGames.ToString();
        PackageableText.Text = packageableGames.ToString();
        TotalSizeText.Text = formattedSize;
    }

    public void UpdateNetworkStatus(int peerCount)
    {
        NetworkStatusText.Text = peerCount > 0
            ? $"📡 {peerCount} peer(s) on LAN"
            : "📡 Searching for peers...";
    }

    // Accessors for filter state
    public bool IsFavoritesChecked => FilterFavoritesBtn.IsChecked == true;
    public bool IsReadyChecked => FilterReadyBtn.IsChecked == true;
    public bool IsPackagedChecked => FilterPackagedBtn.IsChecked == true;
    public bool IsDlcChecked => FilterDlcBtn.IsChecked == true;
    public bool IsUpdateChecked => FilterUpdateBtn.IsChecked == true;

    public object SelectedSortItem => SortBox.SelectedItem;

    // Reset sort for initial state
    public void ResetSort()
    {
        SortBox.SelectedIndex = 0;
    }
}
