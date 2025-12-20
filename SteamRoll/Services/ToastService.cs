using System.Windows;
using System.Windows.Controls;
using SteamRoll.Controls;

namespace SteamRoll.Services;

/// <summary>
/// Singleton service for managing toast notifications across the application.
/// </summary>
public class ToastService
{
    private static ToastService? _instance;
    private Panel? _container;
    private const int MaxToasts = AppConstants.MAX_TOASTS;


    /// <summary>
    /// Gets the singleton instance of the ToastService.
    /// </summary>
    public static ToastService Instance => _instance ??= new ToastService();

    private ToastService() { }

    /// <summary>
    /// Initializes the toast service with a container panel.
    /// Must be called before showing any toasts.
    /// </summary>
    /// <param name="container">The panel (typically StackPanel) where toasts will be added.</param>
    public void Initialize(Panel container)
    {
        _container = container;
    }

    /// <summary>
    /// Shows a toast notification.
    /// </summary>
    /// <param name="title">The toast title.</param>
    /// <param name="message">The toast message.</param>
    /// <param name="type">The toast type for styling.</param>
    /// <param name="durationMs">How long before auto-dismiss (0 = never).</param>
    public void Show(string title, string message, ToastType type = ToastType.Info, int durationMs = 4000)
    {
        if (_container == null) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            // Limit number of toasts
            while (_container.Children.Count >= MaxToasts)
            {
                if (_container.Children[0] is ToastNotification oldToast)
                {
                    oldToast.Close();
                }
                else
                {
                    _container.Children.RemoveAt(0);
                }
            }

            var toast = new ToastNotification();
            toast.Closed += (s, e) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _container.Children.Remove(toast);
                });
            };
            
            _container.Children.Add(toast);
            toast.Show(title, message, type, durationMs);
        });
    }

    /// <summary>
    /// Shows a success toast.
    /// </summary>
    public void ShowSuccess(string title, string message, int durationMs = 4000)
        => Show(title, message, ToastType.Success, durationMs);

    /// <summary>
    /// Shows an error toast.
    /// </summary>
    public void ShowError(string title, string message, int durationMs = 5000)
        => Show(title, message, ToastType.Error, durationMs);

    /// <summary>
    /// Shows a warning toast.
    /// </summary>
    public void ShowWarning(string title, string message, int durationMs = 4000)
        => Show(title, message, ToastType.Warning, durationMs);

    /// <summary>
    /// Shows an info toast.
    /// </summary>
    public void ShowInfo(string title, string message, int durationMs = 4000)
        => Show(title, message, ToastType.Info, durationMs);
}
