using System;
using SteamRoll.Services;

namespace SteamRoll.Models;

/// <summary>
/// Represents a game available from a remote peer.
/// Used by TCP TransferService for reliable library list exchange.
/// </summary>
public class RemoteGame
{
    /// <summary>
    /// Steam App ID.
    /// </summary>
    public int AppId { get; set; }

    /// <summary>
    /// Display name of the game.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Size of the game package in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Build ID of the package.
    /// </summary>
    public int BuildId { get; set; }

    /// <summary>
    /// Formatted size string for UI display.
    /// </summary>
    public string SizeDisplay => FormatUtils.FormatBytes(SizeBytes);
}
