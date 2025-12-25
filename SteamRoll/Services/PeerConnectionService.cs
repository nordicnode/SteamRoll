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
    /// Priority: Direct LAN -> UDP Hole Punch -> Fail
    /// </summary>
    public async Task<PeerConnectionResult> ConnectToPeerAsync(
        string peerId, 
        IPEndPoint lanEndpoint,
        IPEndPoint? publicEndpoint = null)
    {
        // 1. Try direct LAN connection first (always fastest)
        var directResult = await TryDirectConnectionAsync(lanEndpoint);
        if (directResult.Success)
        {
            directResult.Method = ConnectionMethod.Direct;
            LogService.Instance.Info($"Connected to {peerId} via direct LAN", "PeerConnection");
            return directResult;
        }
        
        // 2. If we have a public endpoint, try hole punching
        if (publicEndpoint != null)
        {
            var holePunchResult = await TryHolePunchingAsync(peerId, publicEndpoint);
            if (holePunchResult.Success)
            {
                holePunchResult.Method = ConnectionMethod.HolePunch;
                LogService.Instance.Info($"Connected to {peerId} via hole-punching", "PeerConnection");
                return holePunchResult;
            }
        }
        
        return new PeerConnectionResult
        {
            Success = false,
            Method = ConnectionMethod.Failed,
            Error = "All connection methods failed"
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
    /// Attempts UDP hole-punching to establish a connection through NAT.
    /// This is a simplified implementation that sends UDP packets to punch a hole,
    /// then attempts a TCP connection through the opened port mapping.
    /// </summary>
    private async Task<PeerConnectionResult> TryHolePunchingAsync(string peerId, IPEndPoint publicEndpoint)
    {
        try
        {
            // UDP hole punching works by both sides sending UDP packets to each other
            // This creates NAT mappings that allow return traffic
            
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            // Our punch packet (just a small identifier)
            var punchData = System.Text.Encoding.UTF8.GetBytes($"STEAMROLL_PUNCH:{peerId}");
            
            // Send multiple punch packets to increase success chance
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await udpClient.SendAsync(punchData, punchData.Length, publicEndpoint);
                    await Task.Delay(100);
                }
                catch
                {
                    // Ignore send failures
                }
            }
            
            // Wait a moment for NAT mappings to establish
            await Task.Delay(500);
            
            // Now try TCP connection through the (hopefully) punched hole
            // Note: In practice, this requires both sides to coordinate timing
            var tcpResult = await TryDirectConnectionAsync(publicEndpoint);
            
            return tcpResult;
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Hole-punching failed: {ex.Message}", "PeerConnection");
            return new PeerConnectionResult { Success = false, Error = ex.Message };
        }
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
