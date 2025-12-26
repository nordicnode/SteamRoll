namespace SteamRoll.Services;

/// <summary>
/// Protocol constants for network communication between SteamRoll instances.
/// Centralizes magic strings to prevent version mismatch errors during refactoring.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>
    /// Magic string for LAN discovery protocol (UDP).
    /// </summary>
    public const string DISCOVERY_MAGIC = "STEAMROLL_V1";
    
    /// <summary>
    /// Magic string for file transfer protocol (TCP) version 1.
    /// </summary>
    public const string TRANSFER_MAGIC_V1 = "STEAMROLL_TRANSFER_V1";
    
    /// <summary>
    /// Magic string for file transfer protocol (TCP) version 2.
    /// </summary>
    public const string TRANSFER_MAGIC_V2 = "STEAMROLL_TRANSFER_V2";
    
    /// <summary>
    /// Magic string for encrypted file transfer protocol (TCP) version 3.
    /// Uses PSK encryption with AES-GCM.
    /// </summary>
    public const string TRANSFER_MAGIC_V3 = "STEAMROLL_TRANSFER_V3_ENC";
    
    // ====================================
    // Binary Protocol Constants
    // ====================================
    
    /// <summary>
    /// Magic bytes for delta sync block signature stream.
    /// </summary>
    public static readonly byte[] DELTA_SIGNATURE_MAGIC = "SRBS"u8.ToArray(); // SteamRoll Block Signature
    
    /// <summary>
    /// Magic bytes for delta instruction stream.
    /// </summary>
    public static readonly byte[] DELTA_INSTRUCTION_MAGIC = "SRDI"u8.ToArray(); // SteamRoll Delta Instructions
    
    /// <summary>
    /// Current version of the delta sync binary protocol.
    /// </summary>
    public const byte DELTA_PROTOCOL_VERSION = 1;
    
    /// <summary>
    /// Magic bytes for restore point metadata files.
    /// </summary>
    public static readonly byte[] RESTORE_POINT_MAGIC = "SRRP"u8.ToArray(); // SteamRoll Restore Point
    
    /// <summary>
    /// Current version of restore point metadata format.
    /// </summary>
    public const byte RESTORE_POINT_VERSION = 1;
    
    // ====================================
    // Delta Sync Block Sizes
    // ====================================
    
    /// <summary>
    /// Default block size for delta sync (16 KB).
    /// Smaller = better deduplication, larger = less overhead.
    /// </summary>
    public const int DELTA_BLOCK_SIZE = 16 * 1024;
    
    /// <summary>
    /// Maximum block size for delta sync (1 MB).
    /// </summary>
    public const int DELTA_BLOCK_SIZE_MAX = 1024 * 1024;
    
    /// <summary>
    /// Minimum file size that benefits from delta sync (1 MB).
    /// Smaller files transfer faster without delta overhead.
    /// </summary>
    public const int DELTA_MIN_FILE_SIZE = 1024 * 1024;
}
