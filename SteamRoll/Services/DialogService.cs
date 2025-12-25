using System.Windows;
using Microsoft.Win32;
using SteamRoll.Controls;

namespace SteamRoll.Services;

/// <summary>
/// WPF implementation of IDialogService using MessageBox and standard dialogs.
/// </summary>
public class DialogService : IDialogService
{
    private readonly Window? _ownerWindow;

    public DialogService(Window? ownerWindow = null)
    {
        _ownerWindow = ownerWindow;
    }

    /// <inheritdoc />
    public Task<bool> ShowConfirmationAsync(string title, string message)
    {
        var result = MessageBox.Show(
            _ownerWindow,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    /// <inheritdoc />
    public void ShowAlert(string title, string message)
    {
        MessageBox.Show(
            _ownerWindow,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <inheritdoc />
    public void ShowError(string title, string message)
    {
        MessageBox.Show(
            _ownerWindow,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    /// <inheritdoc />
    public string? SelectFolder(string? initialPath = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Folder"
        };

        if (!string.IsNullOrEmpty(initialPath))
        {
            dialog.InitialDirectory = initialPath;
        }

        var result = _ownerWindow != null 
            ? dialog.ShowDialog(_ownerWindow) 
            : dialog.ShowDialog();

        return result == true ? dialog.FolderName : null;
    }

    /// <inheritdoc />
    public string? SelectFile(string? filter = null, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog();

        if (!string.IsNullOrEmpty(filter))
        {
            dialog.Filter = filter;
        }

        if (!string.IsNullOrEmpty(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        var result = _ownerWindow != null 
            ? dialog.ShowDialog(_ownerWindow) 
            : dialog.ShowDialog();

        return result == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public PeerInfo? SelectPeer(List<PeerInfo> peers)
    {
        if (peers.Count == 0)
        {
            return null;
        }

        if (peers.Count == 1)
        {
            return peers[0];
        }

        var dialog = new PeerSelectionDialog(peers);
        if (_ownerWindow != null)
        {
            dialog.Owner = _ownerWindow;
        }

        var result = dialog.ShowDialog();
        return result == true ? dialog.SelectedPeer : null;
    }
}
