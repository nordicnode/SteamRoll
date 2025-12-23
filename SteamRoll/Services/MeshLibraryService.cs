using System.Collections.Concurrent;
using SteamRoll.Models;

namespace SteamRoll.Services;

/// <summary>
/// Manages the "Mesh Library" - aggregating games available from peers on the LAN.
/// This allows users to see what games are available across the network and request transfers.
/// </summary>
public class MeshLibraryService : IDisposable
{
    private readonly LanDiscoveryService _discoveryService;
    private readonly ConcurrentDictionary<string, List<PeerGameInfo>> _peerGames = new();
    private readonly ConcurrentDictionary<int, List<PeerGameInfo>> _gamesByAppId = new();
    private readonly object _updateLock = new();

    /// <summary>
    /// Raised when the network library changes (peer added/removed games).
    /// </summary>
    public event EventHandler? NetworkLibraryChanged;

    /// <summary>
    /// Raised when a game list is received from a peer.
    /// </summary>
    public event EventHandler<string>? PeerGameListReceived;

    public MeshLibraryService(LanDiscoveryService discoveryService)
    {
        _discoveryService = discoveryService;
        _discoveryService.PeerDiscovered += OnPeerDiscovered;
        _discoveryService.PeerLost += OnPeerLost;
        _discoveryService.GameListReceived += OnGameListReceived;
    }

    /// <summary>
    /// Gets all games available on the network, grouped by AppId.
    /// </summary>
    public Dictionary<int, List<PeerGameInfo>> GetNetworkGames()
    {
        lock (_updateLock)
        {
            return _gamesByAppId.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());
        }
    }

    /// <summary>
    /// Gets all unique games available on the network.
    /// Returns one PeerGameInfo per AppId (from the first available peer).
    /// </summary>
    public List<PeerGameInfo> GetUniqueNetworkGames()
    {
        lock (_updateLock)
        {
            return _gamesByAppId.Values
                .Where(list => list.Count > 0)
                .Select(list => list.First())
                .ToList();
        }
    }

    /// <summary>
    /// Gets all peers that have a specific game.
    /// </summary>
    public List<PeerGameInfo> GetPeersWithGame(int appId)
    {
        lock (_updateLock)
        {
            return _gamesByAppId.TryGetValue(appId, out var peers) 
                ? peers.ToList() 
                : new List<PeerGameInfo>();
        }
    }

    /// <summary>
    /// Checks if a game is available on the network.
    /// </summary>
    public bool IsGameAvailableOnNetwork(int appId)
    {
        return _gamesByAppId.ContainsKey(appId) && _gamesByAppId[appId].Count > 0;
    }

    /// <summary>
    /// Gets the count of peers that have a specific game.
    /// </summary>
    public int GetPeerCountForGame(int appId)
    {
        return _gamesByAppId.TryGetValue(appId, out var peers) ? peers.Count : 0;
    }

    /// <summary>
    /// Requests game lists from all known peers.
    /// </summary>
    public async Task RefreshNetworkLibraryAsync()
    {
        var peers = _discoveryService.GetPeers();
        var tasks = peers.Select(peer => _discoveryService.RequestGameListAsync(peer));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Requests game list from a specific peer.
    /// </summary>
    public Task RequestGameListFromPeerAsync(PeerInfo peer)
    {
        return _discoveryService.RequestGameListAsync(peer);
    }

    private void OnPeerDiscovered(object? sender, PeerInfo peer)
    {
        // Request game list from the newly discovered peer
        _ = _discoveryService.RequestGameListAsync(peer);
    }

    private void OnPeerLost(object? sender, PeerInfo peer)
    {
        var peerId = peer.Id;
        
        lock (_updateLock)
        {
            if (_peerGames.TryRemove(peerId, out var removedGames))
            {
                // Remove this peer's games from the AppId index
                foreach (var game in removedGames)
                {
                    if (_gamesByAppId.TryGetValue(game.AppId, out var peerList))
                    {
                        peerList.RemoveAll(g => g.PeerIp == peer.IpAddress && g.PeerPort == peer.TransferPort);
                        if (peerList.Count == 0)
                        {
                            _gamesByAppId.TryRemove(game.AppId, out _);
                        }
                    }
                }
            }
        }

        NetworkLibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnGameListReceived(object? sender, GameListReceivedEventArgs e)
    {
        var peerId = $"{e.PeerIp}:{e.PeerPort}";
        
        lock (_updateLock)
        {
            // Remove old games from this peer
            if (_peerGames.TryGetValue(peerId, out var oldGames))
            {
                foreach (var game in oldGames)
                {
                    if (_gamesByAppId.TryGetValue(game.AppId, out var peerList))
                    {
                        peerList.RemoveAll(g => g.PeerIp == e.PeerIp && g.PeerPort == e.PeerPort);
                        if (peerList.Count == 0)
                        {
                            _gamesByAppId.TryRemove(game.AppId, out _);
                        }
                    }
                }
            }

            // Convert NetworkGameInfo to PeerGameInfo
            var peerGames = e.Games.Select(g => new PeerGameInfo
            {
                AppId = g.AppId,
                Name = g.Name,
                SizeBytes = g.SizeBytes,
                BuildId = g.BuildId,
                PeerHostName = e.PeerHostName,
                PeerIp = e.PeerIp,
                PeerPort = e.PeerPort
            }).ToList();

            // Update peer games
            _peerGames[peerId] = peerGames;

            // Update AppId index
            foreach (var game in peerGames)
            {
                var list = _gamesByAppId.GetOrAdd(game.AppId, _ => new List<PeerGameInfo>());
                list.Add(game);
            }
        }

        LogService.Instance.Info($"Received game list from {e.PeerHostName}: {e.Games.Count} games", "MeshLibrary");
        PeerGameListReceived?.Invoke(this, e.PeerHostName);
        NetworkLibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _discoveryService.PeerDiscovered -= OnPeerDiscovered;
        _discoveryService.PeerLost -= OnPeerLost;
        _discoveryService.GameListReceived -= OnGameListReceived;
    }
}

/// <summary>
/// Event args for when a game list is received from a peer.
/// </summary>
public class GameListReceivedEventArgs : EventArgs
{
    public string PeerHostName { get; set; } = string.Empty;
    public string PeerIp { get; set; } = string.Empty;
    public int PeerPort { get; set; }
    public List<NetworkGameInfo> Games { get; set; } = new();
}
