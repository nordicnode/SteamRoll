using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SteamRoll.Services;

/// <summary>
/// Result of a peer connection attempt.
/// </summary>
public class PeerConnectionResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public IPEndPoint? RemoteEndpoint { get; set; }
    public Socket? ConnectedSocket { get; set; }
    public ConnectionMethod Method { get; set; }
}

/// <summary>
/// Method used to establish the peer connection.
/// </summary>
public enum ConnectionMethod
{
    /// <summary>Direct LAN connection.</summary>
    Direct,
    /// <summary>UDP hole-punching.</summary>
    HolePunch,
    /// <summary>Fallback relay (not implemented).</summary>
    Relay,
    /// <summary>Connection failed.</summary>
    Failed
}

/// <summary>
/// Manages peer-to-peer connection establishment using UDP hole-punching.
/// Works in conjunction with LanDiscoveryService for peer coordination.
/// </summary>
public class PeerConnectionService
{
    private readonly StunClient _stunClient;
    private readonly int _holePunchTimeoutMs;
    private readonly ConcurrentDictionary<string, PeerEndpointInfo> _peerEndpoints = new();
    
    private StunResult? _cachedStunResult;
    private DateTime _stunCacheTime;
    private readonly TimeSpan _stunCacheDuration = TimeSpan.FromMinutes(5);
    
    public PeerConnectionService(int holePunchTimeoutMs = 5000)
    {
        _stunClient = new StunClient();
        _holePunchTimeoutMs = holePunchTimeoutMs;
    }
    
    /// <summary>
    /// Gets our public endpoint info, caching the result.
    /// </summary>
    public async Task<StunResult> GetOurPublicEndpointAsync()
    {
        if (_cachedStunResult != null && DateTime.UtcNow - _stunCacheTime < _stunCacheDuration)
        {
            return _cachedStunResult;
        }
        
        _cachedStunResult = await _stunClient.DetectNatTypeAsync();
        _stunCacheTime = DateTime.UtcNow;
        
        LogService.Instance.Info(
            $"Public endpoint: {_cachedStunResult.PublicIp}:{_cachedStunResult.PublicPort}, NAT: {_cachedStunResult.NatType}", 
            "PeerConnection");
        
        return _cachedStunResult;
    }
    
    /// <summary>
    /// Registers a peer's endpoint information for future connections.
    /// </summary>
    public void RegisterPeerEndpoint(string peerId, IPEndPoint publicEndpoint, IPEndPoint? privateEndpoint)
    {
        _peerEndpoints[peerId] = new PeerEndpointInfo
        {
            PeerId = peerId,
            PublicEndpoint = publicEndpoint,
            PrivateEndpoint = privateEndpoint,
            LastSeen = DateTime.UtcNow
        };
    }
    
    /// <summary>
    /// Attempts to establish a connection to a peer using the best available method.
    /// Priority: Direct LAN -> Fail
    /// 
    /// NOTE: TCP Hole Punching requires signaling coordination between both peers
    /// and cannot be attempted automatically here. Use the following workflow instead:
    /// 1. Exchange public endpoints via LanDiscoveryService hole punch signaling
    /// 2. Receive HolePunchGo message with coordinated goTime
    /// 3. Call TryTcpHolePunchAsync() with the coordinated parameters
    /// </summary>
    public async Task<PeerConnectionResult> ConnectToPeerAsync(
        string peerId, 
        IPEndPoint lanEndpoint,
        IPEndPoint? publicEndpoint = null)
    {
        // 1. Try direct LAN connection first (always fastest and most reliable)
        var directResult = await TryDirectConnectionAsync(lanEndpoint);
        if (directResult.Success)
        {
            directResult.Method = ConnectionMethod.Direct;
            LogService.Instance.Info($"Connected to {peerId} via direct LAN", "PeerConnection");
            return directResult;
        }
        
        // 2. TCP Hole Punching requires signaling coordination between both peers.
        //    It cannot be attempted automatically here because both sides must call
        //    ConnectAsync() at exactly the same time (TCP Simultaneous Open).
        //    
        //    To use hole punching:
        //    1. Call LanDiscoveryService.SendHolePunchRequestAsync() to initiate
        //    2. Handle HolePunchCoordinationReceived event for the response
        //    3. When HolePunchGo is received, call TryTcpHolePunchAsync()
        //
        //    The old approach of sending UDP packets and then trying TCP was
        //    fundamentally broken because NATs track UDP and TCP separately.
        if (publicEndpoint != null)
        {
            LogService.Instance.Debug(
                $"Direct connection to {peerId} failed. Hole punching requires signaling coordination - use LanDiscoveryService.SendHolePunchRequestAsync() to initiate.",
                "PeerConnection");
        }
        
        return new PeerConnectionResult
        {
            Success = false,
            Method = ConnectionMethod.Failed,
            Error = "Direct connection failed. Use signaling coordination for hole punching."
        };
    }
    
    /// <summary>
    /// Attempts a direct TCP connection to the peer.
    /// </summary>
    private async Task<PeerConnectionResult> TryDirectConnectionAsync(IPEndPoint endpoint)
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            
            using var cts = new CancellationTokenSource(3000);
            await socket.ConnectAsync(endpoint, cts.Token);
            
            return new PeerConnectionResult
            {
                Success = true,
                RemoteEndpoint = endpoint,
                ConnectedSocket = socket
            };
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Direct connection failed: {ex.Message}", "PeerConnection");
            return new PeerConnectionResult { Success = false, Error = ex.Message };
        }
    }
    
    /// <summary>
    /// Attempts TCP Simultaneous Open (TCP hole-punching) to establish a connection through NAT.
    /// 
    /// This replaces the previous flawed implementation that tried to use UDP hole punching
    /// to open a path for TCP. NATs track UDP and TCP sessions separately, so that approach
    /// was fundamentally broken.
    /// 
    /// TCP Simultaneous Open works by having both peers call ConnectAsync() at the same time.
    /// This requires coordination via signaling messages through LanDiscoveryService.
    /// 
    /// IMPORTANT: This method performs the actual TCP connect after signaling has already
    /// been coordinated. The caller must have already:
    /// 1. Exchanged public endpoints via signaling (HolePunchRequest/Response)
    /// 2. Received the GO signal with coordinated goTime
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="peerPublicEndpoint">The peer's public endpoint (from signaling).</param>
    /// <param name="ourLocalPort">Our local port to bind to (from STUN).</param>
    /// <param name="goTime">The coordinated UTC time to start connecting.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the hole punch attempt.</returns>
    public async Task<PeerConnectionResult> TryTcpHolePunchAsync(
        string peerId,
        IPEndPoint peerPublicEndpoint,
        int ourLocalPort,
        DateTime goTime,
        CancellationToken cancellationToken = default)
    {
        var holePunchService = new TcpHolePunchService();
        
        try
        {
            LogService.Instance.Info(
                $"Starting TCP hole punch to {peerId} ({peerPublicEndpoint}), local port: {ourLocalPort}",
                "PeerConnection");
            
            var result = await holePunchService.AttemptHolePunchAsync(
                peerPublicEndpoint,
                ourLocalPort,
                goTime,
                cancellationToken);
            
            if (result.Success && result.ConnectedSocket != null)
            {
                LogService.Instance.Info(
                    $"TCP hole punch succeeded to {peerId} in {result.Duration.TotalMilliseconds:F0}ms (attempt {result.AttemptNumber})",
                    "PeerConnection");
                
                return new PeerConnectionResult
                {
                    Success = true,
                    ConnectedSocket = result.ConnectedSocket,
                    RemoteEndpoint = result.RemoteEndpoint,
                    Method = ConnectionMethod.HolePunch
                };
            }
            else
            {
                LogService.Instance.Warning(
                    $"TCP hole punch failed to {peerId}: {result.Error}",
                    "PeerConnection");
                
                return new PeerConnectionResult
                {
                    Success = false,
                    Error = result.Error,
                    Method = ConnectionMethod.Failed
                };
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"TCP hole punch exception: {ex.Message}", ex, "PeerConnection");
            return new PeerConnectionResult
            {
                Success = false,
                Error = ex.Message,
                Method = ConnectionMethod.Failed
            };
        }
    }
    
    /// <summary>
    /// [DEPRECATED] Old UDP-then-TCP hole punch implementation.
    /// This method was fundamentally flawed because NATs track UDP and TCP separately.
    /// Opening a UDP hole does NOT allow TCP traffic through.
    /// 
    /// Use TryTcpHolePunchAsync with coordinated signaling instead.
    /// </summary>
    [Obsolete("Use TryTcpHolePunchAsync with coordinated signaling. UDP holes do not enable TCP traffic.")]
    private async Task<PeerConnectionResult> TryHolePunchingAsync_Deprecated(string peerId, IPEndPoint publicEndpoint)
    {
        // This implementation was technically flawed and has been replaced.
        // Keeping as reference for what NOT to do:
        // 
        // The old approach:
        // 1. Send UDP packets to peer (creates UDP NAT mapping)
        // 2. Try TCP connect through "the opened hole"
        // 
        // Why it failed:
        // - NAT devices maintain SEPARATE state tables for UDP and TCP
        // - A UDP mapping does NOT allow incoming TCP traffic
        // - For TCP hole punching, both sides must call ConnectAsync() simultaneously
        //   (TCP Simultaneous Open) with coordinated timing
        
        LogService.Instance.Warning(
            "Deprecated hole punch method called - use TryTcpHolePunchAsync instead",
            "PeerConnection");
        
        return new PeerConnectionResult
        {
            Success = false,
            Error = "Deprecated: Use TryTcpHolePunchAsync with signaling coordination",
            Method = ConnectionMethod.Failed
        };
    }
    
    /// <summary>
    /// Determines if hole-punching is likely to work based on NAT types.
    /// </summary>
    public async Task<bool> CanHolePunchWithPeerAsync(NatType peerNatType)
    {
        var ourResult = await GetOurPublicEndpointAsync();
        
        // Hole punching works when at least one side has a favorable NAT type
        // Best case: Both sides have Full Cone NAT
        // Worst case: Both sides have Symmetric NAT (won't work)
        
        return ourResult.SupportsHolePunching || 
               peerNatType is NatType.Open or NatType.FullCone or NatType.RestrictedCone;
    }
    
    /// <summary>
    /// Gets a summary of our network connectivity for display.
    /// </summary>
    public async Task<NetworkConnectivityInfo> GetConnectivityInfoAsync()
    {
        var stunResult = await GetOurPublicEndpointAsync();
        var isBehindNat = await _stunClient.IsBehindNatAsync();
        
        return new NetworkConnectivityInfo
        {
            PublicIp = stunResult.PublicIp?.ToString() ?? "Unknown",
            PublicPort = stunResult.PublicPort,
            NatType = stunResult.NatType,
            IsBehindNat = isBehindNat,
            SupportsPeerToPeer = stunResult.SupportsPeerToPeer,
            SupportsHolePunching = stunResult.SupportsHolePunching
        };
    }
    
    /// <summary>
    /// Cleans up stale peer endpoint entries.
    /// </summary>
    public void CleanupStaleEndpoints(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var staleKeys = _peerEndpoints
            .Where(kvp => kvp.Value.LastSeen < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();
        
        foreach (var key in staleKeys)
        {
            _peerEndpoints.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// Stores endpoint information for a peer.
/// </summary>
public class PeerEndpointInfo
{
    public string PeerId { get; set; } = "";
    public IPEndPoint? PublicEndpoint { get; set; }
    public IPEndPoint? PrivateEndpoint { get; set; }
    public DateTime LastSeen { get; set; }
}

/// <summary>
/// Summary of network connectivity status.
/// </summary>
public class NetworkConnectivityInfo
{
    public string PublicIp { get; set; } = "";
    public int PublicPort { get; set; }
    public NatType NatType { get; set; }
    public bool IsBehindNat { get; set; }
    public bool SupportsPeerToPeer { get; set; }
    public bool SupportsHolePunching { get; set; }
    
    public string NatTypeDisplay => NatType switch
    {
        NatType.Open => "🟢 Open (Direct)",
        NatType.FullCone => "🟢 Full Cone",
        NatType.RestrictedCone => "🟡 Restricted",
        NatType.PortRestrictedCone => "🟡 Port Restricted",
        NatType.Symmetric => "🔴 Symmetric",
        NatType.UdpBlocked => "🔴 UDP Blocked",
        _ => "⚪ Unknown"
    };
    
    public string ConnectivitySummary => SupportsPeerToPeer
        ? "✅ P2P connections supported"
        : "⚠️ May require relay for some peers";
}
