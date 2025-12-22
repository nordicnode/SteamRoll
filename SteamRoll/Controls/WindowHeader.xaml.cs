using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SteamRoll.Controls;

public partial class WindowHeader : UserControl
{
    public static readonly DependencyProperty SearchTextProperty =
        DependencyProperty.Register("SearchText", typeof(string), typeof(WindowHeader),
            new FrameworkPropertyMetadata("🔍 Search games...", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public static readonly DependencyProperty HasPeersProperty =
        DependencyProperty.Register("HasPeers", typeof(bool), typeof(WindowHeader), new PropertyMetadata(false));

    public bool HasPeers
    {
        get => (bool)GetValue(HasPeersProperty);
        set => SetValue(HasPeersProperty, value);
    }

    // Events
    public event RoutedEventHandler LibraryClicked;
    public event RoutedEventHandler PackagesClicked;
    public event RoutedEventHandler RefreshClicked;
    public event RoutedEventHandler OpenOutputClicked;
    public event RoutedEventHandler ImportPackageClicked;
    public event RoutedEventHandler SettingsClicked;
    public event RoutedEventHandler BrowsePeerClicked;
    public event RoutedEventHandler ViewModeToggleClicked;
    public event RoutedEventHandler SelectModeToggleClicked;
    public event TextChangedEventHandler SearchTextChanged;

    public WindowHeader()
    {
        InitializeComponent();
    }

    private void LibraryTab_Click(object sender, RoutedEventArgs e)
    {
        LibraryClicked?.Invoke(this, e);
    }

    private void PackagesTab_Click(object sender, RoutedEventArgs e)
    {
        PackagesClicked?.Invoke(this, e);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshClicked?.Invoke(this, e);
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOutputClicked?.Invoke(this, e);
    }

    private void ImportPackageButton_Click(object sender, RoutedEventArgs e)
    {
        ImportPackageClicked?.Invoke(this, e);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsClicked?.Invoke(this, e);
    }

    private void BrowsePeerButton_Click(object sender, RoutedEventArgs e)
    {
        BrowsePeerClicked?.Invoke(this, e);
    }

    private void ViewModeToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModeToggleClicked?.Invoke(this, e);
    }

    private void SelectModeToggle_Click(object sender, RoutedEventArgs e)
    {
        SelectModeToggleClicked?.Invoke(this, e);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchTextChanged?.Invoke(this, e);
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text == "🔍 Search games...")
        {
            SearchBox.Text = "";
            SearchBox.Foreground = (Brush)FindResource("TextPrimaryBrush");
        }
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            SearchBox.Text = "🔍 Search games...";
            SearchBox.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }
    }

    // Public methods to control state

    public void SetLibraryTabActive(bool active)
    {
        LibraryTabBtn.Background = active
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#30363D"))
            : Brushes.Transparent;
        PackagesTabBtn.Background = active
            ? Brushes.Transparent
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#30363D"));
    }

    public void SetViewModeIcon(bool isList)
    {
        if (isList)
        {
            ViewModeIcon.Text = "⊞"; // Icon becomes Grid
            ViewModeToggle.ToolTip = "Switch to Grid View";
            ViewModeToggle.IsChecked = true;
        }
        else
        {
            ViewModeIcon.Text = "📑"; // Icon becomes List
            ViewModeToggle.ToolTip = "Switch to List View";
            ViewModeToggle.IsChecked = false;
        }
    }

    public bool IsViewModeList => ViewModeToggle.IsChecked == true;

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
