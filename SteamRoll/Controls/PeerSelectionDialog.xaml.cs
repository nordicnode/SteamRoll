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
}
