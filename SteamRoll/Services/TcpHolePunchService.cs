using System.Net;
using System.Net.Sockets;

namespace SteamRoll.Services;

/// <summary>
/// Result of a TCP hole punch attempt.
/// </summary>
public class HolePunchResult
{
    public bool Success { get; set; }
    public Socket? ConnectedSocket { get; set; }
    public IPEndPoint? LocalEndpoint { get; set; }
    public IPEndPoint? RemoteEndpoint { get; set; }
    public string? Error { get; set; }
    public int AttemptNumber { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Implements TCP Simultaneous Open (TCP hole punching) for NAT traversal.
/// 
/// TCP Simultaneous Open works by having both peers call ConnectAsync() at the
/// same time to each other's public endpoints. When both SYN packets cross in
/// the network:
/// 1. Each NAT creates an outbound mapping
/// 2. The crossing SYNs trigger SYN+ACK responses
/// 3. A bidirectional TCP connection is established
/// 
/// This requires coordinated timing between peers, handled via signaling messages
/// through LanDiscoveryService.
/// </summary>
public class TcpHolePunchService
{
    private readonly int _syncDelayMs;
    private readonly int _connectTimeoutMs;
    private readonly int _retryCount;
    
    public TcpHolePunchService(
        int syncDelayMs = ProtocolConstants.HOLE_PUNCH_SYNC_DELAY_MS,
        int connectTimeoutMs = ProtocolConstants.HOLE_PUNCH_CONNECT_TIMEOUT_MS,
        int retryCount = ProtocolConstants.HOLE_PUNCH_RETRY_COUNT)
    {
        _syncDelayMs = syncDelayMs;
        _connectTimeoutMs = connectTimeoutMs;
        _retryCount = retryCount;
    }
    
    /// <summary>
    /// Prepares a socket for TCP hole punching with necessary options set.
    /// </summary>
    /// <param name="localPort">Local port to bind to (from STUN result).</param>
    /// <returns>A configured socket ready for hole punching.</returns>
    public Socket PrepareHolePunchSocket(int localPort)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        
        // Critical: Enable address/port reuse for simultaneous open
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        
        // On Windows, ReuseUnicastPort is the preferred option for port reuse in outbound connections
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseUnicastPort, true);
        }
        catch (SocketException)
        {
            // ReuseUnicastPort may not be available on older Windows versions
            LogService.Instance.Debug("ReuseUnicastPort not available, using ReuseAddress only", "HolePunch");
        }
        
        // Disable Nagle's algorithm for lower latency
        socket.NoDelay = true;
        
        // Bind to the specific local port
        socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
        
        return socket;
    }
    
    /// <summary>
    /// Attempts TCP Simultaneous Open to establish a connection through NAT.
    /// Both peers must call this at approximately the same time (coordinated via signaling).
    /// </summary>
    /// <param name="remoteEndpoint">The remote peer's public endpoint (from STUN/signaling).</param>
    /// <param name="localPort">Our local port to use (from STUN result).</param>
    /// <param name="goTime">The coordinated time when both peers should start connecting.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the hole punch attempt.</returns>
    public async Task<HolePunchResult> AttemptHolePunchAsync(
        IPEndPoint remoteEndpoint,
        int localPort,
        DateTime goTime,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        
        for (int attempt = 1; attempt <= _retryCount; attempt++)
        {
            Socket? socket = null;
            try
            {
                socket = PrepareHolePunchSocket(localPort);
                
                // Wait until the coordinated go time
                var waitTime = goTime - DateTime.UtcNow;
                if (waitTime > TimeSpan.Zero)
                {
                    LogService.Instance.Debug($"Waiting {waitTime.TotalMilliseconds:F0}ms until coordinated go time", "HolePunch");
                    await Task.Delay(waitTime, cancellationToken);
                }
                
                // Add the sync delay for simultaneous open
                await Task.Delay(_syncDelayMs, cancellationToken);
                
                LogService.Instance.Info(
                    $"Attempt {attempt}/{_retryCount}: Connecting to {remoteEndpoint} from port {localPort}", 
                    "HolePunch");
                
                // Attempt the connection with timeout
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(_connectTimeoutMs);
                
                await socket.ConnectAsync(remoteEndpoint, connectCts.Token);
                
                // Success!
                var duration = DateTime.UtcNow - startTime;
                LogService.Instance.Info(
                    $"TCP hole punch succeeded to {remoteEndpoint} in {duration.TotalMilliseconds:F0}ms", 
                    "HolePunch");
                
                return new HolePunchResult
                {
                    Success = true,
                    ConnectedSocket = socket,
                    LocalEndpoint = socket.LocalEndPoint as IPEndPoint,
                    RemoteEndpoint = remoteEndpoint,
                    AttemptNumber = attempt,
                    Duration = duration
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Connect timeout, try again
                socket?.Dispose();
                LogService.Instance.Debug($"Attempt {attempt} timed out, retrying...", "HolePunch");
            }
            catch (SocketException ex)
            {
                socket?.Dispose();
                
                // Connection refused or reset is expected during simultaneous open attempts
                if (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset)
                {
                    LogService.Instance.Debug(
                        $"Attempt {attempt}: {ex.SocketErrorCode}, retrying...", 
                        "HolePunch");
                    
                    // Small delay before retry
                    await Task.Delay(100 * attempt, cancellationToken);
                    continue;
                }
                
                // Other socket errors are likely fatal
                LogService.Instance.Warning($"Socket error during hole punch: {ex.Message}", "HolePunch");
                return new HolePunchResult
                {
                    Success = false,
                    Error = $"Socket error: {ex.SocketErrorCode}",
                    AttemptNumber = attempt,
                    Duration = DateTime.UtcNow - startTime
                };
            }
            catch (Exception ex)
            {
                socket?.Dispose();
                LogService.Instance.Error($"Unexpected error during hole punch: {ex.Message}", ex, "HolePunch");
                return new HolePunchResult
                {
                    Success = false,
                    Error = ex.Message,
                    AttemptNumber = attempt,
                    Duration = DateTime.UtcNow - startTime
                };
            }
        }
        
        return new HolePunchResult
        {
            Success = false,
            Error = $"All {_retryCount} attempts failed",
            AttemptNumber = _retryCount,
            Duration = DateTime.UtcNow - startTime
        };
    }
    
    /// <summary>
    /// Checks if TCP hole punching is likely to succeed based on NAT types.
    /// </summary>
    /// <param name="ourNatType">Our NAT type from STUN.</param>
    /// <param name="peerNatType">Peer's NAT type.</param>
    /// <returns>True if hole punching has reasonable chance of success.</returns>
    public static bool IsHolePunchLikelyToSucceed(NatType ourNatType, NatType peerNatType)
    {
        // Symmetric NAT on both sides = nearly impossible
        if (ourNatType == NatType.Symmetric && peerNatType == NatType.Symmetric)
        {
            return false;
        }
        
        // If either side has UDP blocked, we can't do STUN discovery properly
        if (ourNatType == NatType.UdpBlocked || peerNatType == NatType.UdpBlocked)
        {
            return false;
        }
        
        // Open or Full Cone NATs work well with any other type (except dual-symmetric)
        if (ourNatType is NatType.Open or NatType.FullCone)
        {
            return true;
        }
        if (peerNatType is NatType.Open or NatType.FullCone)
        {
            return true;
        }
        
        // Restricted cone NATs can work with each other
        if (ourNatType is NatType.RestrictedCone or NatType.PortRestrictedCone &&
            peerNatType is NatType.RestrictedCone or NatType.PortRestrictedCone)
        {
            return true;
        }
        
        // One symmetric NAT = low chance but possible with favorable peer
        if (ourNatType == NatType.Symmetric && peerNatType is NatType.Open or NatType.FullCone)
        {
            return true;
        }
        if (peerNatType == NatType.Symmetric && ourNatType is NatType.Open or NatType.FullCone)
        {
            return true;
        }
        
        // Unknown NATs - worth trying
        if (ourNatType == NatType.Unknown || peerNatType == NatType.Unknown)
        {
            return true;
        }
        
        // Default: worth trying
        return true;
    }
    
    /// <summary>
    /// Gets a human-readable description of hole punch success likelihood.
    /// </summary>
    public static string GetHolePunchLikelihoodDescription(NatType ourNatType, NatType peerNatType)
    {
        if (ourNatType == NatType.Symmetric && peerNatType == NatType.Symmetric)
            return "❌ Very unlikely (both Symmetric NAT)";
        
        if (ourNatType is NatType.Open or NatType.FullCone && 
            peerNatType is NatType.Open or NatType.FullCone)
            return "✅ Excellent (both Open/Full Cone)";
        
        if (ourNatType is NatType.Open or NatType.FullCone || 
            peerNatType is NatType.Open or NatType.FullCone)
            return "✅ Good (one side Open/Full Cone)";
        
        if (ourNatType == NatType.Symmetric || peerNatType == NatType.Symmetric)
            return "⚠️ Low (Symmetric NAT present)";
        
        if (ourNatType is NatType.RestrictedCone or NatType.PortRestrictedCone &&
            peerNatType is NatType.RestrictedCone or NatType.PortRestrictedCone)
            return "🟡 Medium (both Restricted Cone)";
        
        return "🟡 Unknown";
    }
}
