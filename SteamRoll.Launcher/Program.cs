using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

namespace SteamRoll.Launcher;

public class LauncherConfig
{
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string AppId { get; set; } = "";
    public bool WaitForExit { get; set; } = true;
}

static class Program
{
    private static string _logPath = "";

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        string markerPath = "";
        
        try
        {
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            _logPath = Path.Combine(currentDir, "LAUNCH.log");
            var configPath = Path.Combine(currentDir, "launcher.json");
            markerPath = Path.Combine(currentDir, ".steamroll_playing");

            Log("=== SteamRoll Launcher Started ===");
            Log($"Current directory: {currentDir}");
            Log($"Config path: {configPath}");
            Log($"Log path: {_logPath}");

            if (!File.Exists(configPath))
            {
                Log("ERROR: launcher.json not found!");
                ShowError($"launcher.json not found.\n\nLooked in: {currentDir}");
                return;
            }

            Log("Reading launcher.json...");
            var json = File.ReadAllText(configPath);
            Log($"Config contents: {json}");
            
            var config = JsonSerializer.Deserialize<LauncherConfig>(json);

            if (config == null || string.IsNullOrEmpty(config.Executable))
            {
                Log("ERROR: Invalid launcher configuration (null or empty executable)");
                ShowError($"Invalid launcher configuration.\n\nConfig contents:\n{json}");
                return;
            }

            Log($"Parsed config:");
            Log($"  Executable: {config.Executable}");
            Log($"  Arguments: {config.Arguments}");
            Log($"  WorkingDirectory: {config.WorkingDirectory}");
            Log($"  AppId: {config.AppId}");
            Log($"  WaitForExit: {config.WaitForExit}");

            // Resolve executable path
            var exePath = Path.GetFullPath(Path.Combine(currentDir, config.Executable));
            Log($"Resolved exe path: {exePath}");
            
            if (!File.Exists(exePath))
            {
                Log($"ERROR: Executable not found at {exePath}");
                ShowError($"Game executable not found:\n{exePath}\n\nConfig.Executable: {config.Executable}");
                return;
            }
            Log("Executable exists: YES");

            // Resolve working directory
            string workingDir;
            if (string.IsNullOrEmpty(config.WorkingDirectory))
            {
                workingDir = Path.GetDirectoryName(exePath) ?? currentDir;
                Log($"WorkingDirectory not set, using exe directory: {workingDir}");
            }
            else
            {
                workingDir = Path.GetFullPath(Path.Combine(currentDir, config.WorkingDirectory));
                Log($"Resolved working directory: {workingDir}");
            }

            if (!Directory.Exists(workingDir))
            {
                Log($"ERROR: Working directory not found: {workingDir}");
                ShowError($"Working directory not found:\n{workingDir}");
                return;
            }
            Log("Working directory exists: YES");

            // Create tracking marker
            if (!string.IsNullOrEmpty(config.AppId))
            {
                try
                {
                    File.WriteAllText(markerPath, $"{config.AppId}|{DateTime.Now:o}");
                    Log($"Created marker file: {markerPath}");
                }
                catch (Exception ex)
                {
                    Log($"Warning: Failed to create marker: {ex.Message}");
                }
            }

            try
            {
                Log("Creating ProcessStartInfo...");
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = config.Arguments ?? "",
                    WorkingDirectory = workingDir,
                    UseShellExecute = true
                };

                Log($"ProcessStartInfo:");
                Log($"  FileName: {startInfo.FileName}");
                Log($"  Arguments: {startInfo.Arguments}");
                Log($"  WorkingDirectory: {startInfo.WorkingDirectory}");
                Log($"  UseShellExecute: {startInfo.UseShellExecute}");

                Log("Starting process...");
                var process = Process.Start(startInfo);

                if (process == null)
                {
                    Log("ERROR: Process.Start returned null!");
                    ShowError($"Failed to start process.\n\nPath: {exePath}\nArgs: {config.Arguments}\nWorkDir: {workingDir}");
                    return;
                }

                Log($"Process started successfully! PID: {process.Id}");

                if (config.WaitForExit)
                {
                    Log("Waiting for process to exit...");
                    process.WaitForExit();
                    Log($"Process exited with code: {process.ExitCode}");
                }
                else
                {
                    Log("Not waiting for exit (WaitForExit=false)");
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR launching process: {ex.GetType().Name}: {ex.Message}");
                Log($"Stack trace: {ex.StackTrace}");
                ShowError($"Failed to launch:\n{ex.Message}\n\nPath: {exePath}\nArgs: {config.Arguments}\nWorkDir: {workingDir}");
            }
            finally
            {
                CleanupMarker(markerPath);
            }
        }
        catch (Exception ex)
        {
            Log($"FATAL ERROR: {ex.GetType().Name}: {ex.Message}");
            Log($"Stack trace: {ex.StackTrace}");
            ShowError($"Launcher error:\n{ex.Message}\n\nStack:\n{ex.StackTrace}");
            CleanupMarker(markerPath);
        }
        
        Log("=== Launcher finished ===");
    }

    static void Log(string message)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line = $"[{timestamp}] {message}";
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch
        {
            // Can't log, ignore
        }
    }

    static void ShowError(string message)
    {
        Log($"Showing error dialog: {message}");
        MessageBox.Show(message, "SteamRoll Launcher Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    static void CleanupMarker(string markerPath)
    {
        if (!string.IsNullOrEmpty(markerPath) && File.Exists(markerPath))
        {
            try 
            { 
                File.Delete(markerPath);
                Log($"Cleaned up marker: {markerPath}");
            }
            catch (Exception ex) 
            { 
                Log($"Warning: Failed to cleanup marker: {ex.Message}");
            }
        }
    }
}
