using System.Windows;
using SteamRoll.Services;

namespace SteamRoll;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ServiceContainer? _services;
    
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
        
        // Initialize service container for dependency injection
        _services = new ServiceContainer();
        ServiceContainer.Initialize(_services);

        // Manually initialize and show the main window to ensure Resources are loaded first
        // This prevents "StaticResource" resolution errors in single-file builds
        var mainWindow = new MainWindow(_services);
        mainWindow.Show();
    }
    
    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose services on exit
        _services?.Dispose();
        base.OnExit(e);
    }
}
