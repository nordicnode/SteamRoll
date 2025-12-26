using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamRoll.Services;

/// <summary>
/// Represents a synchronized save file entry.
/// </summary>
public class SyncedSave
{
    /// <summary>
    /// Steam App ID of the game.
    /// </summary>
    public int AppId { get; set; }

    /// <summary>
    /// Name of the game for display purposes.
    /// </summary>
    public string GameName { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the save data for change detection.
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when this save was created/modified.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Size of the save data in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Hostname of the peer that created this save (empty for local saves).
    /// </summary>
    public string PeerSource { get; set; } = string.Empty;

    /// <summary>
    /// Version number for this save (incremented on each change).
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Vector clock for distributed causality tracking.
    /// Used to detect concurrent modifications without relying on synchronized clocks.
    /// </summary>
    public VectorClock VectorClock { get; set; } = new();

    /// <summary>
    /// Formatted size for display.
    /// </summary>
    [JsonIgnore]
    public string FormattedSize => FormatUtils.FormatBytes(SizeBytes);

    /// <summary>
    /// Whether this is a local save (not from a peer).
    /// </summary>
    [JsonIgnore]
    public bool IsLocal => string.IsNullOrEmpty(PeerSource);
}

/// <summary>
/// Represents a conflict between local and remote saves.
/// </summary>
public class SaveConflict
{
    /// <summary>
    /// The local save version.
    /// </summary>
    public SyncedSave LocalSave { get; set; } = new();

    /// <summary>
    /// The remote save version from a peer.
    /// </summary>
    public SyncedSave RemoteSave { get; set; } = new();

    /// <summary>
    /// When the conflict was detected.
    /// </summary>
    public DateTime ConflictTime { get; set; }

    /// <summary>
    /// Game AppId for convenience.
    /// </summary>
    public int AppId => LocalSave.AppId;

    /// <summary>
    /// Game name for convenience.
    /// </summary>
    public string GameName => LocalSave.GameName;
}

/// <summary>
/// Sync state for a specific game's saves.
/// </summary>
public class GameSyncState
{
    public int AppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string SavePath { get; set; } = string.Empty;
    public string LastKnownHash { get; set; } = string.Empty;
    public DateTime LastSyncTime { get; set; }
    public int LocalVersion { get; set; }
    public bool SyncEnabled { get; set; } = true;
    
    /// <summary>
    /// Vector clock tracking modifications to this game's saves.
    /// </summary>
    public VectorClock VectorClock { get; set; } = new();
}

/// <summary>
/// Save sync offer message sent to peers when we have a newer save.
/// </summary>
public class SaveSyncOffer
{
    public int AppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public long SizeBytes { get; set; }
    public int Version { get; set; }

    /// <summary>
    /// Vector clock for distributed conflict detection.
    /// </summary>
    public VectorClock VectorClock { get; set; } = new();
}

/// <summary>
/// Resolution options for save conflicts.
/// </summary>
public enum SaveConflictResolution
{
    /// <summary>
    /// Keep the local save, ignore remote changes.
    /// </summary>
    KeepLocal,
    
    /// <summary>
    /// Use the remote save, backup local as .bak first.
    /// </summary>
    UseRemote,
    
    /// <summary>
    /// Keep both saves (local with _local suffix, remote applied).
    /// </summary>
    KeepBoth,
    
    /// <summary>
    /// Automatically use the most recently modified save.
    /// The "losing" save is always backed up with a timestamped .bak file.
    /// This is the recommended default for automatic sync.
    /// </summary>
    LastModifiedWins
}

/// <summary>
/// Represents a named restore point for save game versioning.
/// Unlike automatic backups, restore points persist until manually deleted or limit is reached.
/// </summary>
public class RestorePoint
{
    /// <summary>
    /// Unique identifier for this restore point.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// User-friendly name for the restore point (e.g., "Pre-Raid", "Before Final Boss").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description providing more context.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// When this restore point was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Steam App ID of the associated game.
    /// </summary>
    public int AppId { get; set; }

    /// <summary>
    /// Game name for display purposes.
    /// </summary>
    public string GameName { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the save data for integrity verification.
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Size of the backup in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Relative path to the backup zip file.
    /// </summary>
    public string BackupFileName { get; set; } = string.Empty;

    /// <summary>
    /// If true, this restore point won't be auto-deleted when limit is reached.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// Hostname of the creator (for LAN sync tracking).
    /// </summary>
    public string CreatedBy { get; set; } = Environment.MachineName;

    /// <summary>
    /// Formatted size for display.
    /// </summary>
    [JsonIgnore]
    public string FormattedSize => FormatUtils.FormatBytes(SizeBytes);

    /// <summary>
    /// Whether this restore point was created locally.
    /// </summary>
    [JsonIgnore]
    public bool IsLocal => CreatedBy == Environment.MachineName;

    /// <summary>
    /// Formatted creation time for display.
    /// </summary>
    [JsonIgnore]
    public string FormattedTime => CreatedAt.ToString("MMM d, yyyy h:mm tt");
}

/// <summary>
/// Offer message sent to LAN peers when a restore point is created.
/// </summary>
public class RestorePointOffer
{
    public Guid RestorePointId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int AppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>
/// Request for all LAN peers to restore to a specific point.
/// </summary>
public class GroupRestoreRequest
{
    public Guid RestorePointId { get; set; }
    public string RestorePointName { get; set; } = string.Empty;
    public int AppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; } = DateTime.Now;
}

