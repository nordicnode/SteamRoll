namespace SteamRoll.Services;

/// <summary>
/// Interface for UI dialog interactions, enabling testability of ViewModels.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows a confirmation dialog with Yes/No options.
    /// </summary>
    Task<bool> ShowConfirmationAsync(string title, string message);

    /// <summary>
    /// Shows an alert/informational dialog.
    /// </summary>
    void ShowAlert(string title, string message);

    /// <summary>
    /// Shows an error dialog.
    /// </summary>
    void ShowError(string title, string message);

    /// <summary>
    /// Shows a folder browser dialog.
    /// </summary>
    /// <returns>Selected folder path, or null if cancelled.</returns>
    string? SelectFolder(string? initialPath = null);

    /// <summary>
    /// Shows a file open dialog.
    /// </summary>
    /// <returns>Selected file path, or null if cancelled.</returns>
    string? SelectFile(string? filter = null, string? initialDirectory = null);

    /// <summary>
    /// Shows peer selection dialog.
    /// </summary>
    /// <returns>Selected peer, or null if cancelled.</returns>
    PeerInfo? SelectPeer(List<PeerInfo> peers);
}
