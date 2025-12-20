using System;

namespace SteamRoll.Models;

/// <summary>
/// Metadata stored in steamroll.json inside generated packages.
/// Used for version tracking, integrity checks, and update detection.
/// </summary>
public class PackageMetadata
{
    /// <summary>
    /// Steam AppID of the game.
    /// </summary>
    public int AppId { get; set; }

    /// <summary>
    /// Name of the game.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Steam Build ID of the source game at time of packaging.
    /// </summary>
    public int BuildId { get; set; }

    /// <summary>
    /// When the package was created.
    /// </summary>
    public DateTime CreatedDate { get; set; }

    /// <summary>
    /// The version of SteamRoll used to create this package.
    /// </summary>
    public string CreatorVersion { get; set; } = "1.0.0";

    /// <summary>
    /// The emulator mode used (Goldberg, CreamApi, etc).
    /// </summary>
    public string EmulatorMode { get; set; } = string.Empty;

    /// <summary>
    /// The version of the emulator used.
    /// </summary>
    public string? EmulatorVersion { get; set; }

    /// <summary>
    /// Original size of the game in bytes.
    /// </summary>
    public long OriginalSize { get; set; }

    /// <summary>
    /// SHA256 hashes of key files for integrity verification.
    /// Key: relative file path, Value: SHA256 hash (lowercase hex).
    /// </summary>
    public Dictionary<string, string> FileHashes { get; set; } = new();

    /// <summary>
    /// User-provided notes about this package.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// User-defined tags for organizing packages.
    /// </summary>
    public List<string> Tags { get; set; } = new();
}
