using System.Configuration;
using System.Data;
using System.Windows;

namespace SteamRoll;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Hook global exception handlers
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            MessageBox.Show($"Fatal error: {ex?.Message}\n\n{ex?.StackTrace}", "SteamRoll Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"UI error: {args.Exception.Message}\n\n{args.Exception.StackTrace}", "SteamRoll Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        
        base.OnStartup(e);

        // Manually initialize and show the main window to ensure Resources are loaded first
        // This prevents "StaticResource" resolution errors in single-file builds
        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}
