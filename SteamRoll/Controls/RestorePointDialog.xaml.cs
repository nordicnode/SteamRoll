using System.Windows;
using System.Windows.Input;

namespace SteamRoll.Controls;

/// <summary>
/// Dialog for creating a named restore point.
/// </summary>
public partial class RestorePointDialog : Window
{
    /// <summary>
    /// The name entered by the user.
    /// </summary>
    public string RestorePointName { get; private set; } = string.Empty;

    /// <summary>
    /// Whether the restore point should be pinned.
    /// </summary>
    public bool IsPinned { get; private set; }

    /// <summary>
    /// Whether the user confirmed the dialog.
    /// </summary>
    public bool Confirmed { get; private set; }

    public RestorePointDialog()
    {
        InitializeComponent();
        NameBox.Focus();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        CreateButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);
    }

    private void QuickName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is string name)
        {
            NameBox.Text = name;
            CreateButton.IsEnabled = true;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            return;

        RestorePointName = NameBox.Text.Trim();
        IsPinned = PinCheckBox.IsChecked ?? false;
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
