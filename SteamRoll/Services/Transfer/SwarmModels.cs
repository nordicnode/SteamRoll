namespace SteamRoll.Services.Transfer;

// ========================================
// Swarm Block Management
// ========================================

/// <summary>
/// Represents a single block/chunk of a file to be downloaded.
/// Used by SwarmCoordinator to track block state across multiple peers.
/// </summary>
public class BlockJob
{
    /// <summary>
    /// Byte offset within the file where this block starts.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Length of this block in bytes (usually 4MB, may be smaller for last block).
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// Zero-based index of this block within the file.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Whether this block has been successfully downloaded and verified.
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// ID of the peer currently downloading this block (null if unassigned).
    /// </summary>
    public Guid? AssignedPeerId { get; set; }

    /// <summary>
    /// When this block was assigned to a peer (for timeout/work-stealing detection).
    /// </summary>
    public DateTime? AssignmentTime { get; set; }

    /// <summary>
    /// Number of failed attempts for this block (for retry limiting).
    /// </summary>
    public int FailedAttempts { get; set; }
}

/// <summary>
/// Status of a block in the swarm download.
/// </summary>
public enum BlockStatus
{
    Pending,
    InFlight,
    Completed,
    Failed
}

// ========================================
// Protocol Messages for Swarm Communication
// ========================================

/// <summary>
/// Broadcast message asking peers if they have a specific file.
/// Used during swarm initialization to discover available sources.
/// </summary>
/// <param name="GameName">Name of the game package.</param>
/// <param name="FileHash">XxHash64 of the complete file for identification.</param>
public record QueryAvailabilityMessage(string GameName, string FileHash);

/// <summary>
/// Response to availability query - indicates if peer has the file.
/// </summary>
/// <param name="PeerId">Unique identifier of the responding peer.</param>
/// <param name="HasFile">Whether this peer has the complete file.</param>
/// <param name="FileSize">Size of the file in bytes (for verification).</param>
/// <param name="MaxUploadSpeedBytesPerSec">Peer's advertised max upload speed (0 = unknown).</param>
public record AvailabilityResponseMessage(
    Guid PeerId, 
    bool HasFile, 
    long FileSize, 
    long MaxUploadSpeedBytesPerSec = 0);

/// <summary>
/// Request a specific block/chunk from a peer.
/// </summary>
/// <param name="GameName">Name of the game package.</param>
/// <param name="FilePath">Relative path within the package.</param>
/// <param name="BlockIndex">Index of the requested block.</param>
/// <param name="Offset">Byte offset within the file.</param>
/// <param name="Length">Number of bytes to retrieve.</param>
public record RequestBlockMessage(
    string GameName, 
    string FilePath, 
    int BlockIndex, 
    long Offset, 
    int Length);

/// <summary>
/// Response containing block data or error.
/// </summary>
public class BlockDataMessage
{
    /// <summary>
    /// Index of the block being returned.
    /// </summary>
    public int BlockIndex { get; set; }

    /// <summary>
    /// The block data (null on failure).
    /// </summary>
    public byte[]? Data { get; set; }

    /// <summary>
    /// XxHash64 of the data for verification.
    /// </summary>
    public string? Hash { get; set; }

    /// <summary>
    /// Whether the block was successfully retrieved.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if Success is false.
    /// </summary>
    public string? Error { get; set; }
}

// ========================================
// Progress and Statistics
// ========================================

/// <summary>
/// Progress information for a swarm download.
/// </summary>
public class SwarmProgress
{
    public string GameName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long TotalBytes { get; set; }
    public long CompletedBytes { get; set; }
    public int TotalBlocks { get; set; }
    public int CompletedBlocks { get; set; }
    public int InFlightBlocks { get; set; }
    public int PendingBlocks => TotalBlocks - CompletedBlocks - InFlightBlocks;
    public double Percentage => TotalBytes > 0 ? (double)CompletedBytes / TotalBytes * 100 : 0;
    
    /// <summary>
    /// Statistics per peer showing their contribution.
    /// </summary>
    public Dictionary<Guid, PeerStats> PeerContributions { get; set; } = new();

    /// <summary>
    /// Combined download speed from all peers.
    /// </summary>
    public double CombinedSpeedBytesPerSec => PeerContributions.Values.Sum(p => p.SpeedBytesPerSec);
}

/// <summary>
/// Statistics for a single peer's contribution to the swarm.
/// </summary>
public class PeerStats
{
    public string IpAddress { get; set; } = "";
    public long BytesReceived { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public int BlocksCompleted { get; set; }
    public int BlocksFailed { get; set; }
    public bool IsConnected { get; set; }
}

/// <summary>
/// Result of a swarm download operation.
/// </summary>
public class SwarmResult
{
    public bool Success { get; set; }
    public string GameName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long TotalBytes { get; set; }
    public TimeSpan Duration { get; set; }
    public double AverageSpeedBytesPerSec => Duration.TotalSeconds > 0 ? TotalBytes / Duration.TotalSeconds : 0;
    public int PeersUsed { get; set; }
    public string? Error { get; set; }
    
    /// <summary>
    /// Final contribution from each peer.
    /// </summary>
    public Dictionary<Guid, PeerStats> PeerContributions { get; set; } = new();
}

/// <summary>
/// Information about a peer available for swarm download.
/// </summary>
public class SwarmPeerInfo
{
    public Guid PeerId { get; set; }
    public string IpAddress { get; set; } = "";
    public int Port { get; set; }
    public string? DeviceName { get; set; }
    public long AdvertisedSpeedBytesPerSec { get; set; }
    public DateTime DiscoveredAt { get; set; }
    public bool IsAvailable { get; set; } = true;
}
