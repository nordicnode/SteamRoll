namespace SteamRoll.Models;

/// <summary>
/// Represents a game available from a peer on the network.
/// Used by the Mesh Library feature to aggregate games across the LAN.
/// </summary>
public class PeerGameInfo
{
    /// <summary>
    /// Steam App ID.
    /// </summary>
    public int AppId { get; set; }

    /// <summary>
    /// Display name of the game.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Size of the game package in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Hostname of the peer that has this game.
    /// </summary>
    public string PeerHostName { get; set; } = string.Empty;

    /// <summary>
    /// IP address of the peer.
    /// </summary>
    public string PeerIp { get; set; } = string.Empty;

    /// <summary>
    /// Transfer port of the peer.
    /// </summary>
    public int PeerPort { get; set; }

    /// <summary>
    /// Build ID of the package.
    /// </summary>
    public int BuildId { get; set; }

    /// <summary>
    /// Formatted size string for UI display.
    /// </summary>
    public string FormattedSize => Services.FormatUtils.FormatBytes(SizeBytes);

    /// <summary>
    /// Display string for the peer source.
    /// </summary>
    public string PeerDisplay => $"{PeerHostName} ({PeerIp})";
}

/// <summary>
/// Lightweight game info for network transmission.
/// Sent when a peer requests our game list.
/// </summary>
public class NetworkGameInfo
{
    public int AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int BuildId { get; set; }
}
