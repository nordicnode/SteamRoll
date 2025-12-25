using System.Net;
using System.Net.Sockets;

namespace SteamRoll.Services;

/// <summary>
/// NAT type detection results.
/// </summary>
public enum NatType
{
    /// <summary>No NAT detected - direct internet connection.</summary>
    Open,
    /// <summary>Full cone NAT - any external host can send packets.</summary>
    FullCone,
    /// <summary>Restricted cone NAT - only known hosts can send packets.</summary>
    RestrictedCone,
    /// <summary>Port restricted NAT - only known host:port pairs can send.</summary>
    PortRestrictedCone,
    /// <summary>Symmetric NAT - different external port per destination.</summary>
    Symmetric,
    /// <summary>Could not determine NAT type.</summary>
    Unknown,
    /// <summary>UDP is blocked.</summary>
    UdpBlocked
}

/// <summary>
/// Result of a STUN binding request.
/// </summary>
public class StunResult
{
    public bool Success { get; set; }
    public NatType NatType { get; set; } = NatType.Unknown;
    public IPAddress? PublicIp { get; set; }
    public int PublicPort { get; set; }
    public string? Error { get; set; }
    
    /// <summary>
    /// Whether this NAT type supports peer-to-peer connections.
    /// </summary>
    public bool SupportsPeerToPeer => NatType is NatType.Open or NatType.FullCone or NatType.RestrictedCone or NatType.PortRestrictedCone;
    
    /// <summary>
    /// Whether hole punching is likely to work.
    /// </summary>
    public bool SupportsHolePunching => NatType is NatType.Open or NatType.FullCone or NatType.RestrictedCone;
}

/// <summary>
/// Lightweight STUN (Session Traversal Utilities for NAT) client.
/// Implements RFC 5389 binding requests to determine public IP and NAT type.
/// </summary>
public class StunClient
{
    // Public STUN servers (free, widely available)
    private static readonly string[] DefaultStunServers = new[]
    {
        "stun.l.google.com:19302",
        "stun1.l.google.com:19302",
        "stun.stunprotocol.org:3478",
        "stun.voip.eutelia.it:3478"
    };
    
    // STUN message type constants (RFC 5389)
    private const ushort STUN_BINDING_REQUEST = 0x0001;
    private const ushort STUN_BINDING_RESPONSE = 0x0101;
    private const uint STUN_MAGIC_COOKIE = 0x2112A442;
    
    // Attribute types
    private const ushort ATTR_MAPPED_ADDRESS = 0x0001;
    private const ushort ATTR_XOR_MAPPED_ADDRESS = 0x0020;
    
    private readonly int _timeoutMs;
    
    public StunClient(int timeoutMs = 3000)
    {
        _timeoutMs = timeoutMs;
    }
    
    /// <summary>
    /// Performs a STUN binding request to discover public IP and port.
    /// </summary>
    public async Task<StunResult> GetPublicEndpointAsync(string? stunServer = null)
    {
        stunServer ??= DefaultStunServers[0];
        
        try
        {
            var (host, port) = ParseServer(stunServer);
            var serverEndpoint = await ResolveEndpointAsync(host, port);
            
            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = _timeoutMs;
            
            // Build STUN binding request
            var request = BuildBindingRequest();
            
            // Send request
            await udpClient.SendAsync(request, request.Length, serverEndpoint);
            
            // Wait for response
            using var cts = new CancellationTokenSource(_timeoutMs);
            var response = await udpClient.ReceiveAsync(cts.Token);
            
            // Parse response
            return ParseBindingResponse(response.Buffer);
        }
        catch (OperationCanceledException)
        {
            return new StunResult { Success = false, NatType = NatType.UdpBlocked, Error = "Request timed out" };
        }
        catch (SocketException ex)
        {
            return new StunResult { Success = false, NatType = NatType.UdpBlocked, Error = ex.Message };
        }
        catch (Exception ex)
        {
            return new StunResult { Success = false, Error = ex.Message };
        }
    }
    
    /// <summary>
    /// Performs NAT type detection using multiple STUN requests.
    /// This is a simplified detection that determines basic NAT characteristics.
    /// </summary>
    public async Task<StunResult> DetectNatTypeAsync()
    {
        // First, try to get our public endpoint
        var result1 = await GetPublicEndpointAsync(DefaultStunServers[0]);
        if (!result1.Success)
        {
            return result1;
        }
        
        // Try a second server to see if we get the same public IP
        var result2 = await GetPublicEndpointAsync(DefaultStunServers[1]);
        if (!result2.Success)
        {
            // One server worked, assume basic NAT
            result1.NatType = NatType.RestrictedCone;
            return result1;
        }
        
        // Compare results
        if (result1.PublicIp?.Equals(result2.PublicIp) == true)
        {
            if (result1.PublicPort == result2.PublicPort)
            {
                // Same IP and port with different servers = Full Cone or Restricted Cone
                result1.NatType = NatType.FullCone;
            }
            else
            {
                // Same IP but different ports = Symmetric NAT (port-dependent)
                result1.NatType = NatType.Symmetric;
            }
        }
        else
        {
            // Different IPs is unusual, might be load-balanced or VPN
            result1.NatType = NatType.Unknown;
        }
        
        return result1;
    }
    
    /// <summary>
    /// Quick check to see if we're behind NAT.
    /// </summary>
    public async Task<bool> IsBehindNatAsync()
    {
        var result = await GetPublicEndpointAsync();
        if (!result.Success || result.PublicIp == null)
            return true; // Assume NAT if we can't determine
        
        // Get local IPs and compare
        try
        {
            var hostName = Dns.GetHostName();
            var localAddresses = await Dns.GetHostAddressesAsync(hostName);
            
            return !localAddresses.Any(local => 
                local.AddressFamily == AddressFamily.InterNetwork && 
                local.Equals(result.PublicIp));
        }
        catch
        {
            return true;
        }
    }
    
    private static (string Host, int Port) ParseServer(string server)
    {
        var parts = server.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 ? int.Parse(parts[1]) : 3478;
        return (host, port);
    }
    
    private static async Task<IPEndPoint> ResolveEndpointAsync(string host, int port)
    {
        var addresses = await Dns.GetHostAddressesAsync(host);
        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? throw new InvalidOperationException($"Could not resolve {host}");
        return new IPEndPoint(ip, port);
    }
    
    private byte[] BuildBindingRequest()
    {
        // STUN header: 20 bytes
        // Type (2) + Length (2) + Magic Cookie (4) + Transaction ID (12)
        var buffer = new byte[20];
        
        // Message Type: Binding Request
        buffer[0] = (byte)(STUN_BINDING_REQUEST >> 8);
        buffer[1] = (byte)(STUN_BINDING_REQUEST & 0xFF);
        
        // Message Length: 0 (no attributes)
        buffer[2] = 0;
        buffer[3] = 0;
        
        // Magic Cookie (RFC 5389)
        buffer[4] = (byte)((STUN_MAGIC_COOKIE >> 24) & 0xFF);
        buffer[5] = (byte)((STUN_MAGIC_COOKIE >> 16) & 0xFF);
        buffer[6] = (byte)((STUN_MAGIC_COOKIE >> 8) & 0xFF);
        buffer[7] = (byte)(STUN_MAGIC_COOKIE & 0xFF);
        
        // Transaction ID (12 random bytes)
        Random.Shared.NextBytes(buffer.AsSpan(8, 12));
        
        return buffer;
    }
    
    private StunResult ParseBindingResponse(byte[] data)
    {
        if (data.Length < 20)
        {
            return new StunResult { Success = false, Error = "Response too short" };
        }
        
        // Check message type
        var messageType = (ushort)((data[0] << 8) | data[1]);
        if (messageType != STUN_BINDING_RESPONSE)
        {
            return new StunResult { Success = false, Error = $"Unexpected message type: {messageType}" };
        }
        
        // Parse attributes
        var messageLength = (data[2] << 8) | data[3];
        var offset = 20; // Skip header
        
        IPAddress? mappedAddress = null;
        int mappedPort = 0;
        
        while (offset + 4 <= data.Length && offset < 20 + messageLength)
        {
            var attrType = (ushort)((data[offset] << 8) | data[offset + 1]);
            var attrLength = (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;
            
            if (offset + attrLength > data.Length) break;
            
            if (attrType == ATTR_XOR_MAPPED_ADDRESS || attrType == ATTR_MAPPED_ADDRESS)
            {
                // Parse address
                if (attrLength >= 8)
                {
                    var family = data[offset + 1];
                    
                    if (family == 0x01) // IPv4
                    {
                        if (attrType == ATTR_XOR_MAPPED_ADDRESS)
                        {
                            // XOR'd values
                            mappedPort = ((data[offset + 2] << 8) | data[offset + 3]) ^ (int)(STUN_MAGIC_COOKIE >> 16);
                            var ip = new byte[4];
                            ip[0] = (byte)(data[offset + 4] ^ ((STUN_MAGIC_COOKIE >> 24) & 0xFF));
                            ip[1] = (byte)(data[offset + 5] ^ ((STUN_MAGIC_COOKIE >> 16) & 0xFF));
                            ip[2] = (byte)(data[offset + 6] ^ ((STUN_MAGIC_COOKIE >> 8) & 0xFF));
                            ip[3] = (byte)(data[offset + 7] ^ (STUN_MAGIC_COOKIE & 0xFF));
                            mappedAddress = new IPAddress(ip);
                        }
                        else
                        {
                            // Plain values
                            mappedPort = (data[offset + 2] << 8) | data[offset + 3];
                            mappedAddress = new IPAddress(new[] { data[offset + 4], data[offset + 5], data[offset + 6], data[offset + 7] });
                        }
                        break; // Got what we need
                    }
                }
            }
            
            // Move to next attribute (aligned to 4 bytes)
            offset += attrLength;
            if (attrLength % 4 != 0)
            {
                offset += 4 - (attrLength % 4);
            }
        }
        
        if (mappedAddress != null)
        {
            return new StunResult
            {
                Success = true,
                PublicIp = mappedAddress,
                PublicPort = mappedPort,
                NatType = NatType.Unknown // Will be refined by DetectNatTypeAsync
            };
        }
        
        return new StunResult { Success = false, Error = "No mapped address in response" };
    }
}
