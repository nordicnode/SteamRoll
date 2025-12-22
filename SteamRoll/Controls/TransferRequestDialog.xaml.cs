using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SteamRoll.Controls;

/// <summary>
/// A styled dialog for incoming transfer requests.
/// </summary>
public partial class TransferRequestDialog : Window
{
    /// <summary>
    /// Whether the transfer was accepted.
    /// </summary>
    public bool Accepted { get; private set; }

    /// <summary>
    /// Game name for the transfer.
    /// </summary>
    public string GameName { get; set; } = "";

    /// <summary>
    /// Formatted size string.
    /// </summary>
    public string FormattedSize { get; set; } = "";

    /// <summary>
    /// Number of files in the transfer.
    /// </summary>
    public int FileCount { get; set; }

    /// <summary>
    /// Optional peer name.
    /// </summary>
    public string? PeerName { get; set; }

    /// <summary>
    /// Optional app ID for loading game image.
    /// </summary>
    public int AppId { get; set; }

    public TransferRequestDialog()
    {
        InitializeComponent();
        MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    /// <summary>
    /// Creates a new transfer request dialog with the specified parameters.
    /// </summary>
    public TransferRequestDialog(string gameName, string formattedSize, int fileCount, string? peerName = null, int appId = 0)
        : this()
    {
        GameName = gameName;
        FormattedSize = formattedSize;
        FileCount = fileCount;
        PeerName = peerName;
        AppId = appId;

        LoadContent();
    }

    private void LoadContent()
    {
        GameNameText.Text = GameName;
        SizeText.Text = $"Size: {FormattedSize}";
        FilesText.Text = $"Files: {FileCount:N0}";
        PeerText.Text = !string.IsNullOrEmpty(PeerName) ? $"From: {PeerName}" : "";
        PeerText.Visibility = string.IsNullOrEmpty(PeerName) ? Visibility.Collapsed : Visibility.Visible;

        // Load game image from Steam CDN
        if (AppId > 0)
        {
            try
            {
                var imageUrl = $"https://steamcdn-a.akamaihd.net/steam/apps/{AppId}/capsule_184x69.jpg";
                GameImageBrush.ImageSource = new BitmapImage(new Uri(imageUrl));
            }
            catch
            {
                // Fallback - no image
            }
        }
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        DialogResult = true;
        Close();
    }

    private void DeclineButton_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        DialogResult = false;
        Close();
    }
}
