using System.IO;
using System.Text.RegularExpressions;

namespace SteamRoll.Services.Goldberg;

/// <summary>
/// Scans game files for Steam interface usage.
/// </summary>
public class GoldbergScanner
{
    // Precompiled regex patterns for Steam interface detection (performance optimization)
    private static readonly Regex[] InterfacePatterns = new[]
    {
        new Regex(@"SteamClient\d+", RegexOptions.Compiled),
        new Regex(@"SteamUser\d+", RegexOptions.Compiled),
        new Regex(@"SteamFriends\d+", RegexOptions.Compiled),
        new Regex(@"SteamUtils\d+", RegexOptions.Compiled),
        new Regex(@"SteamMatchMaking\d+", RegexOptions.Compiled),
        new Regex(@"SteamUserStats\d+", RegexOptions.Compiled),
        new Regex(@"SteamApps\d+", RegexOptions.Compiled),
        new Regex(@"SteamNetworking\d+", RegexOptions.Compiled),
        new Regex(@"SteamRemoteStorage\d+", RegexOptions.Compiled),
        new Regex(@"SteamScreenshots\d+", RegexOptions.Compiled),
        new Regex(@"SteamHTTP\d+", RegexOptions.Compiled),
        new Regex(@"SteamController\d+", RegexOptions.Compiled),
        new Regex(@"SteamUGC\d+", RegexOptions.Compiled),
        new Regex(@"SteamAppList\d+", RegexOptions.Compiled),
        new Regex(@"SteamMusic\d+", RegexOptions.Compiled),
        new Regex(@"SteamMusicRemote\d+", RegexOptions.Compiled),
        new Regex(@"SteamHTMLSurface\d+", RegexOptions.Compiled),
        new Regex(@"SteamInventory\d+", RegexOptions.Compiled),
        new Regex(@"SteamVideo\d+", RegexOptions.Compiled),
        new Regex(@"SteamParentalSettings\d+", RegexOptions.Compiled),
        new Regex(@"SteamInput\d+", RegexOptions.Compiled),
        new Regex(@"SteamParties\d+", RegexOptions.Compiled),
        new Regex(@"SteamRemotePlay\d+", RegexOptions.Compiled),
        new Regex(@"SteamNetworkingMessages\d+", RegexOptions.Compiled),
        new Regex(@"SteamNetworkingSockets\d+", RegexOptions.Compiled),
        new Regex(@"SteamNetworkingUtils\d+", RegexOptions.Compiled),
        new Regex(@"SteamGameServer\d+", RegexOptions.Compiled),
        new Regex(@"SteamGameServerStats\d+", RegexOptions.Compiled),
    };

    /// <summary>
    /// Detects Steam interfaces used in a specific file.
    /// </summary>
    public List<string> DetectInterfaces(string steamApiPath)
    {
        var interfaces = new List<string>();

        if (!File.Exists(steamApiPath))
            return interfaces;

        try
        {
            // Robust scanning: Stream read file in chunks to handle any size
            // and use overlapping buffers to catch patterns spanning chunk boundaries
            const int bufferSize = 64 * 1024; // 64KB chunks
            const int overlap = 1024; // Max interface name length safety margin

            var buffer = new byte[bufferSize];
            using var fs = new FileStream(steamApiPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            int bytesRead;
            int offset = 0;

            // Read until end
            while ((bytesRead = fs.Read(buffer, offset, buffer.Length - offset)) > 0)
            {
                var totalBytes = bytesRead + offset;
                var content = System.Text.Encoding.ASCII.GetString(buffer, 0, totalBytes);

                // Scan buffer
                foreach (var regex in InterfacePatterns)
                {
                    var matches = regex.Matches(content);
                    foreach (Match match in matches)
                    {
                        if (!interfaces.Contains(match.Value))
                            interfaces.Add(match.Value);
                    }
                }

                // Shift buffer to keep overlap for next iteration
                // If we reached end of file, stop
                if (fs.Position >= fs.Length) break;

                // Keep the last 'overlap' bytes at the start of buffer for next read
                var keepCount = Math.Min(totalBytes, overlap);
                Array.Copy(buffer, totalBytes - keepCount, buffer, 0, keepCount);
                offset = keepCount;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error detecting interfaces in {steamApiPath}: {ex.Message}", ex, "GoldbergScanner");
        }

        return interfaces.OrderBy(i => i).ToList();
    }
}
