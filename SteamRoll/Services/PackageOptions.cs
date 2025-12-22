using System.IO.Compression;

namespace SteamRoll.Services;

/// <summary>
/// Options for package creation.
/// </summary>
public class PackageOptions
{
    /// <summary>
    /// Whether to include DLC content.
    /// </summary>
    public bool IncludeDlc { get; set; } = true;

    /// <summary>
    /// Whether to compress the package.
    /// </summary>
    public bool Compress { get; set; } = false;
    
    /// <summary>
    /// Compression level for the zip archive.
    /// </summary>
    public CompressionLevel ZipCompressionLevel { get; set; } = CompressionLevel.Optimal;

    /// <summary>
    /// The emulation mode to use for the package.
    /// </summary>
    public PackageMode Mode { get; set; } = PackageMode.Goldberg;

    /// <summary>
    /// User-provided notes about this package.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Custom arguments to pass to the game executable in the launcher.
    /// </summary>
    public string? LauncherArguments { get; set; }

    /// <summary>
    /// List of file paths (relative to game root) to exclude from the package.
    /// Supports basic wildcards (*).
    /// </summary>
    public List<string> ExcludedPaths { get; set; } = new();

    /// <summary>
    /// Tags for organizing packages.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Whether to perform a differential update instead of a full repackage.
    /// </summary>
    public bool IsUpdate { get; set; } = false;

    /// <summary>
    /// Advanced Goldberg configuration (null = use defaults).
    /// </summary>
    public GoldbergConfig? GoldbergConfig { get; set; }
}

/// <summary>
/// Advanced configuration options for Goldberg Emulator.
/// </summary>
public class GoldbergConfig
{
    /// <summary>
    /// The account name shown in-game.
    /// </summary>
    public string AccountName { get; set; } = "Player";

    /// <summary>
    /// Whether to disable all network functionality.
    /// </summary>
    public bool DisableNetworking { get; set; } = true;

    /// <summary>
    /// Whether to disable the Steam overlay.
    /// </summary>
    public bool DisableOverlay { get; set; } = true;

    /// <summary>
    /// Whether to enable LAN multiplayer functionality.
    /// </summary>
    public bool EnableLan { get; set; } = false;
}

/// <summary>
/// The Steam emulation mode to apply to the package.
/// </summary>
public enum PackageMode
{
    /// <summary>
    /// Goldberg Emulator - Full Steam replacement, works offline.
    /// Best for most games.
    /// </summary>
    Goldberg,
    
    /// <summary>
    /// CreamAPI - Steam proxy that unlocks DLC while maintaining some Steam features.
    /// Use when Goldberg doesn't work or you need Steam integration.
    /// </summary>
    CreamApi
}
