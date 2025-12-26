using System.Net;

namespace SteamRoll.Services;

/// <summary>
/// Coordination data for TCP hole punch signaling between peers.
/// Sent as serialized JSON in DiscoveryMessage.HolePunchJson.
/// </summary>
public class HolePunchCoordinationData
{
    /// <summary>
    /// Unique ID for this hole punch session.
    /// </summary>
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    
    /// <summary>
    /// The signal type (maps to ProtocolConstants.HOLE_PUNCH_* values).
    /// </summary>
    public string SignalType { get; set; } = "";
    
    /// <summary>
    /// Sender's public IP address (from STUN).
    /// </summary>
    public string? PublicIp { get; set; }
    
    /// <summary>
    /// Sender's public port (from STUN).
    /// </summary>
    public int PublicPort { get; set; }
    
    /// <summary>
    /// Sender's local/LAN IP address.
    /// </summary>
    public string? LocalIp { get; set; }
    
    /// <summary>
    /// Sender's local port.
    /// </summary>
    public int LocalPort { get; set; }
    
    /// <summary>
    /// Sender's NAT type (for compatibility checking).
    /// </summary>
    public NatType NatType { get; set; } = NatType.Unknown;
    
    /// <summary>
    /// Coordinated UTC timestamp when both peers should call ConnectAsync.
    /// Only set in HolePunchGo messages.
    /// </summary>
    public DateTime? GoTime { get; set; }
    
    /// <summary>
    /// Error message if SignalType is HP_FAIL.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Event args for hole punch coordination events.
/// </summary>
public class HolePunchCoordinationEventArgs : EventArgs
{
    /// <summary>
    /// The peer's hostname.
    /// </summary>
    public string PeerHostName { get; set; } = "";
    
    /// <summary>
    /// The peer's LAN IP address (from UDP source).
    /// </summary>
    public string PeerLanIp { get; set; } = "";
    
    /// <summary>
    /// The peer's transfer port.
    /// </summary>
    public int PeerTransferPort { get; set; }
    
    /// <summary>
    /// The hole punch coordination data.
    /// </summary>
    public HolePunchCoordinationData Data { get; set; } = new();
    
    /// <summary>
    /// Gets the peer's public endpoint (if available).
    /// </summary>
    public IPEndPoint? PeerPublicEndpoint
    {
        get
        {
            if (string.IsNullOrEmpty(Data.PublicIp) || Data.PublicPort == 0)
                return null;
            
            if (IPAddress.TryParse(Data.PublicIp, out var ip))
                return new IPEndPoint(ip, Data.PublicPort);
            
            return null;
        }
    }
}

/// <summary>
/// Tracks the state of an active hole punch coordination session.
/// </summary>
public class HolePunchSession
{
    public string SessionId { get; set; } = "";
    public string PeerId { get; set; } = "";
    public string PeerHostName { get; set; } = "";
    public string PeerLanIp { get; set; } = "";
    public int PeerTransferPort { get; set; }
    public IPEndPoint? PeerPublicEndpoint { get; set; }
    public IPEndPoint? OurPublicEndpoint { get; set; }
    public NatType PeerNatType { get; set; } = NatType.Unknown;
    public NatType OurNatType { get; set; } = NatType.Unknown;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? GoTime { get; set; }
    public HolePunchSessionState State { get; set; } = HolePunchSessionState.Initiated;
    public TaskCompletionSource<HolePunchResult>? CompletionSource { get; set; }
}

/// <summary>
/// State of a hole punch coordination session.
/// </summary>
public enum HolePunchSessionState
{
    /// <summary>We initiated the request, waiting for response.</summary>
    Initiated,
    /// <summary>Received request from peer, sent our response.</summary>
    Responded,
    /// <summary>Both sides ready, GO signal sent/received.</summary>
    Connecting,
    /// <summary>Connection established successfully.</summary>
    Connected,
    /// <summary>Connection attempt failed.</summary>
    Failed,
    /// <summary>Session timed out.</summary>
    TimedOut
}
