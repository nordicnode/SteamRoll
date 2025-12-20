using System.Collections.Concurrent;
using System.IO;

namespace SteamRoll.Services;

/// <summary>
/// Simple logging service that writes to both Debug output and a file.
/// </summary>
public class LogService : IDisposable
{
    private static LogService? _instance;
    public static LogService Instance => _instance ??= new LogService();
    
    private readonly string _logPath;
    private readonly ConcurrentQueue<LogEntry> _pendingLogs = new();
    private readonly object _writeLock = new();
    private bool _disposed;
    
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    
    private LogService()
    {
        var logDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamRoll",
            "Logs"
        );
        Directory.CreateDirectory(logDir);
        
        _logPath = System.IO.Path.Combine(logDir, $"steamroll_{DateTime.Now:yyyy-MM-dd}.log");
    }
    
    public void Debug(string message, string? category = null)
        => Log(LogLevel.Debug, message, category);
    
    public void Info(string message, string? category = null)
        => Log(LogLevel.Info, message, category);
    
    public void Warning(string message, string? category = null)
        => Log(LogLevel.Warning, message, category);
    
    public void Error(string message, Exception? ex = null, string? category = null)
    {
        var fullMessage = ex != null ? $"{message}: {ex.Message}" : message;
        Log(LogLevel.Error, fullMessage, category);
        
        if (ex != null)
        {
            Log(LogLevel.Debug, $"Stack trace: {ex.StackTrace}", category);
        }
    }
    
    private void Log(LogLevel level, string message, string? category)
    {
        if (level < MinimumLevel) return;
        
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category ?? "General",
            Message = message
        };
        
        // Always write to Debug output
        System.Diagnostics.Debug.WriteLine($"[{entry.Level}] {entry.Category}: {entry.Message}");
        
        // Queue for file writing
        _pendingLogs.Enqueue(entry);
        
        // Write to file (batch if many logs)
        if (_pendingLogs.Count >= 10 || level >= LogLevel.Warning)
        {
            Flush();
        }
    }
    
    /// <summary>
    /// Writes all pending log entries to the log file.
    /// </summary>
    public void Flush()
    {
        if (_pendingLogs.IsEmpty) return;
        
        lock (_writeLock)
        {
            try
            {
                var entries = new List<string>();
                while (_pendingLogs.TryDequeue(out var entry))
                {
                    entries.Add($"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Level,-7}] [{entry.Category}] {entry.Message}");
                }
                
                if (entries.Count > 0)
                {
                    File.AppendAllLines(_logPath, entries);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write log: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Gets the path to the current log file.
    /// </summary>
    public string GetLogPath() => _logPath;
    
    /// <summary>
    /// Gets the log directory System.IO.Path.
    /// </summary>
    public string GetLogDirectory() => System.IO.Path.GetDirectoryName(_logPath) ?? "";
    
    /// <summary>
    /// Gets recent log entries from the current log file.
    /// </summary>
    public List<string> GetRecentLogs(int maxLines = 200)
    {
        Flush(); // Ensure all pending logs are written
        
        try
        {
            if (!File.Exists(_logPath))
                return new List<string> { "No log file found." };
            
            var lines = File.ReadAllLines(_logPath);
            return lines.TakeLast(maxLines).ToList();
        }
        catch (Exception ex)
        {
            return new List<string> { $"Error reading logs: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Cleans up old log files (older than specified days).
    /// </summary>
    public void CleanupOldLogs(int daysToKeep = 7)
    {
        try
        {
            var logDir = System.IO.Path.GetDirectoryName(_logPath);
            if (string.IsNullOrEmpty(logDir)) return;
            
            var cutoff = DateTime.Now.AddDays(-daysToKeep);
            var oldLogs = Directory.GetFiles(logDir, "steamroll_*.log")
                                   .Where(f => File.GetCreationTime(f) < cutoff);
            
            foreach (var oldLog in oldLogs)
            {
                File.Delete(oldLog);
                System.Diagnostics.Debug.WriteLine($"Deleted old log: {oldLog}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error cleaning old logs: {ex.Message}");
        }
    }

    
    public void Dispose()
    {
        if (!_disposed)
        {
            Flush();
            _disposed = true;
        }
    }
}

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

internal class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
}
