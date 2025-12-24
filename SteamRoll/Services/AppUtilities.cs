using System.IO;

namespace SteamRoll.Services;

/// <summary>
/// Shared utility methods and constants used across the application.
/// </summary>
public static class AppConstants
{
    // ============================================
    // Network Constants
    // ============================================
    
    /// <summary>
    /// Default port for LAN file transfers between SteamRoll instances.
    /// </summary>
    public const int DEFAULT_TRANSFER_PORT = 27051;
    
    /// <summary>
    /// Default port for LAN peer discovery.
    /// </summary>
    public const int DEFAULT_DISCOVERY_PORT = 27050;
    
    /// <summary>
    /// Buffer size for file transfers (80KB).
    /// </summary>
    public const int TRANSFER_BUFFER_SIZE = 81920;
    
    // ============================================
    // Cache Constants
    // ============================================
    
    /// <summary>
    /// Number of days before cached game data expires.
    /// </summary>
    public const int CACHE_EXPIRY_DAYS = 7;
    
    // ============================================
    // DRM Detection Constants
    // ============================================
    
    /// <summary>
    /// File size threshold (100MB) that may indicate Denuvo protection.
    /// </summary>
    public const long DENUVO_SIZE_THRESHOLD = 100_000_000;
    
    /// <summary>
    /// Overlay size threshold (10MB) that may indicate packed/protected executable.
    /// </summary>
    public const long PACKED_OVERLAY_THRESHOLD = 10_000_000;
    
    // ============================================
    // API Constants
    // ============================================
    
    /// <summary>
    /// HTTP timeout for Steam API requests.
    /// </summary>
    public const int HTTP_TIMEOUT_SECONDS = 15;
    
    /// <summary>
    /// HTTP timeout for large file downloads.
    /// </summary>
    public const int HTTP_DOWNLOAD_TIMEOUT_MINUTES = 5;
    
    // ============================================
    // LAN Discovery Constants
    // ============================================
    
    /// <summary>
    /// Base interval between peer announcements in milliseconds.
    /// Actual interval will be randomized with jitter to prevent broadcast storms.
    /// </summary>
    public const int ANNOUNCE_INTERVAL_MS = 5000;
    
    /// <summary>
    /// Maximum jitter to add to the announce interval in milliseconds.
    /// Prevents multiple clients from broadcasting simultaneously.
    /// Actual delay will be: ANNOUNCE_INTERVAL_MS + random(0, ANNOUNCE_JITTER_MS)
    /// </summary>
    public const int ANNOUNCE_JITTER_MS = 2000;
    
    /// <summary>
    /// Maximum random delay at startup before first announcement.
    /// Prevents burst of announcements when multiple clients start together.
    /// </summary>
    public const int ANNOUNCE_STARTUP_JITTER_MS = 3000;
    
    /// <summary>
    /// Time before a peer is considered lost in milliseconds.
    /// Must be greater than 2x (ANNOUNCE_INTERVAL_MS + ANNOUNCE_JITTER_MS) to handle 
    /// 2 consecutive dropped UDP packets with max jitter applied.
    /// Math: 2 × (5000 + 2000) = 14000ms, so 20000ms gives comfortable margin.
    /// </summary>
    public const int PEER_TIMEOUT_MS = 20000;
    
    // ============================================
    // UI Constants
    // ============================================
    
    /// <summary>
    /// Maximum number of toast notifications to display at once.
    /// </summary>
    public const int MAX_TOASTS = 5;
    
    /// <summary>
    /// Default duration for toast notifications in milliseconds.
    /// </summary>
    public const int TOAST_DURATION_MS = 4000;
    
    // ============================================
    // DLC Fetch Constants
    // ============================================
    
    /// <summary>
    /// Delay between DLC fetch requests to avoid rate limiting in milliseconds.
    /// </summary>
    public const int DLC_FETCH_DELAY_MS = 100;
    
    // ============================================
    // Cache Limits
    // ============================================
    
    /// <summary>
    /// Maximum number of entries in the in-memory Steam Store cache.
    /// </summary>
    public const int MAX_STORE_CACHE_ENTRIES = 500;

    // ============================================
    // External Resources
    // ============================================

    /// <summary>
    /// Default URL for Goldberg Emulator GitLab releases API.
    /// </summary>
    public const string DEFAULT_GOLDBERG_GITLAB_URL = "https://gitlab.com/api/v4/projects/Mr_Goldberg%2Fgoldberg_emulator/releases";

    /// <summary>
    /// Default URL for Goldberg Emulator GitHub fork releases API (gbe_fork).
    /// </summary>
    public const string DEFAULT_GOLDBERG_GITHUB_URL = "https://api.github.com/repos/Detanup01/gbe_fork/releases/latest";

    /// <summary>
    /// Default URL for CreamAPI GitHub releases API.
    /// </summary>
    public const string DEFAULT_CREAMAPI_GITHUB_URL = "https://api.github.com/repos/deadmau5v/CreamAPI/releases/latest";

    /// <summary>
    /// Default fallback URL for CreamAPI download.
    /// </summary>
    public const string DEFAULT_CREAMAPI_FALLBACK_URL = "https://github.com/deadmau5v/CreamAPI/releases/download/2024.12.08/CreamAPI.zip";
}


/// <summary>
/// Shared utility methods for formatting and common operations.
/// </summary>
public static class FormatUtils
{
    private static readonly string[] SizeSuffixes = { "B", "KB", "MB", "GB", "TB" };
    
    /// <summary>
    /// Formats a byte count into a human-readable string (e.g., "15.2 GB").
    /// </summary>
    /// <param name="bytes">The byte count to format.</param>
    /// <returns>A formatted string with appropriate size suffix.</returns>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        
        int order = 0;
        double len = bytes;
        
        while (len >= 1024 && order < SizeSuffixes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        
        return $"{len:0.##} {SizeSuffixes[order]}";
    }
    
    /// <summary>
    /// Sanitizes a string to be used as a valid file name.
    /// </summary>
    /// <param name="name">The name to sanitize.</param>
    /// <returns>A sanitized string safe for use as a file name.</returns>
    public static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();

        // Prevent directory traversal names if the name consists solely of dots
        if (sanitized == "." || sanitized == "..")
        {
            return "Unknown_Game";
        }

        return sanitized;
    }
}

/// <summary>
/// Network-related utility methods.
/// </summary>
public static class NetworkUtils
{
    /// <summary>
    /// Gets the local IP address of the primary network interface.
    /// Falls back to 0.0.0.0 if no suitable address is found.
    /// </summary>
    public static string GetLocalIpAddress()
    {
        try
        {
            // Get the first non-loopback IPv4 address
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
        }
        catch
        {
            // Fall through to default
        }
        
        return "0.0.0.0";
    }
}

