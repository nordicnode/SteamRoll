using System.Windows;
using SteamRoll.Controls;
using SteamRoll.Models;
using SteamRoll.Services;

namespace SteamRoll;

public partial class MainWindow
{
    private void GameSelection_Click(object sender, RoutedEventArgs e)
    {
        UpdateBatchActionBar();
    }

    private void UpdateBatchActionBar()
    {
        var allGames = _libraryManager.Games;
        var selectedGames = allGames.Where(g => g.IsSelected).ToList();
        var selectedCount = selectedGames.Count;
        var sendableCount = selectedGames.Count(g => g.IsPackaged);

        GameLibraryViewControl.UpdateBatchBar(selectedCount, sendableCount > 0);
    }

    private void BatchClear_Click(object sender, RoutedEventArgs e) => ClearSelection_Click(sender, e);

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var game in _libraryManager.Games)
        {
            game.IsSelected = false;
        }

        UpdateGamesList(GetGamesSnapshot());
        UpdateBatchActionBar();
    }

    private void SelectModeToggle_Click(object sender, RoutedEventArgs e)
    {
        UpdateBatchActionBar();
    }

    private async void BatchPackage_Click(object sender, RoutedEventArgs e)
    {
        var selectedGames = _libraryManager.Games.Where(g => g.IsSelected && g.IsPackageable).ToList();

        if (selectedGames.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Games Selected", "Please select packageable games first.");
            return;
        }

        var result = MessageBox.Show(
            $"Package {selectedGames.Count} game{(selectedGames.Count > 1 ? "s" : "")}?\n\nThis may take a while depending on game sizes.",
            "Batch Package",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        GameLibraryViewControl.SetBatchButtonsEnabled(false);

        var successCount = 0;
        var failCount = 0;

        try
        {
            for (int i = 0; i < selectedGames.Count; i++)
            {
                var game = selectedGames[i];
                StatusText.Text = $"📦 Packaging {i + 1}/{selectedGames.Count}: {game.Name}";

                try
                {
                    var mode = _settingsService.Settings.DefaultPackageMode;
                    await CreatePackageAsync(game, mode);
                    successCount++;
                    game.IsSelected = false; // Deselect after success
                }
                catch (Exception ex)
                {
                    LogService.Instance.Error($"Batch package failed for {game.Name}", ex, "Batch");
                    failCount++;
                }
            }

            if (failCount == 0)
            {
                ToastService.Instance.ShowSuccess(
                    "Batch Complete",
                    $"Successfully packaged {successCount} game{(successCount > 1 ? "s" : "")}."
                );
            }
            else
            {
                ToastService.Instance.ShowWarning(
                    "Batch Complete",
                    $"Packaged {successCount}, failed {failCount}. Check logs for details."
                );
            }

            StatusText.Text = $"✓ Batch packaging complete: {successCount} succeeded, {failCount} failed";
        }
        finally
        {
            GameLibraryViewControl.SetBatchButtonsEnabled(true);
            UpdateGamesList(GetGamesSnapshot());
            UpdateBatchActionBar();
        }
    }

    private async void BatchSendToPeer_Click(object sender, RoutedEventArgs e)
    {
        var selectedGames = _libraryManager.Games.Where(g => g.IsSelected && g.IsPackaged && !string.IsNullOrEmpty(g.PackagePath)).ToList();

        if (selectedGames.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Packages Selected", "Please select packaged games to send.");
            return;
        }

        var peers = _lanDiscoveryService.GetPeers();
        if (peers.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Peers Found", "No other SteamRoll instances found on your network.");
            return;
        }

        PeerInfo? selectedPeer;
        if (peers.Count == 1)
        {
            selectedPeer = peers.First();
        }
        else
        {
            var dialog = new PeerSelectionDialog(peers);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true || dialog.SelectedPeer == null)
            {
                return;
            }
            selectedPeer = dialog.SelectedPeer;
        }

        var totalSize = selectedGames.Sum(g => g.SizeOnDisk);
        var confirm = MessageBox.Show(
            $"Send {selectedGames.Count} package{(selectedGames.Count > 1 ? "s" : "")} to {selectedPeer.HostName}?\n\n" +
            $"Total size: ~{totalSize / (1024 * 1024 * 1024.0):F1} GB",
            "Confirm Batch Transfer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );

        if (confirm != MessageBoxResult.Yes) return;

        GameLibraryViewControl.SetBatchButtonsEnabled(false);

        var successCount = 0;
        var failCount = 0;

        try
        {
            for (int i = 0; i < selectedGames.Count; i++)
            {
                var game = selectedGames[i];
                StatusText.Text = $"📤 Sending {i + 1}/{selectedGames.Count}: {game.Name} to {selectedPeer.HostName}...";

                try
                {
                    var success = await _transferService.SendPackageAsync(
                        selectedPeer.IpAddress,
                        selectedPeer.TransferPort,
                        game.PackagePath!
                    );

                    if (success)
                    {
                        successCount++;
                        game.IsSelected = false;
                    }
                    else
                    {
                        failCount++;
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Error($"Batch transfer failed for {game.Name}", ex, "BatchTransfer");
                    failCount++;
                }
            }

            if (failCount == 0)
            {
                ToastService.Instance.ShowSuccess(
                    "Batch Transfer Complete",
                    $"Successfully sent {successCount} package{(successCount > 1 ? "s" : "")} to {selectedPeer.HostName}."
                );
            }
            else
            {
                ToastService.Instance.ShowWarning(
                    "Batch Transfer Complete",
                    $"Sent {successCount}, failed {failCount}. Check logs for details."
                );
            }

            StatusText.Text = $"✓ Batch transfer complete: {successCount} sent, {failCount} failed";
        }
        finally
        {
            GameLibraryViewControl.SetBatchButtonsEnabled(true);
            UpdateGamesList(GetGamesSnapshot());
            UpdateBatchActionBar();
        }
    }
}
