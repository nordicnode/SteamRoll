using System.IO;

namespace SteamRoll.Utils;

/// <summary>
/// Utility methods for disk operations.
/// </summary>
public static class DiskUtils
{
    /// <summary>
    /// Checks available free space for a path, handling UNC paths and other edge cases.
    /// </summary>
    /// <param name="path">File or directory path to check.</param>
    /// <returns>Tuple of (success, free bytes, error message if failed).</returns>
    public static (bool Success, long FreeBytes, string? Error) CheckFreeSpace(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return (false, 0, "Path is null or empty");
            
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                return (false, 0, "Could not determine path root");
            
            // Handle UNC paths (network shares) - we can't reliably check space
            if (root.StartsWith(@"\\"))
                return (true, long.MaxValue, null);
            
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return (false, 0, $"Drive {root} is not ready");
            
            return (true, drive.AvailableFreeSpace, null);
        }
        catch (IOException ex)
        {
            return (false, 0, $"IO error checking disk space: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, 0, $"Access denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Checks if there is enough space for a given size.
    /// </summary>
    public static (bool HasSpace, string? Error) HasSufficientSpace(string path, long requiredBytes)
    {
        var (success, freeBytes, error) = CheckFreeSpace(path);
        
        if (!success)
            return (false, error);
        
        if (freeBytes < requiredBytes)
            return (false, $"Insufficient disk space. Required: {FormatBytes(requiredBytes)}, Available: {FormatBytes(freeBytes)}");
        
        return (true, null);
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
