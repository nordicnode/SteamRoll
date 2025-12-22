using System.IO;
using System.Windows;
using SteamRoll.Controls;
using SteamRoll.Models;
using SteamRoll.Services;
using SteamRoll.Services.Transfer;

namespace SteamRoll;

public partial class MainWindow
{
    private void UpdateNetworkStatus()
    {
        var peerCount = _lanDiscoveryService.GetPeers().Count;
        Dispatcher.Invoke(() =>
        {
            StatsBarControl.UpdateNetworkStatus(peerCount);
            HeaderControl.HasPeers = peerCount > 0;
        });
    }

    private void OnPeerDiscovered(object? sender, PeerInfo peer)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateNetworkStatus();
            StatusText.Text = $"🔗 Found peer: {peer.HostName}";
            ToastService.Instance.ShowInfo("Peer Found", $"Connected to {peer.HostName}");
        });
    }

    private void OnPeerLost(object? sender, PeerInfo peer)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateNetworkStatus();
        });
    }

    private void OnTransferProgress(object? sender, TransferProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            var direction = progress.IsSending ? "📤 Sending" : "📥 Receiving";
            StatusText.Text = $"{direction} {progress.GameName}: {progress.FormattedProgress}";
        });
    }

    private void OnTransferComplete(object? sender, TransferResult result)
    {
        Dispatcher.Invoke(async () =>
        {
            if (result.Success)
            {
                var action = result.WasReceived ? "received" : "sent";

                if (result.WasReceived)
                {
                    if (result.IsSaveSync)
                    {
                        try
                        {
                            var game = _allGames.FirstOrDefault(g => g.Name.Equals(result.GameName, StringComparison.OrdinalIgnoreCase));

                            if (game != null)
                            {
                                var confirm = MessageBox.Show(
                                    $"Received updated saves for {game.Name}. Overwrite local saves?",
                                    "Save Sync Received",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Question);

                                if (confirm == MessageBoxResult.Yes)
                                {
                                    await _saveGameService.RestoreSavesAsync(result.Path, game.AppId, game.PackagePath);
                                    StatusText.Text = $"✓ Synced saves for {result.GameName}";
                                    ToastService.Instance.ShowSuccess("Save Sync", "Local saves updated successfully.");
                                }
                                else
                                {
                                    StatusText.Text = $"⚠ Save sync skipped for {result.GameName}";
                                }
                            }
                            else
                            {
                                ToastService.Instance.ShowWarning("Save Sync", $"Received saves for unknown game: {result.GameName}");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.Instance.Error($"Failed to restore saves: {ex.Message}", ex, "SaveSync");
                            ToastService.Instance.ShowError("Save Restore Failed", ex.Message);
                        }
                        finally
                        {
                            try { File.Delete(result.Path); } catch (Exception ex) { LogService.Instance.Debug($"Could not delete temp file: {ex.Message}", "MainWindow"); }
                        }
                        return;
                    }

                    if (result.VerificationPassed)
                    {
                        StatusText.Text = $"✓ Successfully {action}: {result.GameName} (Verified ✓)";
                        ToastService.Instance.ShowSuccess("Transfer Complete",
                            $"Successfully {action} {result.GameName}\n✓ Package integrity verified");
                    }
                    else
                    {
                        var errorSummary = result.VerificationErrors.Count > 0
                            ? string.Join(", ", result.VerificationErrors.Take(3))
                            : "Unknown verification error";
                        StatusText.Text = $"⚠ {action}: {result.GameName} (Verification failed)";
                        ToastService.Instance.ShowWarning("Transfer Complete - Verification Failed",
                            $"{result.GameName} was received but verification failed:\n{errorSummary}");
                    }

                    _scanCts?.Cancel();
                    _scanCts = new CancellationTokenSource();
                    await ScanPackagesAsync(_scanCts.Token);
                }
                else
                {
                    StatusText.Text = $"✓ Successfully {action}: {result.GameName}";
                    ToastService.Instance.ShowSuccess("Transfer Complete", $"Successfully {action} {result.GameName}");
                }
            }
            else
            {
                StatusText.Text = $"⚠ Transfer failed: {result.GameName}";
                ToastService.Instance.ShowError("Transfer Failed", $"Failed to transfer {result.GameName}");
            }
        });
    }

    private void OnTransferApprovalRequested(object? sender, TransferApprovalEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var message = $"📥 Incoming game transfer request:\n\n" +
                          $"Game: {e.GameName}\n" +
                          $"Size: {e.FormattedSize}\n" +
                          $"Files: {e.FileCount}\n\n" +
                          "Do you want to accept this transfer?";

            var result = MessageBox.Show(
                message,
                "Incoming Transfer Request",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No
            );

            var approved = (result == MessageBoxResult.Yes);
            e.SetApproval(approved);

            if (approved)
            {
                StatusText.Text = $"✓ Accepted transfer of {e.GameName}";
                ToastService.Instance.ShowInfo("Transfer Accepted", $"Receiving {e.GameName}...");
            }
            else
            {
                StatusText.Text = $"✗ Rejected transfer of {e.GameName}";
                ToastService.Instance.ShowWarning("Transfer Rejected", $"Declined transfer of {e.GameName}");
            }
        });
    }

    private void OnTransferRequested(object? sender, TransferRequest request)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"📥 Transfer request from {request.FromHostName}: {request.GameName}";
        });
    }

    private List<RemoteGame> OnLocalLibraryRequested()
    {
        return GetGamesSnapshot().Where(g => g.IsPackaged && !string.IsNullOrEmpty(g.PackagePath))
            .Select(g => new RemoteGame { Name = g.Name, SizeBytes = g.SizeOnDisk })
            .ToList();
    }

    private async Task OnPullPackageRequested(string gameName, string targetIp, int targetPort)
    {
        var game = FindGame(g => g.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase) && g.IsPackaged);

        if (game != null && !string.IsNullOrEmpty(game.PackagePath))
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"📤 Sending requested package {game.Name}...";
                ToastService.Instance.ShowInfo("Transfer Started", $"Sending {game.Name} (Requested by peer)");
            });

            await _transferService.SendPackageAsync(targetIp, targetPort, game.PackagePath);
        }
        else
        {
            LogService.Instance.Warning($"Peer requested unknown/unpackaged game: {gameName}", "MainWindow");
        }
    }
}
