using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SteamRoll.Models;

namespace SteamRoll.Controls;

public partial class GameLibraryView : UserControl
{
    // Events
    public event RoutedEventHandler? RefreshClicked;
    public event MouseButtonEventHandler? GameCardClicked;
    public event RoutedEventHandler? FavoriteToggled;
    public event RoutedEventHandler? GameSelectionClicked;

    // Batch Events
    public event RoutedEventHandler? BatchPackageClicked;
    public event RoutedEventHandler? BatchClearClicked;
    public event RoutedEventHandler? BatchSendToPeerClicked;

    // Item Action Events (from context menu or buttons)
    public event RoutedEventHandler? PackageGameClicked;
    public event RoutedEventHandler? SendGameToPeerClicked;
    public event RoutedEventHandler? UpdatePackageClicked;
    public event RoutedEventHandler? ContextMenuFavoriteClicked;
    public event RoutedEventHandler? ContextMenuBackupSaveClicked;
    public event RoutedEventHandler? ContextMenuDeletePackageClicked;
    public event RoutedEventHandler? ContextMenuSyncSavesClicked;
    public event RoutedEventHandler? ContextMenuAdvancedConfigClicked;
    public event RoutedEventHandler? ContextMenuOpenInstallFolderClicked;
    public event RoutedEventHandler? ContextMenuVerifyIntegrityClicked;
    public event RoutedEventHandler? ContextMenuRepairFromPeerClicked;
    public event RoutedEventHandler? ContextMenuOpenSteamStoreClicked;
    public event RoutedEventHandler? ContextMenuCreatePackageClicked;
    public event RoutedEventHandler? ContextMenuViewDetailsClicked;
    public event RoutedEventHandler? ContextMenuUpdatePackageClicked;
    public event RoutedEventHandler? InstallFromPeerClicked;

    public GameLibraryView()
    {
        InitializeComponent();
    }

    // Event Handlers
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshClicked?.Invoke(this, e);

    private void GameCard_Click(object sender, MouseButtonEventArgs e) => GameCardClicked?.Invoke(sender, e);

    private void FavoriteToggle_Click(object sender, RoutedEventArgs e) => FavoriteToggled?.Invoke(sender, e);

    private void GameSelection_Click(object sender, RoutedEventArgs e) => GameSelectionClicked?.Invoke(sender, e);

    private void PackageButton_Click(object sender, RoutedEventArgs e) => PackageGameClicked?.Invoke(sender, e);
    private void SendToPeerButton_Click(object sender, RoutedEventArgs e) => SendGameToPeerClicked?.Invoke(sender, e);
    private void UpdatePackageButton_Click(object sender, RoutedEventArgs e) => UpdatePackageClicked?.Invoke(sender, e);
    private void InstallFromPeerButton_Click(object sender, RoutedEventArgs e) => InstallFromPeerClicked?.Invoke(sender, e);

    private void BatchPackage_Click(object sender, RoutedEventArgs e) => BatchPackageClicked?.Invoke(this, e);
    private void BatchClear_Click(object sender, RoutedEventArgs e) => BatchClearClicked?.Invoke(this, e);
    private void BatchSendToPeer_Click(object sender, RoutedEventArgs e) => BatchSendToPeerClicked?.Invoke(this, e);

    // Context Menu Forwarders
    private void ContextMenu_Favorite_Click(object sender, RoutedEventArgs e) => ContextMenuFavoriteClicked?.Invoke(sender, e);
    private void ContextMenu_BackupSave_Click(object sender, RoutedEventArgs e) => ContextMenuBackupSaveClicked?.Invoke(sender, e);
    private void ContextMenu_Package_Click(object sender, RoutedEventArgs e) => ContextMenuCreatePackageClicked?.Invoke(sender, e);
    private void ContextMenu_DeletePackage_Click(object sender, RoutedEventArgs e) => ContextMenuDeletePackageClicked?.Invoke(sender, e);
    private void ContextMenu_SendToPeer_Click(object sender, RoutedEventArgs e) => SendGameToPeerClicked?.Invoke(sender, e); // Reuse
    private void ContextMenu_SyncSaves_Click(object sender, RoutedEventArgs e) => ContextMenuSyncSavesClicked?.Invoke(sender, e);
    private void ContextMenu_AdvancedConfig_Click(object sender, RoutedEventArgs e) => ContextMenuAdvancedConfigClicked?.Invoke(sender, e);
    private void ContextMenu_OpenInstallFolder_Click(object sender, RoutedEventArgs e) => ContextMenuOpenInstallFolderClicked?.Invoke(sender, e);
    private void ContextMenu_ViewDetails_Click(object sender, RoutedEventArgs e) => ContextMenuViewDetailsClicked?.Invoke(sender, e);
    private void ContextMenu_VerifyIntegrity_Click(object sender, RoutedEventArgs e) => ContextMenuVerifyIntegrityClicked?.Invoke(sender, e);
    private void ContextMenu_RepairFromPeer_Click(object sender, RoutedEventArgs e) => ContextMenuRepairFromPeerClicked?.Invoke(sender, e);
    private void ContextMenu_OpenSteamStore_Click(object sender, RoutedEventArgs e) => ContextMenuOpenSteamStoreClicked?.Invoke(sender, e);
    private void ContextMenu_UpdatePackage_Click(object sender, RoutedEventArgs e) => ContextMenuUpdatePackageClicked?.Invoke(sender, e);


    // Public State Management Methods

    public void SetGames(IEnumerable games)
    {
        GamesList.ItemsSource = games;
        GamesListView.ItemsSource = games;

        // Handle empty state logic in parent or helper
        // But for display consistency:
        var count = 0;
        if (games is ICollection col) count = col.Count;
        else { foreach(var item in games) count++; } // Inefficient but functional

        var isEmpty = count == 0;

        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        SkeletonView.Visibility = Visibility.Collapsed;

        if (isEmpty)
        {
            GamesGridScroll.Visibility = Visibility.Collapsed;
            GamesListView.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Visibility of grid vs list depends on view mode, which is controlled externally
            // We just ensure they are not hidden due to empty state.
            // But we need to know the view mode.
            // Let's expose methods to set view mode.
        }
    }

    public void SetViewMode(bool isList)
    {
        if (EmptyStatePanel.Visibility == Visibility.Visible)
        {
             GamesGridScroll.Visibility = Visibility.Collapsed;
             GamesListView.Visibility = Visibility.Collapsed;
             return;
        }

        if (isList)
        {
            GamesGridScroll.Visibility = Visibility.Collapsed;
            GamesList.Visibility = Visibility.Collapsed;
            GamesListView.Visibility = Visibility.Visible;
        }
        else
        {
            GamesGridScroll.Visibility = Visibility.Visible;
            GamesList.Visibility = Visibility.Visible;
            GamesListView.Visibility = Visibility.Collapsed;
        }
    }

    public void SetLoading(bool isLoading)
    {
        if (isLoading)
        {
            SkeletonView.Visibility = Visibility.Visible;
            GamesList.Visibility = Visibility.Collapsed;
            GamesListView.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            GamesGridScroll.Visibility = Visibility.Collapsed;
        }
        else
        {
            SkeletonView.Visibility = Visibility.Collapsed;
            // The rest depends on SetGames result
        }
    }

    public void UpdateBatchBar(int selectedCount, bool canSend)
    {
        if (selectedCount > 0)
        {
            BatchSelectionText.Text = $"{selectedCount} game{(selectedCount > 1 ? "s" : "")} selected";
            BatchActionBar.Visibility = Visibility.Visible;
            BatchSendBtn.IsEnabled = canSend;
        }
        else
        {
            BatchActionBar.Visibility = Visibility.Collapsed;
        }
    }

    public void RefreshList()
    {
        GamesList.Items.Refresh();
        GamesListView.Items.Refresh();
    }

    public void SetEmptyStateMessage(string title, string message)
    {
        EmptyStateTitle.Text = title;
        EmptyStateMessage.Text = message;
    }

    public void SetBatchButtonsEnabled(bool enabled)
    {
        BatchPackageButton.IsEnabled = enabled;
        BatchClearButton.IsEnabled = enabled;
        if (enabled)
        {
             // Send button state is managed by UpdateBatchBar logic usually, but global disable overrides
             // If re-enabling, we should respect the logical state, but we don't store it here easily.
             // Assume caller handles it or updates batch bar again.
        }
        else
        {
            BatchSendBtn.IsEnabled = false;
        }
    }
}
