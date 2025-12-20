using System.Diagnostics;
using System.Windows;
using SteamRoll.Services;

namespace SteamRoll;

/// <summary>
/// Log viewer window for viewing application logs.
/// </summary>
public partial class LogViewerWindow : Window
{
    public LogViewerWindow()
    {
        InitializeComponent();
        LoadLogs();
        
        // Show log path
        LogPathText.Text = $"Log file: {LogService.Instance.GetLogPath()}";
    }
    
    private void LoadLogs()
    {
        var logs = LogService.Instance.GetRecentLogs(500);
        LogTextBox.Text = string.Join(Environment.NewLine, logs);
        
        // Scroll to bottom
        LogScrollViewer.ScrollToEnd();
    }
    
    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadLogs();
        ToastService.Instance.ShowSuccess("Logs Refreshed", "Latest log entries loaded.");
    }
    
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var logDir = LogService.Instance.GetLogDirectory();
        if (!string.IsNullOrEmpty(logDir))
        {
            Process.Start("explorer.exe", logDir);
        }
    }
    
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
