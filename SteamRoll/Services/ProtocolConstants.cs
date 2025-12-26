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
    
    // ====================================
    // NAT Traversal Protocol Constants
    // ====================================
    
    /// <summary>
    /// Signaling message type: Request to initiate hole punch coordination.
    /// </summary>
    public const string HOLE_PUNCH_REQUEST = "HP_REQ";
    
    /// <summary>
    /// Signaling message type: Response with own public endpoint.
    /// </summary>
    public const string HOLE_PUNCH_RESPONSE = "HP_RSP";
    
    /// <summary>
    /// Signaling message type: Both peers ready, prepare to connect.
    /// </summary>
    public const string HOLE_PUNCH_READY = "HP_RDY";
    
    /// <summary>
    /// Signaling message type: Start simultaneous connect NOW.
    /// </summary>
    public const string HOLE_PUNCH_GO = "HP_GO";
    
    /// <summary>
    /// Signaling message type: Connection attempt failed.
    /// </summary>
    public const string HOLE_PUNCH_FAIL = "HP_FAIL";
    
    /// <summary>
    /// Signaling message type: Connection established successfully.
    /// </summary>
    public const string HOLE_PUNCH_SUCCESS = "HP_OK";
    
    /// <summary>
    /// Total timeout for hole punch attempt in milliseconds.
    /// </summary>
    public const int HOLE_PUNCH_TIMEOUT_MS = 10000;
    
    /// <summary>
    /// Delay in milliseconds after receiving GO signal before simultaneous connect.
    /// Both peers wait this exact amount to synchronize their ConnectAsync calls.
    /// </summary>
    public const int HOLE_PUNCH_SYNC_DELAY_MS = 100;
    
    /// <summary>
    /// Number of retry attempts for hole punch before giving up.
    /// </summary>
    public const int HOLE_PUNCH_RETRY_COUNT = 3;
    
    /// <summary>
    /// Timeout for individual TCP connect attempt during hole punch.
    /// </summary>
    public const int HOLE_PUNCH_CONNECT_TIMEOUT_MS = 3000;
    
    /// <summary>
    /// Port used for TCP hole punching. Uses a separate port from transfer
    /// to avoid conflicts with existing TCP listeners.
    /// </summary>
    public const int HOLE_PUNCH_PORT = 27053;
}
