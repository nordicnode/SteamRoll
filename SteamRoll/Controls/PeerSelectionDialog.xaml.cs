using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SteamRoll.Services;

namespace SteamRoll.Controls;

/// <summary>
/// Dialog for selecting a peer from a list of discovered LAN peers.
/// </summary>
public partial class PeerSelectionDialog : Window
{
    /// <summary>
    /// Gets the selected peer, or null if dialog was cancelled.
    /// </summary>
    public PeerInfo? SelectedPeer { get; private set; }
    
    public PeerSelectionDialog(List<PeerInfo> peers)
    {
        InitializeComponent();
        
        PeerListBox.ItemsSource = peers;
        PeerListBox.SelectionChanged += (s, e) =>
        {
            SelectButton.IsEnabled = PeerListBox.SelectedItem != null;
        };
        
        // Select first item if available
        if (peers.Count > 0)
        {
            PeerListBox.SelectedIndex = 0;
        }
    }
    
    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedPeer = PeerListBox.SelectedItem as PeerInfo;
        DialogResult = true;
        Close();
    }
    
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
    
    private void PeerListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PeerListBox.SelectedItem != null)
        {
            SelectedPeer = PeerListBox.SelectedItem as PeerInfo;
            DialogResult = true;
            Close();
        }
    }

    private async void SpeedTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (PeerListBox.SelectedItem is not PeerInfo peer)
        {
            MessageBox.Show("Please select a peer first.", "Speed Test", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var btn = sender as Button;
        if (btn != null) btn.IsEnabled = false;

        try
        {
            // Create a temporary TransferService just for the test if not passed in,
            // or we assume MainWindow passes one.
            // To keep this dialog simple/standalone, we'll assume we can't easily reuse the main one without passing it.
            // But we can instantiate a temporary one easily.
            using var transferService = new TransferService(Path.GetTempPath());

            // We use a small buffer for quick test (50MB)
            var mbps = await transferService.RunSpeedTestAsync(peer.IpAddress, peer.TransferPort);

            MessageBox.Show($"Speed to {peer.HostName}: {mbps:F1} Mbps", "Speed Test Result", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Speed test failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }
}
