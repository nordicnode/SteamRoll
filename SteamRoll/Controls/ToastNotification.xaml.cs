using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SteamRoll.Controls;

/// <summary>
/// Toast notification types for visual styling.
/// </summary>
public enum ToastType
{
    Success,
    Error,
    Warning,
    Info
}

/// <summary>
/// Toast notification control with slide animations and auto-dismiss.
/// </summary>
public partial class ToastNotification : UserControl
{
    private Storyboard? _slideIn;
    private Storyboard? _slideOut;
    private DispatcherTimer? _autoDismissTimer;
    
    /// <summary>
    /// Fired when the toast is closed (either by user or auto-dismiss).
    /// </summary>
    public event EventHandler? Closed;

    public ToastNotification()
    {
        InitializeComponent();
        
        _slideIn = (Storyboard)FindResource("SlideIn");
        _slideOut = (Storyboard)FindResource("SlideOut");
    }

    /// <summary>
    /// Configures and shows the toast notification.
    /// </summary>
    public void Show(string title, string message, ToastType type = ToastType.Info, int durationMs = 4000)
    {
        TitleText.Text = title;
        MessageText.Text = message;
        
        ApplyTypeStyles(type);
        
        // Start slide-in animation
        _slideIn?.Begin(this);
        
        // Set up auto-dismiss timer
        if (durationMs > 0)
        {
            _autoDismissTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(durationMs)
            };
            _autoDismissTimer.Tick += (s, e) =>
            {
                _autoDismissTimer.Stop();
                Close();
            };
            _autoDismissTimer.Start();
        }
    }

    /// <summary>
    /// Closes the toast with animation.
    /// </summary>
    public void Close()
    {
        _autoDismissTimer?.Stop();
        
        if (_slideOut != null)
        {
            var slideOut = _slideOut.Clone();
            slideOut.Completed += (s, e) =>
            {
                Dispatcher.Invoke(() => Closed?.Invoke(this, EventArgs.Empty));
            };
            slideOut.Begin(this);
        }
        else
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyTypeStyles(ToastType type)
    {
        var (icon, bgColor, borderColor, iconBg) = type switch
        {
            ToastType.Success => ("✓", "#1A3F2E", "#3FB950", "#238636"),
            ToastType.Error => ("✕", "#3A1F2E", "#F85149", "#DA3633"),
            ToastType.Warning => ("⚠", "#3A2F1A", "#D29922", "#9E6A03"),
            ToastType.Info => ("ℹ", "#1A2A3F", "#58A6FF", "#1F6FEB"),
            _ => ("ℹ", "#1C2128", "#30363D", "#21262D")
        };

        IconText.Text = icon;
        ToastBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgColor));
        ToastBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderColor));
        IconBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(iconBg));
    }
}
