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
    private const int ANNOUNCE_JITTER_MS = AppConstants.ANNOUNCE_JITTER_MS;
    private const int ANNOUNCE_STARTUP_JITTER_MS = AppConstants.ANNOUNCE_STARTUP_JITTER_MS;
    private const int PEER_TIMEOUT_MS = AppConstants.PEER_TIMEOUT_MS;
    private const string PROTOCOL_MAGIC = ProtocolConstants.DISCOVERY_MAGIC;

    private static readonly Random _jitterRandom = new();

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();
    private string _localHostName;
    private int _transferPort;
    private volatile int _localPackagedGameCount;

    public event EventHandler<PeerInfo>? PeerDiscovered;
    public event EventHandler<PeerInfo>? PeerLost;
    public event EventHandler<TransferRequest>? TransferRequested;
    public event EventHandler<GameListReceivedEventArgs>? GameListReceived;

    /// <summary>
    /// Callback to get the local packaged games list for sharing with peers.
    /// </summary>
    public Func<List<Models.NetworkGameInfo>>? GetLocalGamesCallback { get; set; }

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
        _transferPort = AppConstants.DEFAULT_TRANSFER_PORT; // Default transfer port
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
    /// Safe to call multiple times.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS was already disposed, safe to ignore
        }
        _cts = null;
        
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
    /// Manually adds a peer by IP address.
    /// Used when UDP broadcast discovery is blocked or across subnets.
    /// </summary>
    /// <param name="ipAddress">IP address of the peer</param>
    /// <param name="port">Transfer port (default 27051)</param>
    /// <param name="displayName">Optional friendly name for the peer</param>
    /// <param name="persist">If true, saves to settings for automatic restoration on startup</param>
    /// <param name="settingsService">Required if persist is true</param>
    public void AddManualPeer(string ipAddress, int port = AppConstants.DEFAULT_TRANSFER_PORT, 
        string? displayName = null, bool persist = false, SettingsService? settingsService = null)
    {
        try
        {
            var peerId = $"{ipAddress}:{port}";
            if (_peers.ContainsKey(peerId)) return;

            var peer = new PeerInfo
            {
                Id = peerId,
                HostName = displayName ?? ipAddress, // Will be updated if/when we get a real announce
                IpAddress = ipAddress,
                TransferPort = port,
                LastSeen = DateTime.Now,
                PackagedGameCount = 0,
                IsManual = true
            };

            _peers[peerId] = peer;
            LogService.Instance.Info($"Added manual peer: {displayName ?? ipAddress} ({ipAddress})", "LanDiscovery");
            PeerDiscovered?.Invoke(this, peer);

            // Persist to settings if requested and setting is enabled
            if (persist && settingsService != null && settingsService.Settings.RememberDirectConnectPeers)
            {
                var existing = settingsService.Settings.DirectConnectPeers
                    .FirstOrDefault(p => p.IpAddress == ipAddress && p.Port == port);
                if (existing == null)
                {
                    settingsService.Settings.DirectConnectPeers.Add(new DirectConnectPeer
                    {
                        IpAddress = ipAddress,
                        Port = port,
                        DisplayName = displayName
                    });
                    settingsService.Save();
                    LogService.Instance.Info($"Persisted direct connect peer: {ipAddress}:{port}", "LanDiscovery");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to add manual peer {ipAddress}: {ex.Message}", ex, "LanDiscovery");
        }
    }

    /// <summary>
    /// Removes a manual peer by IP address and optionally removes from persistent settings.
    /// </summary>
    public void RemoveManualPeer(string ipAddress, int port = AppConstants.DEFAULT_TRANSFER_PORT, 
        SettingsService? settingsService = null)
    {
        try
        {
            var peerId = $"{ipAddress}:{port}";
            if (_peers.TryRemove(peerId, out var removed))
            {
                LogService.Instance.Info($"Removed manual peer: {ipAddress}", "LanDiscovery");
                PeerLost?.Invoke(this, removed);
            }

            // Remove from persistent settings if present
            if (settingsService != null)
            {
                var existing = settingsService.Settings.DirectConnectPeers
                    .FirstOrDefault(p => p.IpAddress == ipAddress && p.Port == port);
                if (existing != null)
                {
                    settingsService.Settings.DirectConnectPeers.Remove(existing);
                    settingsService.Save();
                    LogService.Instance.Info($"Removed persisted direct connect peer: {ipAddress}:{port}", "LanDiscovery");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to remove manual peer {ipAddress}: {ex.Message}", ex, "LanDiscovery");
        }
    }

    /// <summary>
    /// Restores all persistent direct connect peers from settings.
    /// Call this after Start() to load saved peers.
    /// Only restores if RememberDirectConnectPeers is enabled.
    /// </summary>
    public void RestorePersistentPeers(SettingsService settingsService)
    {
        if (!settingsService.Settings.RememberDirectConnectPeers)
        {
            LogService.Instance.Debug("Direct connect peer persistence is disabled", "LanDiscovery");
            return;
        }
        
        foreach (var peer in settingsService.Settings.DirectConnectPeers)
        {
            AddManualPeer(peer.IpAddress, peer.Port, peer.DisplayName, persist: false);
        }
        
        if (settingsService.Settings.DirectConnectPeers.Count > 0)
        {
            LogService.Instance.Info($"Restored {settingsService.Settings.DirectConnectPeers.Count} persistent peers", "LanDiscovery");
        }
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
                    case MessageType.GameListRequest:
                        // DEPRECATED: UDP game lists can exceed MTU limits and fragment
                        // Use TCP-based TransferService.RequestLibraryListAsync instead
                        LogService.Instance.Debug($"Ignoring UDP GameListRequest from {peerIp} - use TCP", "LanDiscovery");
                        break;
                    case MessageType.GameListResponse:
                        // DEPRECATED: Large game lists will be dropped/fragmented over UDP
                        LogService.Instance.Debug($"Ignoring UDP GameListResponse from {peerIp} - use TCP", "LanDiscovery");
                        break;
                    case MessageType.SaveSyncOffer:
                        HandleSaveSyncOffer(peerIp, message);
                        break;
                    case MessageType.SaveSyncRequest:
                        HandleSaveSyncRequest(peerIp, message);
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

    /// <summary>
    /// [DEPRECATED] Handles UDP game list requests. Large payloads will fragment.
    /// Use TCP-based TransferService.RequestLibraryListAsync instead.
    /// </summary>
    [Obsolete("UDP game lists can exceed MTU limits. Use TransferService.RequestLibraryListAsync (TCP) instead.")]
    private async void HandleGameListRequest(string ip, DiscoveryMessage message)
    {
        // DEPRECATED: This method is kept for backwards compatibility but logs a warning
        LogService.Instance.Warning($"UDP GameListRequest received from {ip} - consider using TCP for reliability", "LanDiscovery");
        
        try
        {
            var games = GetLocalGamesCallback?.Invoke() ?? new List<Models.NetworkGameInfo>();
            
            // Only respond if game list is small enough to fit in UDP packet
            // Max safe UDP payload is ~1400 bytes to avoid fragmentation
            var response = new DiscoveryMessage
            {
                Type = MessageType.GameListResponse,
                Magic = PROTOCOL_MAGIC,
                HostName = _localHostName,
                TransferPort = _transferPort,
                PackagedGameCount = games.Count,
                GameListJson = JsonSerializer.Serialize(games)
            };

            var json = JsonSerializer.Serialize(response);
            
            // Check if payload exceeds safe UDP size
            if (Encoding.UTF8.GetByteCount(json) > 1400)
            {
                LogService.Instance.Warning($"Game list too large for UDP ({games.Count} games), skipping. Use TCP.", "LanDiscovery");
                return;
            }
            
            var data = Encoding.UTF8.GetBytes(json);

            using var client = new UdpClient();
            await client.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Parse(ip), DISCOVERY_PORT));
            
            LogService.Instance.Debug($"Sent game list to {ip}: {games.Count} games", "LanDiscovery");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to send game list: {ex.Message}", ex, "LanDiscovery");
        }
    }

    private void HandleGameListResponse(string ip, DiscoveryMessage message)
    {
        try
        {
            if (string.IsNullOrEmpty(message.GameListJson)) return;

            var games = JsonSerializer.Deserialize<List<Models.NetworkGameInfo>>(message.GameListJson) 
                ?? new List<Models.NetworkGameInfo>();

            var eventArgs = new GameListReceivedEventArgs
            {
                PeerHostName = message.HostName ?? "Unknown",
                PeerIp = ip,
                PeerPort = message.TransferPort,
                Games = games
            };

            GameListReceived?.Invoke(this, eventArgs);
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to parse game list from {ip}: {ex.Message}", ex, "LanDiscovery");
        }
    }

    /// <summary>
    /// [DEPRECATED] Requests the game list from a specific peer via UDP.
    /// Use TCP-based TransferService.RequestLibraryListAsync instead for reliability.
    /// </summary>
    [Obsolete("UDP game lists can exceed MTU limits. Use TransferService.RequestLibraryListAsync (TCP) instead.")]
    public async Task RequestGameListAsync(PeerInfo peer)
    {
        // Log deprecation warning
        LogService.Instance.Warning($"UDP RequestGameListAsync is deprecated - use TransferService.RequestLibraryListAsync for {peer.HostName}", "LanDiscovery");
        
        try
        {
            var request = new DiscoveryMessage
            {
                Type = MessageType.GameListRequest,
                Magic = PROTOCOL_MAGIC,
                HostName = _localHostName,
                TransferPort = _transferPort
            };

            var json = JsonSerializer.Serialize(request);
            var data = Encoding.UTF8.GetBytes(json);

            using var client = new UdpClient();
            await client.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Parse(peer.IpAddress), DISCOVERY_PORT));
            
            LogService.Instance.Debug($"Requested game list from {peer.HostName}", "LanDiscovery");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to request game list: {ex.Message}", ex, "LanDiscovery");
        }
    }

    /// <summary>
    /// Raised when a save sync offer is received from a peer.
    /// </summary>
    public event EventHandler<SaveSyncOfferEventArgs>? SaveSyncOfferReceived;

    private void HandleSaveSyncOffer(string ip, DiscoveryMessage message)
    {
        try
        {
            if (string.IsNullOrEmpty(message.SaveSyncJson)) return;

            var offer = JsonSerializer.Deserialize<SaveSyncOffer>(message.SaveSyncJson);
            if (offer == null) return;

            var eventArgs = new SaveSyncOfferEventArgs
            {
                PeerHostName = message.HostName ?? "Unknown",
                PeerIp = ip,
                PeerPort = message.TransferPort,
                Offer = offer
            };

            SaveSyncOfferReceived?.Invoke(this, eventArgs);
            LogService.Instance.Debug($"Received save sync offer from {eventArgs.PeerHostName} for AppId {offer.AppId}", "LanDiscovery");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to parse save sync offer from {ip}: {ex.Message}", ex, "LanDiscovery");
        }
    }

    private void HandleSaveSyncRequest(string ip, DiscoveryMessage message)
    {
        // Peer is requesting our save data for a specific game
        // This would trigger the SaveSyncService to handle the actual transfer
        LogService.Instance.Debug($"Received save sync request from {ip}", "LanDiscovery");
    }

    /// <summary>
    /// Broadcasts a save sync offer to all peers.
    /// </summary>
    public async Task BroadcastSaveSyncOfferAsync(SaveSyncOffer offer)
    {
        try
        {
            if (_udpClient == null) return;

            var message = new DiscoveryMessage
            {
                Type = MessageType.SaveSyncOffer,
                Magic = PROTOCOL_MAGIC,
                HostName = _localHostName,
                TransferPort = _transferPort,
                SaveSyncJson = JsonSerializer.Serialize(offer)
            };

            var json = JsonSerializer.Serialize(message);
            var data = Encoding.UTF8.GetBytes(json);

            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT);
            await _udpClient.SendAsync(data, data.Length, broadcastEndpoint);
            
            LogService.Instance.Debug($"Broadcast save sync offer for AppId {offer.AppId}", "LanDiscovery");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to broadcast save sync offer: {ex.Message}", ex, "LanDiscovery");
        }
    }

    private async Task AnnounceAsync(CancellationToken ct)
    {
        // Add random startup delay to prevent burst of announcements when multiple clients start together
        var startupDelay = _jitterRandom.Next(0, ANNOUNCE_STARTUP_JITTER_MS);
        LogService.Instance.Debug($"Announce startup delay: {startupDelay}ms", "LanDiscovery");
        await Task.Delay(startupDelay, ct);
        
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

                // Add jitter to prevent multiple clients from broadcasting simultaneously
                var jitter = _jitterRandom.Next(0, ANNOUNCE_JITTER_MS);
                await Task.Delay(ANNOUNCE_INTERVAL_MS + jitter, ct);
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

                // Only clean up non-manual peers
                var stale = _peers.Where(p => !p.Value.IsManual && (now - p.Value.LastSeen).TotalMilliseconds > PEER_TIMEOUT_MS)
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
    public bool IsManual { get; set; }

    public string DisplayName => IsManual ? $"{HostName} (Manual)" : $"{HostName} ({IpAddress})";
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
    SaveSyncRequest
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
