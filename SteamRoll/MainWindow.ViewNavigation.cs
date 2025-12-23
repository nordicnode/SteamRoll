using System.Windows;
using System.Windows.Input;
using SteamRoll.Models;
using SteamRoll.Services;

namespace SteamRoll;

/// <summary>
/// Partial class handling view navigation and transitions.
/// </summary>
public partial class MainWindow
{
    // ============================================
    // View Switching Methods
    // ============================================

    private void ViewModeToggle_Click(object sender, RoutedEventArgs e)
    {
        // Toggle UI logic delegates to the control
        GameLibraryViewControl.SetViewMode(HeaderControl.IsViewModeList);
        HeaderControl.SetViewModeIcon(HeaderControl.IsViewModeList);
    }
    
    private void ShowDetailsView(InstalledGame game)
    {
        GameLibraryViewControl.Visibility = Visibility.Collapsed;
        GameDetailsView.Visibility = Visibility.Visible;
        SafeFireAndForget(GameDetailsView.LoadGameAsync(game), "Load Game Details");

    }
    
    private void ShowLibraryView()
    {
        _isLibraryViewActive = true;
        GameDetailsView.Visibility = Visibility.Collapsed;
        TransfersViewControl.Visibility = Visibility.Collapsed;
        StatsBarControl.Visibility = Visibility.Visible;
        GameLibraryViewControl.Visibility = Visibility.Visible;
        
        // Update tab button styles via control method
        HeaderControl.SetLibraryTabActive(true);

        // Cancel any running scan
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        SafeFireAndForget(ScanLibraryAsync(_scanCts.Token), "Scan Library");
    }
    
    private void ShowPackagesView()
    {
        _isLibraryViewActive = false;
        GameDetailsView.Visibility = Visibility.Collapsed;
        TransfersViewControl.Visibility = Visibility.Collapsed;
        StatsBarControl.Visibility = Visibility.Visible;
        GameLibraryViewControl.Visibility = Visibility.Visible; // Reuse library view layout

        // Update tab button styles via control method
        HeaderControl.SetLibraryTabActive(false);

        // Cancel any running scan
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        SafeFireAndForget(ScanPackagesAsync(_scanCts.Token), "Scan Packages");
    }

    private void LibraryTab_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryView();
    }

    private void PackagesTab_Click(object sender, RoutedEventArgs e)
    {
        ShowPackagesView();
    }
    
    private void OnDetailsBackRequested(object? sender, EventArgs e)
    {
        ShowLibraryView();
    }
    
    private async void OnDetailsPackageRequested(object? sender, (InstalledGame Game, PackageMode Mode) args)
    {
        try
        {
            await CreatePackageAsync(args.Game, args.Mode);
            // Refresh just the package state to show updated button without full reload
            GameDetailsView.RefreshPackageState();
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("Failed to create package from details view", ex, "MainWindow");
            ToastService.Instance.ShowError("Package Failed", ex.Message);
        }
    }
    
    private void GameCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is InstalledGame game)
        {
            ShowDetailsView(game);
        }
        else
        {
            LogService.Instance.Warning($"GameCard_Click received unexpected sender type or Tag: {sender?.GetType().Name}", "MainWindow");
        }
    }
}
