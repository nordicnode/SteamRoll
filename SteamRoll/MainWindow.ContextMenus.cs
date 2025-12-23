using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SteamRoll.Controls;
using SteamRoll.Models;
using SteamRoll.Services;

namespace SteamRoll;

public partial class MainWindow
{
    private InstalledGame? GetGameFromContextMenu(object sender)
    {
        if (sender is MenuItem menuItem &&
            menuItem.Parent is ContextMenu contextMenu &&
            contextMenu.PlacementTarget is FrameworkElement element &&
            element.DataContext is InstalledGame game)
        {
            return game;
        }
        return null;
    }

    private void ContextMenu_Package_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null) return;

        if (game.IsPackaged && !string.IsNullOrEmpty(game.PackagePath))
        {
            try { Process.Start("explorer.exe", game.PackagePath); } catch (Exception ex) { LogService.Instance.Debug($"Could not open package folder: {ex.Message}", "MainWindow"); }
        }
        else
        {
            ToastService.Instance.ShowInfo("Package Game", $"Use the Package button on the game card to create a package for {game.Name}");
        }
    }

    private void ContextMenu_SendToPeer_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game?.IsPackaged == true)
        {
            ToastService.Instance.ShowInfo("Send to Peer", $"Use the 'Send to Peer' button on the game card to transfer {game.Name}");
        }
    }

    private void ContextMenu_Favorite_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game != null)
        {
            game.IsFavorite = !game.IsFavorite;
            _cacheService.UpdateCache(game);
            _cacheService.SaveCache();
            ApplyFilters(); // Refresh sort order
        }
    }

    private async void ContextMenu_BackupSave_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null) return;

        try
        {
            var saveDir = _saveGameService.FindSaveDirectory(game.AppId, game.PackagePath);
            if (string.IsNullOrEmpty(saveDir))
            {
                ToastService.Instance.ShowWarning("No Saves Found", $"Could not find local saves for {game.Name}.");
                return;
            }

            var backupDir = Path.Combine(_settingsService.Settings.OutputPath, "Backups");
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupPath = Path.Combine(backupDir, $"{FormatUtils.SanitizeFileName(game.Name)}_Save_{timestamp}.zip");

            await _saveGameService.BackupSavesAsync(game.AppId, backupPath, game.PackagePath);

            ToastService.Instance.ShowSuccess("Backup Complete", $"Saved to Backups/{Path.GetFileName(backupPath)}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Backup failed: {ex.Message}", ex, "Backup");
            ToastService.Instance.ShowError("Backup Failed", ex.Message);
        }
    }

    private async void ContextMenu_SyncSaves_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null) return;

        // Get available peers
        var peers = _lanDiscoveryService.GetPeers();
        if (peers.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Peers Found", "No other SteamRoll instances found on your network.");
            return;
        }

        // Peer selection
        PeerInfo? selectedPeer;
        if (peers.Count == 1)
        {
            selectedPeer = peers.First();
        }
        else
        {
            var dialog = new PeerSelectionDialog(peers) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedPeer == null) return;
            selectedPeer = dialog.SelectedPeer;
        }

        // Confirm sync (Push)
        var result = MessageBox.Show(
            $"Send saves for {game.Name} to {selectedPeer.HostName}?\n\nThis will overwrite their local saves.",
            "Send Saves",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            StatusText.Text = $"⏳ Sending saves for {game.Name} to {selectedPeer.HostName}...";
            LoadingOverlay.Show("Sending saves...");

            // Check if we have saves to send
            var saveDir = _saveGameService.FindSaveDirectory(game.AppId, game.PackagePath);
            if (string.IsNullOrEmpty(saveDir) || Directory.GetFiles(saveDir).Length == 0)
            {
                ToastService.Instance.ShowWarning("No Saves Found", $"Could not find local saves for {game.Name} to send.");
                LoadingOverlay.Hide();
                return;
            }

            // Create zip in temp and send
            var tempZip = System.IO.Path.GetTempFileName();
            File.Delete(tempZip);
            await _saveGameService.BackupSavesAsync(game.AppId, tempZip, game.PackagePath);

            // Use SendSaveSyncAsync
            var success = await _transferService.SendSaveSyncAsync(
                selectedPeer.IpAddress,
                selectedPeer.TransferPort,
                tempZip,
                game.Name
            );

            File.Delete(tempZip); // Cleanup

            LoadingOverlay.Hide();

            if (success)
            {
                StatusText.Text = $"✓ Sent saves for {game.Name} to {selectedPeer.HostName}";
                ToastService.Instance.ShowSuccess("Save Sync", "Saves sent successfully!");
            }
            else
            {
                StatusText.Text = "⚠ Failed to send saves";
                ToastService.Instance.ShowError("Save Sync Failed", "Peer rejected or error occurred.");
            }
        }
        catch (Exception ex)
        {
            LoadingOverlay.Hide();
            LogService.Instance.Error($"Save sync failed: {ex.Message}", ex, "SaveSync");
            ToastService.Instance.ShowError("Save Sync Error", ex.Message);
        }
    }

    private async void ContextMenu_DeletePackage_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null || !game.IsPackaged || string.IsNullOrEmpty(game.PackagePath)) return;

        var result = MessageBox.Show(
            $"Are you sure you want to delete the package for {game.Name}?\n\nThis will permanently remove the folder:\n{game.PackagePath}",
            "Delete Package",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                await PackageBuilder.DeletePackageAsync(game.PackagePath);

                // Update game state
                game.IsPackaged = false;
                game.PackagePath = null;
                game.LastPackaged = null;

                if (game.LibraryPath == _outputPath)
                {
                    lock (_gamesLock)
                    {
                        _allGames.Remove(game);
                    }
                }

                _cacheService.UpdateCache(game);
                _cacheService.SaveCache();

                UpdateGamesList(_allGames);

                ToastService.Instance.ShowSuccess("Package Deleted", $"Deleted package for {game.Name}");
            }
            catch (Exception ex)
            {
                ToastService.Instance.ShowError("Delete Failed", ex.Message);
            }
        }
    }

    private void ContextMenu_OpenInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game != null)
        {
            try { Process.Start("explorer.exe", game.InstallDir); } catch (Exception ex) { LogService.Instance.Debug($"Could not open install folder: {ex.Message}", "MainWindow"); }
        }
    }

    private void ContextMenu_ViewDetails_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game != null)
        {
            var info = $"AppID: {game.AppId}\nSize: {game.FormattedSize}\nInstalled: {game.InstallDir}";
            ToastService.Instance.ShowInfo(game.Name, info);
        }
    }

    private async void ContextMenu_RepairFromPeer_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null) return;

        var peers = _lanDiscoveryService.GetPeers();
        if (peers.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Peers Found", "No peers available for repair.");
            return;
        }

        PeerInfo? selectedPeer;
        if (peers.Count == 1)
        {
            selectedPeer = peers.First();
        }
        else
        {
            var dialog = new PeerSelectionDialog(peers) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedPeer == null) return;
            selectedPeer = dialog.SelectedPeer;
        }

        var result = MessageBox.Show(
            $"Attempt to repair \"{game.Name}\" from {selectedPeer.HostName}?\n\n" +
            "This will verify your local files against the peer's copy and download only what is missing or corrupt.",
            "Repair Package",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            StatusText.Text = $"⏳ Requesting repair for {game.Name}...";
            await _transferService.RequestPullPackageAsync(selectedPeer.IpAddress, selectedPeer.TransferPort, game.Name);
            ToastService.Instance.ShowSuccess("Repair Requested", $"Asked {selectedPeer.HostName} to send clean files.");
        }
        catch (Exception ex)
        {
            ToastService.Instance.ShowError("Repair Failed", ex.Message);
        }
    }

    private async void ContextMenu_VerifyIntegrity_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null || !game.IsPackaged || string.IsNullOrEmpty(game.PackagePath)) return;

        LoadingOverlay.Show($"Verifying {game.Name} integrity...");
        StatusText.Text = $"🛡️ Verifying {game.Name}...";

        try
        {
            var progress = new Progress<int>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    LoadingOverlay.UpdateProgress("Verifying files...", p);
                    StatusText.Text = $"🛡️ Verifying {game.Name}: {p}%";
                });
            });

            var result = await _integrityService.VerifyPackageAsync(game.PackagePath, progress);

            LoadingOverlay.Hide();

            if (result.IsValid)
            {
                StatusText.Text = $"✓ Verification passed: {game.Name}";
                ToastService.Instance.ShowSuccess("Verification Passed", $"{game.Name} is valid and intact.");
            }
            else
            {
                StatusText.Text = $"⚠ Verification failed: {game.Name}";

                var message = $"{result.MissingFiles.Count} missing files, {result.MismatchedFiles.Count} modified files.\n\n" +
                              "Would you like to see the detailed report?";

                if (MessageBox.Show(message, "Verification Failed", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    var report = $"Verification Report for {game.Name}\n\n";
                    if (result.MissingFiles.Count > 0)
                    {
                        report += "MISSING FILES:\n" + string.Join("\n", result.MissingFiles.Take(10));
                        if (result.MissingFiles.Count > 10) report += $"\n...and {result.MissingFiles.Count - 10} more";
                        report += "\n\n";
                    }
                    if (result.MismatchedFiles.Count > 0)
                    {
                        report += "MODIFIED/CORRUPT FILES:\n" + string.Join("\n", result.MismatchedFiles.Take(10));
                        if (result.MismatchedFiles.Count > 10) report += $"\n...and {result.MismatchedFiles.Count - 10} more";
                    }

                    MessageBox.Show(report, "Detailed Report", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            LoadingOverlay.Hide();
            StatusText.Text = $"⚠ Verification error: {ex.Message}";
            ToastService.Instance.ShowError("Verification Error", ex.Message);
        }
    }

    private void ContextMenu_OpenSteamStore_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game != null)
        {
            var url = $"https://store.steampowered.com/app/{game.AppId}";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { LogService.Instance.Debug($"Could not open Steam store: {ex.Message}", "MainWindow"); }
        }
    }

    private async void ContextMenu_UpdatePackage_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null || !game.UpdateAvailable) return;

        var result = MessageBox.Show(
            $"Update package for {game.Name}?\n\n" +
            $"Current Package Build: {game.PackageBuildId}\n" +
            $"Latest Steam Build: {game.BuildId}\n\n" +
            "This will sync your package with the latest Steam files.\n" +
            "Only changed files will be copied.",
            "Update Package",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _currentOperationCts?.Cancel();
        _currentOperationCts = new CancellationTokenSource();

        LoadingOverlay.Show($"Updating {game.Name}...");
        StatusText.Text = $"⏳ Updating package for {game.Name}...";

        try
        {
            var updatedPath = await _packageBuilder.UpdatePackageAsync(
                game,
                _outputPath,
                ct: _currentOperationCts.Token);

            LoadingOverlay.Hide();
            StatusText.Text = $"✓ Package updated: {game.Name}";
            ToastService.Instance.ShowSuccess("Package Updated", $"Updated {game.Name} to Build {game.BuildId}");

            // Refresh package data
            game.PackageBuildId = game.BuildId;
            game.LastPackaged = DateTime.Now;
            _cacheService.UpdateCache(game);
            _cacheService.SaveCache();

            // Refresh UI
            ApplyFilters();
        }
        catch (OperationCanceledException)
        {
            LoadingOverlay.Hide();
            StatusText.Text = "Update cancelled";
        }
        catch (Exception ex)
        {
            LoadingOverlay.Hide();
            StatusText.Text = $"⚠ Update failed: {ex.Message}";
            ToastService.Instance.ShowError("Update Failed", ex.Message);
            LogService.Instance.Error($"Package update failed for {game.Name}", ex, "MainWindow");
        }
    }

    private void ContextMenu_AdvancedConfig_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null) return;

        _gameGoldbergConfigs.TryGetValue(game.AppId, out var existingConfig);

        var dialog = new GoldbergConfigDialog(existingConfig)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.Config != null)
        {
            _gameGoldbergConfigs[game.AppId] = dialog.Config;
            ToastService.Instance.ShowSuccess("Config Saved", $"Goldberg settings saved for {game.Name}");
        }
    }
}
