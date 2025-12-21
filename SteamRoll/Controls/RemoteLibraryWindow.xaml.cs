using System.Windows;
using System.Windows.Controls;
using SteamRoll.Models;
using SteamRoll.Services;

namespace SteamRoll.Controls;

public partial class RemoteLibraryWindow : Window
{
    private readonly TransferService _transferService;
    private readonly PeerInfo _peer;

    public RemoteGame? SelectedGame { get; private set; }

    public RemoteLibraryWindow(TransferService transferService, PeerInfo peer)
    {
        InitializeComponent();
        _transferService = transferService;
        _peer = peer;

        TitleText.Text = $"Library: {_peer.HostName}";
        SubtitleText.Text = "Fetching list of available games...";

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var games = await _transferService.RequestLibraryListAsync(_peer.IpAddress, _peer.TransferPort);

            if (games == null || games.Count == 0)
            {
                SubtitleText.Text = "No shared games found on this peer.";
            }
            else
            {
                SubtitleText.Text = $"Found {games.Count} available game(s).";
                GamesList.ItemsSource = games;
            }
        }
        catch (Exception ex)
        {
            SubtitleText.Text = "Failed to connect.";
            MessageBox.Show($"Error fetching library: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is RemoteGame game)
        {
            SelectedGame = game;
            DialogResult = true;
            Close();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
