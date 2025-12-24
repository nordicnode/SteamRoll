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
}
