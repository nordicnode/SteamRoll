using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamRoll.Services;

/// <summary>
/// Information about a discovered peer on the network.
/// </summary>
public class PeerInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public string Id { get; set; } = "";
    public string HostName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int TransferPort { get; set; }
    public DateTime LastSeen { get; set; }
    public int PackagedGameCount { get; set; }
    public bool IsManual { get; set; }

    public string DisplayName => IsManual ? $"{HostName} (Manual)" : $"{HostName} ({IpAddress})";

    // Connection health properties
    private long _latencyMs;
    public long LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (_latencyMs != value)
            {
                _latencyMs = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LatencyDisplay));
                OnPropertyChanged(nameof(QualityDisplay));
            }
        }
    }

    private bool _isOnline = true;
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (_isOnline != value)
            {
                _isOnline = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(QualityDisplay));
            }
        }
    }

    public string LatencyDisplay => IsOnline && LatencyMs > 0 ? $"{LatencyMs}ms" : "";
    
    public string QualityDisplay
    {
        get
        {
            if (!IsOnline) return "🔴 Offline";
            return LatencyMs switch
            {
                < 50 => "🟢 Excellent",
                < 150 => "🟢 Good",
                < 300 => "🟡 Fair",
                _ => "🟠 Poor"
            };
        }
    }
}

/// <summary>
/// Incoming transfer request from a peer.
/// </summary>
public class TransferRequest
{
    public string FromHostName { get; set; } = "";
    public string FromIp { get; set; } = "";
    public int FromPort { get; set; }
    public string GameName { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime RequestedAt { get; set; }

    public string FormattedSize => FormatUtils.FormatBytes(SizeBytes);
}

/// <summary>
/// Message types for LAN discovery protocol.
/// </summary>
public enum MessageType
{
    Announce,
    TransferRequest,
    TransferAccept,
    TransferReject,
    GameListRequest,
    GameListResponse,
    SaveSyncOffer,
    SaveSyncRequest,
    /// <summary>
    /// Query for peers that have a specific game for swarm download.
    /// </summary>
    SwarmQuery,
    /// <summary>
    /// Response indicating this peer has the queried game.
    /// </summary>
    SwarmResponse,
    /// <summary>
    /// Broadcast when a named restore point is created, offering it to peers.
    /// </summary>
    RestorePointOffer,
    /// <summary>
    /// Request to download a restore point from a peer.
    /// </summary>
    RestorePointRequest,
    /// <summary>
    /// Request all peers to restore to a specific save point.
    /// </summary>
    GroupRestoreRequest,
    /// <summary>
    /// Request to initiate TCP hole punch coordination with a peer.
    /// </summary>
    HolePunchRequest,
    /// <summary>
    /// Response to hole punch request with own public endpoint.
    /// </summary>
    HolePunchResponse,
    /// <summary>
    /// Signal to start simultaneous TCP connect at coordinated time.
    /// </summary>
    HolePunchGo
}

/// <summary>
/// Discovery message structure.
/// </summary>
public class DiscoveryMessage
{
    public string Magic { get; set; } = ProtocolConstants.DISCOVERY_MAGIC;
    public MessageType Type { get; set; }
    public string? HostName { get; set; }
    public int TransferPort { get; set; }
    public int PackagedGameCount { get; set; }
    public string? GameName { get; set; }
    public long GameSize { get; set; }
    
    /// <summary>
    /// Serialized game list for GameListResponse messages.
    /// </summary>
    public string? GameListJson { get; set; }
    
    /// <summary>
    /// Serialized save sync offer for SaveSyncOffer messages.
    /// </summary>
    public string? SaveSyncJson { get; set; }

    /// <summary>
    /// For SwarmQuery: hash of the file being requested.
    /// For SwarmResponse: indicates if this peer has the file.
    /// </summary>
    public string? FileHash { get; set; }

    /// <summary>
    /// For SwarmResponse: this peer's unique ID for swarm coordination.
    /// </summary>
    public Guid? SwarmPeerId { get; set; }

    /// <summary>
    /// For SwarmResponse: this peer's advertised upload speed (bytes/sec).
    /// </summary>
    public long SwarmUploadSpeed { get; set; }

    /// <summary>
    /// Serialized restore point data for RestorePointOffer/Request/GroupRestore messages.
    /// </summary>
    public string? RestorePointJson { get; set; }
    
    /// <summary>
    /// Serialized hole punch coordination data for HolePunchRequest/Response/Go messages.
    /// </summary>
    public string? HolePunchJson { get; set; }
}

/// <summary>
/// Event args for save sync offer received events.
/// </summary>
public class SaveSyncOfferEventArgs : EventArgs
{
    public string PeerHostName { get; set; } = string.Empty;
    public string PeerIp { get; set; } = string.Empty;
    public int PeerPort { get; set; }
    public SaveSyncOffer Offer { get; set; } = new();
}

/// <summary>
/// Event args for swarm peer responses.
/// </summary>
public class SwarmPeerResponseEventArgs : EventArgs
{
    public Guid PeerId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public string GameName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long UploadSpeedBytesPerSec { get; set; }
}

