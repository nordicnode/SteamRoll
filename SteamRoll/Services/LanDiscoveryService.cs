using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace SteamRoll.Services;

/// <summary>
/// Provides UDP-based LAN discovery for finding other SteamRoll instances on the network.
/// Uses broadcast to announce presence and discover peers.
/// Supports both IPv4 broadcast and IPv6 link-local multicast.
/// </summary>
public class LanDiscoveryService : IDisposable
{
    private const int DISCOVERY_PORT = AppConstants.DEFAULT_DISCOVERY_PORT;
    private const int ANNOUNCE_INTERVAL_MS = AppConstants.ANNOUNCE_INTERVAL_MS;
    private const int ANNOUNCE_JITTER_MS = AppConstants.ANNOUNCE_JITTER_MS;
    private const int ANNOUNCE_STARTUP_JITTER_MS = AppConstants.ANNOUNCE_STARTUP_JITTER_MS;
    private const int PEER_TIMEOUT_MS = AppConstants.PEER_TIMEOUT_MS;
    private const string PROTOCOL_MAGIC = ProtocolConstants.DISCOVERY_MAGIC;
    
    // IPv6 link-local all-nodes multicast address
    private static readonly IPAddress IPv6MulticastAddress = IPAddress.Parse("ff02::1");

    private static readonly Random _jitterRandom = new();

    private UdpClient? _udpClient;       // IPv4
    private UdpClient? _udpClientV6;     // IPv6
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
    /// Supports both IPv4 broadcast and IPv6 link-local multicast.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        try
        {
            _cts = new CancellationTokenSource();
            
            // IPv4: Broadcast-based discovery
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_PORT));
            _udpClient.EnableBroadcast = true;

            // IPv6: Multicast-based discovery (best-effort, doesn't fail if IPv6 unavailable)
            try
            {
                _udpClientV6 = new UdpClient(AddressFamily.InterNetworkV6);
                _udpClientV6.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClientV6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, DISCOVERY_PORT));
                
                // Join link-local multicast group on all interfaces
                _udpClientV6.JoinMulticastGroup(IPv6MulticastAddress);
                
                LogService.Instance.Info("IPv6 discovery enabled", "LanDiscovery");
            }
            catch (Exception ex)
            {
                LogService.Instance.Debug($"IPv6 discovery not available: {ex.Message}", "LanDiscovery");
                _udpClientV6?.Dispose();
                _udpClientV6 = null;
            }

            IsRunning = true;

            // Start background tasks
            _ = ListenAsync(_cts.Token);
            if (_udpClientV6 != null)
            {
                _ = ListenAsyncV6(_cts.Token);
            }
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
        
        // Cleanup IPv6 client
        if (_udpClientV6 != null)
        {
            try { _udpClientV6.DropMulticastGroup(IPv6MulticastAddress); } catch { }
            _udpClientV6.Close();
            _udpClientV6.Dispose();
            _udpClientV6 = null;
        }

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
                    case MessageType.SwarmQuery:
                        HandleSwarmQuery(peerIp, message);
                        break;
                    case MessageType.SwarmResponse:
                        HandleSwarmResponse(peerIp, message);
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
    
    /// <summary>
    /// Listens for IPv6 multicast discovery messages.
    /// </summary>
    private async Task ListenAsyncV6(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClientV6 != null)
        {
            try
            {
                var result = await _udpClientV6.ReceiveAsync(ct);
                var json = Encoding.UTF8.GetString(result.Buffer);
                
                var message = JsonSerializer.Deserialize<DiscoveryMessage>(json);
                if (message == null || message.Magic != PROTOCOL_MAGIC) continue;

                // Ignore our own broadcasts
                if (message.HostName == _localHostName) continue;

                // Extract IPv6 address (may be zone-scoped like fe80::1%eth0)
                var peerIp = result.RemoteEndPoint.Address.ToString();
                // Remove zone ID for consistent peer tracking
                var zoneSeparator = peerIp.IndexOf('%');
                if (zoneSeparator > 0)
                {
                    peerIp = peerIp.Substring(0, zoneSeparator);
                }

                switch (message.Type)
                {
                    case MessageType.Announce:
                        HandleAnnounce(peerIp, message);
                        break;
                    case MessageType.TransferRequest:
                        HandleTransferRequest(peerIp, message);
                        break;
                    case MessageType.SaveSyncOffer:
                        HandleSaveSyncOffer(peerIp, message);
                        break;
                    case MessageType.SaveSyncRequest:
                        HandleSaveSyncRequest(peerIp, message);
                        break;
                    case MessageType.SwarmQuery:
                        HandleSwarmQuery(peerIp, message);
                        break;
                    case MessageType.SwarmResponse:
                        HandleSwarmResponse(peerIp, message);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogService.Instance.Debug($"IPv6 Listen error: {ex.Message}", "LanDiscovery");
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
    /// Raised when a peer responds to a swarm query with availability info.
    /// </summary>
    public event EventHandler<SwarmPeerResponseEventArgs>? SwarmPeerResponseReceived;

    /// <summary>
    /// Callback to check if we have a specific game for swarm queries.
    /// Returns (hasGame, fileSize) tuple.
    /// </summary>
    public Func<string, (bool HasGame, long FileSize)>? CheckGameAvailabilityCallback { get; set; }

    /// <summary>
    /// Device ID for swarm peer identification.
    /// </summary>
    public Guid SwarmPeerId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Advertised upload speed in bytes/sec for swarm load balancing.
    /// </summary>
    public long SwarmUploadSpeedBytesPerSec { get; set; }

    private async void HandleSwarmQuery(string ip, DiscoveryMessage message)
    {
        try
        {
            if (string.IsNullOrEmpty(message.GameName)) return;

            // Check if we have the requested game
            var availability = CheckGameAvailabilityCallback?.Invoke(message.GameName);
            if (availability?.HasGame != true) return;

            // Respond that we have the file
            var response = new DiscoveryMessage
            {
                Type = MessageType.SwarmResponse,
                Magic = PROTOCOL_MAGIC,
                HostName = _localHostName,
                TransferPort = _transferPort,
                GameName = message.GameName,
                GameSize = availability.Value.FileSize,
                FileHash = message.FileHash,
                SwarmPeerId = SwarmPeerId,
                SwarmUploadSpeed = SwarmUploadSpeedBytesPerSec
            };

            var json = JsonSerializer.Serialize(response);
            var data = Encoding.UTF8.GetBytes(json);

            using var client = new UdpClient();
            await client.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Parse(ip), DISCOVERY_PORT));

            LogService.Instance.Debug($"Sent swarm response to {ip} for {message.GameName}", "LanDiscovery");
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Failed to send swarm response: {ex.Message}", "LanDiscovery");
        }
    }

    private void HandleSwarmResponse(string ip, DiscoveryMessage message)
    {
        try
        {
            var eventArgs = new SwarmPeerResponseEventArgs
            {
                PeerId = message.SwarmPeerId ?? Guid.NewGuid(),
                HostName = message.HostName ?? "Unknown",
                IpAddress = ip,
                Port = message.TransferPort,
                GameName = message.GameName ?? "",
                FileSize = message.GameSize,
                UploadSpeedBytesPerSec = message.SwarmUploadSpeed
            };

            SwarmPeerResponseReceived?.Invoke(this, eventArgs);
            LogService.Instance.Debug($"Received swarm response from {ip}: {message.GameName}", "LanDiscovery");
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Failed to handle swarm response: {ex.Message}", "LanDiscovery");
        }
    }

    /// <summary>
    /// Broadcasts a swarm query to find peers that have a specific game.
    /// Listen to SwarmPeerResponseReceived for responses.
    /// </summary>
    /// <param name="gameName">Name of the game to find.</param>
    /// <param name="fileHash">Optional file hash for verification.</param>
    public async Task BroadcastSwarmQueryAsync(string gameName, string? fileHash = null)
    {
        try
        {
            if (_udpClient == null) return;

            var message = new DiscoveryMessage
            {
                Type = MessageType.SwarmQuery,
                Magic = PROTOCOL_MAGIC,
                HostName = _localHostName,
                TransferPort = _transferPort,
                GameName = gameName,
                FileHash = fileHash,
                SwarmPeerId = SwarmPeerId
            };

            var json = JsonSerializer.Serialize(message);
            var data = Encoding.UTF8.GetBytes(json);

            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT);
            await _udpClient.SendAsync(data, data.Length, broadcastEndpoint);

            // Also send to IPv6 multicast if available
            if (_udpClientV6 != null)
            {
                try
                {
                    var multicastEndpoint = new IPEndPoint(IPv6MulticastAddress, DISCOVERY_PORT);
                    await _udpClientV6.SendAsync(data, data.Length, multicastEndpoint);
                }
                catch { /* IPv6 may not be available */ }
            }

            LogService.Instance.Debug($"Broadcast swarm query for {gameName}", "LanDiscovery");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to broadcast swarm query: {ex.Message}", ex, "LanDiscovery");
        }
    }

    /// <summary>
    /// Discovers all peers that have a specific game for swarm download.
    /// Waits for responses for the specified timeout.
    /// </summary>
    /// <param name="gameName">Name of the game to find.</param>
    /// <param name="timeout">How long to wait for responses.</param>
    /// <returns>List of peers that have the game.</returns>
    public async Task<List<Transfer.SwarmPeerInfo>> DiscoverSwarmPeersAsync(string gameName, TimeSpan? timeout = null)
    {
        var waitTime = timeout ?? TimeSpan.FromSeconds(2);
        var peers = new List<Transfer.SwarmPeerInfo>();

        void OnResponse(object? sender, SwarmPeerResponseEventArgs e)
        {
            if (e.GameName == gameName)
            {
                peers.Add(new Transfer.SwarmPeerInfo
                {
                    PeerId = e.PeerId,
                    IpAddress = e.IpAddress,
                    Port = e.Port,
                    DeviceName = e.HostName,
                    AdvertisedSpeedBytesPerSec = e.UploadSpeedBytesPerSec,
                    DiscoveredAt = DateTime.UtcNow,
                    IsAvailable = true
                });
            }
        }

        SwarmPeerResponseReceived += OnResponse;
        try
        {
            await BroadcastSwarmQueryAsync(gameName);
            await Task.Delay(waitTime);
        }
        finally
        {
            SwarmPeerResponseReceived -= OnResponse;
        }

        LogService.Instance.Info($"Discovered {peers.Count} swarm peers for {gameName}", "LanDiscovery");
        return peers;
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
        
        // Use PeriodicTimer for more efficient scheduling than while + Task.Delay
        // PeriodicTimer avoids async state machine overhead on each iteration
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ANNOUNCE_INTERVAL_MS));
        
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_udpClient == null) break;
                
                try
                {
                    // Add per-tick jitter to prevent synchronized broadcasts across clients
                    var jitter = _jitterRandom.Next(0, ANNOUNCE_JITTER_MS);
                    if (jitter > 0)
                    {
                        await Task.Delay(jitter, ct);
                    }
                    
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

                    // Broadcast to all network interfaces (IPv4)
                    var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT);
                    await _udpClient.SendAsync(data, data.Length, broadcastEndpoint);
                    
                    // Also send to IPv6 multicast if available
                    if (_udpClientV6 != null)
                    {
                        try
                        {
                            var multicastEndpoint = new IPEndPoint(IPv6MulticastAddress, DISCOVERY_PORT);
                            await _udpClientV6.SendAsync(data, data.Length, multicastEndpoint);
                        }
                        catch
                        {
                            // IPv6 send may fail on some networks, continue with IPv4
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogService.Instance.Debug($"Announce error: {ex.Message}", "LanDiscovery");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private async Task CleanupPeersAsync(CancellationToken ct)
    {
        // Use PeriodicTimer for more efficient scheduling than while + Task.Delay
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
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
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
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

