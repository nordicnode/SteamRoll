using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SteamRoll.Controls;

/// <summary>
/// Loading overlay control with animated spinner and progress display.
/// </summary>
public partial class LoadingOverlay : UserControl
{
    private Storyboard? _spinAnimation;
    private Storyboard? _pulseAnimation;
    private Storyboard? _fadeIn;
    private Storyboard? _fadeOut;

    public LoadingOverlay()
    {
        InitializeComponent();
        
        // Cache storyboard references
        _spinAnimation = (Storyboard)FindResource("SpinAnimation");
        _pulseAnimation = (Storyboard)FindResource("PulseAnimation");
        _fadeIn = (Storyboard)FindResource("FadeIn");
        _fadeOut = (Storyboard)FindResource("FadeOut");
    }

    /// <summary>
    /// Shows the loading overlay with the specified message.
    /// </summary>
    /// <param name="message">The status message to display.</param>
    public void Show(string message = "Loading...")
    {
        StatusText.Text = message;
        ProgressText.Text = "";
        ProgressBar.Width = 0;
        
        Visibility = Visibility.Visible;
        _fadeIn?.Begin(this);
        _spinAnimation?.Begin(this, true);
        _pulseAnimation?.Begin(this, true);
    }

    /// <summary>
    /// Updates the loading overlay with progress information.
    /// </summary>
    /// <param name="message">The current status message.</param>
    /// <param name="percentage">Progress percentage (0-100).</param>
    public void UpdateProgress(string message, int percentage)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
            ProgressText.Text = $"{percentage}%";
            
            // Animate progress bar width (320 is max width)
            var targetWidth = (percentage / 100.0) * 320;
            ProgressBar.Width = targetWidth;
        });
    }

    /// <summary>
    /// Hides the loading overlay with fade animation.
    /// </summary>
    public void Hide()
    {
        _spinAnimation?.Stop(this);
        _pulseAnimation?.Stop(this);
        
        if (_fadeOut != null)
        {
            var fadeOut = _fadeOut.Clone();
            fadeOut.Completed += (s, e) => 
            {
                Dispatcher.Invoke(() => Visibility = Visibility.Collapsed);
            };
            fadeOut.Begin(this);
        }
        else
        {
            Visibility = Visibility.Collapsed;
        }
    }
}
