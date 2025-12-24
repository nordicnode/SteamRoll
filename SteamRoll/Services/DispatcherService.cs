namespace SteamRoll.Services;

/// <summary>
/// Abstraction for UI thread dispatching.
/// Allows services to update UI without direct WPF coupling,
/// enabling unit testing without WPF runtime.
/// </summary>
public interface IDispatcherService
{
    /// <summary>
    /// Invokes an action on the UI thread synchronously.
    /// </summary>
    void Invoke(Action action);
    
    /// <summary>
    /// Invokes an action on the UI thread asynchronously.
    /// </summary>
    void BeginInvoke(Action action);
    
    /// <summary>
    /// Checks if we're already on the UI thread.
    /// </summary>
    bool CheckAccess();
}

/// <summary>
/// WPF implementation of IDispatcherService that uses Application.Current.Dispatcher.
/// </summary>
public class WpfDispatcherService : IDispatcherService
{
    public void Invoke(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            // No dispatcher available (headless/test mode), just run inline
            action();
            return;
        }
        
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
    
    public void BeginInvoke(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            action();
            return;
        }
        
        dispatcher.BeginInvoke(action);
    }
    
    public bool CheckAccess()
    {
        return System.Windows.Application.Current?.Dispatcher.CheckAccess() ?? true;
    }
}

/// <summary>
/// Test/mock implementation that runs actions inline.
/// </summary>
public class InlineDispatcherService : IDispatcherService
{
    public void Invoke(Action action) => action();
    public void BeginInvoke(Action action) => action();
    public bool CheckAccess() => true;
}
