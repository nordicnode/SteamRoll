using System.Windows;
using SteamRoll.Services;

namespace SteamRoll;

/// <summary>
/// Dialog for configuring advanced Goldberg Emulator settings.
/// </summary>
public partial class GoldbergConfigDialog : Window
{
    /// <summary>
    /// Gets the configured Goldberg settings, or null if cancelled.
    /// </summary>
    public GoldbergConfig? Config { get; private set; }

    public GoldbergConfigDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Creates a dialog pre-populated with existing config.
    /// </summary>
    public GoldbergConfigDialog(GoldbergConfig? existingConfig) : this()
    {
        if (existingConfig != null)
        {
            AccountNameBox.Text = existingConfig.AccountName;
            DisableNetworkingCheck.IsChecked = existingConfig.DisableNetworking;
            DisableOverlayCheck.IsChecked = existingConfig.DisableOverlay;
            EnableLanCheck.IsChecked = existingConfig.EnableLan;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Config = new GoldbergConfig
        {
            AccountName = string.IsNullOrWhiteSpace(AccountNameBox.Text) ? "Player" : AccountNameBox.Text.Trim(),
            DisableNetworking = DisableNetworkingCheck.IsChecked ?? true,
            DisableOverlay = DisableOverlayCheck.IsChecked ?? true,
            EnableLan = EnableLanCheck.IsChecked ?? false
        };
        
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
