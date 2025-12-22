using System.Windows;
using System.Windows.Controls;
using SteamRoll.Models;

namespace SteamRoll;

/// <summary>
/// Partial class handling filter and search functionality.
/// </summary>
public partial class MainWindow
{
    // ============================================
    // Filters & Sorting
    // ============================================

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Skip if not yet initialized (fires during XAML loading)
        if (!IsLoaded) return;
        
        ApplyFilters();
    }

    private void FilterReady_Click(object sender, RoutedEventArgs e) => ApplyFilters();
    private void FilterPackaged_Click(object sender, RoutedEventArgs e) => ApplyFilters();
    private void FilterDlc_Click(object sender, RoutedEventArgs e) => ApplyFilters();
    private void FilterUpdate_Click(object sender, RoutedEventArgs e) => ApplyFilters();
    private void FilterFavorites_Click(object sender, RoutedEventArgs e) => ApplyFilters();

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }
    
    private void ApplyFilters()
    {
        var searchText = HeaderControl.SearchText;
        var isSearchActive = !string.IsNullOrWhiteSpace(searchText) && searchText != "🔍 Search games...";
        
        var filterReady = StatsBarControl.IsReadyChecked;
        var filterPackaged = StatsBarControl.IsPackagedChecked;
        var filterDlc = StatsBarControl.IsDlcChecked;
        var filterUpdate = StatsBarControl.IsUpdateChecked;
        var filterFavorites = StatsBarControl.IsFavoritesChecked;
        
        var filtered = GetGamesSnapshot().AsEnumerable();
        
        // Apply search filter
        if (isSearchActive)
        {
            filtered = filtered.Where(g => 
                g.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                g.AppId.ToString().Contains(searchText));
        }
        
        // Apply toggle filters
        if (filterReady)
            filtered = filtered.Where(g => g.IsPackageable);
        
        if (filterPackaged)
            filtered = filtered.Where(g => g.IsPackaged);
            
        if (filterDlc)
            filtered = filtered.Where(g => g.HasDlc);
        
        if (filterUpdate)
            filtered = filtered.Where(g => g.UpdateAvailable);

        if (filterFavorites)
            filtered = filtered.Where(g => g.IsFavorite);

        // Apply Sorting - combine favorites pinning with primary sort in single pass
        // If we are NOT filtering BY favorites, pin favorites to top first
        var pinFavorites = !filterFavorites;
        
        if (StatsBarControl.SelectedSortItem is ComboBoxItem item && item.Tag is string sortType)
        {
            // Use a combined sort: favorites first (if pinning), then by selected sort type
            if (pinFavorites)
            {
                filtered = sortType switch
                {
                    "Size" => filtered.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.SizeOnDisk),
                    "LastPlayed" => filtered.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.LastPlayed),
                    "ReviewScore" => filtered.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.ReviewPositivePercent ?? 0),
                    "ReleaseDate" => filtered.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.BuildId),
                    _ => filtered.OrderByDescending(g => g.IsFavorite).ThenBy(g => g.Name)
                };
            }
            else
            {
                filtered = sortType switch
                {
                    "Size" => filtered.OrderByDescending(g => g.SizeOnDisk),
                    "LastPlayed" => filtered.OrderByDescending(g => g.LastPlayed),
                    "ReviewScore" => filtered.OrderByDescending(g => g.ReviewPositivePercent ?? 0),
                    "ReleaseDate" => filtered.OrderByDescending(g => g.BuildId),
                    _ => filtered.OrderBy(g => g.Name)
                };
            }
        }
        else
        {
            // Default sort by name, with favorites pinned if not filtering by favorites
            filtered = pinFavorites 
                ? filtered.OrderByDescending(g => g.IsFavorite).ThenBy(g => g.Name)
                : filtered.OrderBy(g => g.Name);
        }
        
        UpdateGamesList(filtered.ToList());
    }
}
