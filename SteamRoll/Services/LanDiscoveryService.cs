using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SteamRoll.Services;

/// <summary>
/// Provides UDP-based LAN discovery for finding other SteamRoll instances on the network.
/// Uses broadcast to announce presence and discover peers.
/// </summary>
public class LanDiscoveryService : IDisposable
{
    private const int DISCOVERY_PORT = AppConstants.DEFAULT_DISCOVERY_PORT;
    private const int ANNOUNCE_INTERVAL_MS = AppConstants.ANNOUNCE_INTERVAL_MS;
    private const int PEER_TIMEOUT_MS = AppConstants.PEER_TIMEOUT_MS;
    private const string PROTOCOL_MAGIC = "STEAMROLL_V1";


    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();
    private string _localHostName;
    private int _transferPort;
    private int _localPackagedGameCount;

    public event EventHandler<PeerInfo>? PeerDiscovered;
    public event EventHandler<PeerInfo>? PeerLost;
    public event EventHandler<TransferRequest>? TransferRequested;

    public bool IsRunning { get; private set; }
    
    /// <summary>
    /// Gets or sets the count of locally packaged games to announce to peers.
    /// </summary>
    public int LocalPackagedGameCount
    {
        get => _localPackagedGameCount;
        set => _localPackagedGameCount = value;
    }

    public LanDiscoveryService()
    {
        _localHostName = Environment.MachineName;
        _transferPort = 27051; // Default transfer port
    }


    /// <summary>
    /// Starts the discovery service - begins broadcasting presence and listening for peers.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        try
        {
            _cts = new CancellationTokenSource();
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_PORT));
            _udpClient.EnableBroadcast = true;

            IsRunning = true;

            // Start background tasks
            _ = ListenAsync(_cts.Token);
            _ = AnnounceAsync(_cts.Token);
            _ = CleanupPeersAsync(_cts.Token);

            LogService.Instance.Info($"LAN Discovery started on port {DISCOVERY_PORT}", "LanDiscovery");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to start LAN discovery: {ex.Message}", ex, "LanDiscovery");
            Stop();
        }
    }

    /// <summary>
    /// Stops the discovery service.
    /// </summary>
    public void Stop()
    {
        IsRunning = false;
        _cts?.Cancel();
        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;

        _peers.Clear();

        LogService.Instance.Info("LAN Discovery stopped", "LanDiscovery");
    }

    /// <summary>
    /// Gets a list of currently discovered peers.
    /// </summary>
    public List<PeerInfo> GetPeers()
    {
        return _peers.Values.ToList();
    }

    /// <summary>
    /// Sends a transfer request to a peer.
    /// </summary>
    public async Task<bool> SendTransferRequestAsync(PeerInfo peer, string gameName, long sizeBytes)
    {
        try
        {
            var request = new DiscoveryMessage
            {
                Type = MessageType.TransferRequest,
                HostName = _localHostName,
                TransferPort = _transferPort,
                GameName = gameName,
                GameSize = sizeBytes
            };

            var json = JsonSerializer.Serialize(request);
            var data = Encoding.UTF8.GetBytes(json);

            using var client = new UdpClient();
            await client.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Parse(peer.IpAddress), DISCOVERY_PORT));
            
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to send transfer request: {ex.Message}", ex, "LanDiscovery");
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct);
                var json = Encoding.UTF8.GetString(result.Buffer);
                
                var message = JsonSerializer.Deserialize<DiscoveryMessage>(json);
                if (message == null || message.Magic != PROTOCOL_MAGIC) continue;

                // Ignore our own broadcasts
                if (message.HostName == _localHostName) continue;

                var peerIp = result.RemoteEndPoint.Address.ToString();

                switch (message.Type)
                {
                    case MessageType.Announce:
                        HandleAnnounce(peerIp, message);
                        break;
                    case MessageType.TransferRequest:
                        HandleTransferRequest(peerIp, message);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogService.Instance.Debug($"Listen error: {ex.Message}", "LanDiscovery");
            }
        }
    }

    private void HandleAnnounce(string ip, DiscoveryMessage message)
    {
        var peerId = $"{ip}:{message.TransferPort}";
        
        var isNew = !_peers.ContainsKey(peerId);
        
        var peer = new PeerInfo
        {
            Id = peerId,
            HostName = message.HostName ?? "Unknown",
            IpAddress = ip,
            TransferPort = message.TransferPort,
            LastSeen = DateTime.Now,
            PackagedGameCount = message.PackagedGameCount
        };

        _peers[peerId] = peer;

        if (isNew)
        {
            LogService.Instance.Info($"Discovered peer: {peer.HostName} ({ip})", "LanDiscovery");
            PeerDiscovered?.Invoke(this, peer);
        }
    }

    private void HandleTransferRequest(string ip, DiscoveryMessage message)
    {
        var request = new TransferRequest
        {
            FromHostName = message.HostName ?? "Unknown",
            FromIp = ip,
            FromPort = message.TransferPort,
            GameName = message.GameName ?? "Unknown",
            SizeBytes = message.GameSize,
            RequestedAt = DateTime.Now
        };

        LogService.Instance.Info($"Transfer request from {request.FromHostName}: {request.GameName}", "LanDiscovery");
        TransferRequested?.Invoke(this, request);
    }

    private async Task AnnounceAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var message = new DiscoveryMessage
                {
                    Type = MessageType.Announce,
                    Magic = PROTOCOL_MAGIC,
                    HostName = _localHostName,
                    TransferPort = _transferPort,
                    PackagedGameCount = GetLocalPackagedGameCount()
                };

                var json = JsonSerializer.Serialize(message);
                var data = Encoding.UTF8.GetBytes(json);

                // Broadcast to all network interfaces
                var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT);
                await _udpClient.SendAsync(data, data.Length, broadcastEndpoint);

                await Task.Delay(ANNOUNCE_INTERVAL_MS, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogService.Instance.Debug($"Announce error: {ex.Message}", "LanDiscovery");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task CleanupPeersAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, ct);

                var now = DateTime.Now;
                List<PeerInfo> lostPeers = new();

                var stale = _peers.Where(p => (now - p.Value.LastSeen).TotalMilliseconds > PEER_TIMEOUT_MS)
                                  .Select(p => p.Key)
                                  .ToList();

                foreach (var key in stale)
                {
                    if (_peers.TryRemove(key, out var removed))
                    {
                        lostPeers.Add(removed);
                    }
                }

                foreach (var peer in lostPeers)
                {
                    LogService.Instance.Info($"Lost peer: {peer.HostName}", "LanDiscovery");
                    PeerLost?.Invoke(this, peer);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private int GetLocalPackagedGameCount()
    {
        return _localPackagedGameCount;
    }


    public void SetTransferPort(int port)
    {
        _transferPort = port;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}

/// <summary>
/// Information about a discovered peer on the network.
/// </summary>
public class PeerInfo
{
    public string Id { get; set; } = "";
    public string HostName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int TransferPort { get; set; }
    public DateTime LastSeen { get; set; }
    public int PackagedGameCount { get; set; }

    public string DisplayName => $"{HostName} ({IpAddress})";
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
    TransferReject
}

/// <summary>
/// Discovery message structure.
/// </summary>
public class DiscoveryMessage
{
    public string Magic { get; set; } = "STEAMROLL_V1";
    public MessageType Type { get; set; }
    public string? HostName { get; set; }
    public int TransferPort { get; set; }
    public int PackagedGameCount { get; set; }
    public string? GameName { get; set; }
    public long GameSize { get; set; }
}
