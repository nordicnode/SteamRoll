using System.Collections.ObjectModel;
using System.Windows.Data;
using SteamRoll.Services;
using SteamRoll.Services.Transfer;

namespace SteamRoll.ViewModels;

/// <summary>
/// ViewModel for network peer discovery and management.
/// Extracted from MainViewModel to improve separation of concerns.
/// </summary>
public class NetworkViewModel : ViewModelBase
{
    private readonly LanDiscoveryService _lanDiscoveryService;
    private readonly TransferService _transferService;
    private MeshLibraryService? _meshLibraryService;
    
    private int _peerCount;
    private string _statusText = "";

    /// <summary>
    /// Observable collection of discovered network peers.
    /// </summary>
    public ObservableCollection<PeerInfo> NetworkPeers { get; } = new();

    public NetworkViewModel(LanDiscoveryService lanDiscoveryService, TransferService transferService)
    {
        _lanDiscoveryService = lanDiscoveryService;
        _transferService = transferService;
        
        // Enable thread-safe collection access for UI binding
        BindingOperations.EnableCollectionSynchronization(NetworkPeers, new object());
        
        // Subscribe to discovery events
        _lanDiscoveryService.PeerDiscovered += (_, peer) => HandlePeerDiscovered(peer);
        _lanDiscoveryService.PeerLost += (_, peer) => HandlePeerLost(peer);
    }

    /// <summary>
    /// Number of connected peers on the network.
    /// </summary>
    public int PeerCount
    {
        get => _peerCount;
        set
        {
            if (SetProperty(ref _peerCount, value))
                OnPropertyChanged(nameof(HasPeers));
        }
    }

    /// <summary>
    /// Whether any peers are connected.
    /// </summary>
    public bool HasPeers => PeerCount > 0;

    /// <summary>
    /// Status text for network operations.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>
    /// Access to mesh library service.
    /// </summary>
    public MeshLibraryService? MeshLibraryService => _meshLibraryService;

    /// <summary>
    /// Raised when a peer is discovered (for toast notifications).
    /// </summary>
    public event EventHandler<PeerInfo>? PeerDiscoveredNotification;

    /// <summary>
    /// Raised when network status changes.
    /// </summary>
    public event EventHandler? NetworkStatusChanged;

    /// <summary>
    /// Updates the peer count from the discovery service.
    /// </summary>
    public void UpdatePeerCount()
    {
        PeerCount = _lanDiscoveryService.GetPeers().Count;
    }

    /// <summary>
    /// Gets the list of peers from the discovery service and updates the collection.
    /// </summary>
    public void RefreshNetworkPeers()
    {
        NetworkPeers.Clear();
        foreach (var peer in _lanDiscoveryService.GetPeers())
        {
            NetworkPeers.Add(peer);
        }
        PeerCount = NetworkPeers.Count;
    }

    /// <summary>
    /// Handles peer discovered from LAN discovery service.
    /// </summary>
    public void HandlePeerDiscovered(PeerInfo peer)
    {
        UpdatePeerCount();
        RefreshNetworkPeers();
        StatusText = $"🔗 Found peer: {peer.HostName}";
        PeerDiscoveredNotification?.Invoke(this, peer);
        NetworkStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Handles peer lost from LAN discovery service.
    /// </summary>
    public void HandlePeerLost(PeerInfo peer)
    {
        UpdatePeerCount();
        RefreshNetworkPeers();
        NetworkStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets peers available for transfer.
    /// </summary>
    public List<PeerInfo> GetAvailablePeers() => _lanDiscoveryService.GetPeers();

    /// <summary>
    /// Whether any peers are available.
    /// </summary>
    public bool HasAvailablePeers => _lanDiscoveryService.GetPeers().Count > 0;

    /// <summary>
    /// Initializes the mesh library service after window is loaded.
    /// </summary>
    public void InitializeMeshLibrary()
    {
        _meshLibraryService = new MeshLibraryService(_lanDiscoveryService, _transferService);
    }

    /// <summary>
    /// Manually adds a peer by IP address.
    /// </summary>
    public void AddManualPeer(string ipAddress, int port, string? displayName = null,
        bool persist = false, SettingsService? settingsService = null)
    {
        _lanDiscoveryService.AddManualPeer(ipAddress, port, displayName, persist, settingsService);
        RefreshNetworkPeers();
    }

    /// <summary>
    /// Removes a manual peer.
    /// </summary>
    public void RemoveManualPeer(string ipAddress, int port, SettingsService? settingsService = null)
    {
        _lanDiscoveryService.RemoveManualPeer(ipAddress, port, settingsService);
        RefreshNetworkPeers();
    }

    /// <summary>
    /// Restores persistent peers from settings.
    /// </summary>
    public void RestorePersistentPeers(SettingsService settingsService)
    {
        _lanDiscoveryService.RestorePersistentPeers(settingsService);
        RefreshNetworkPeers();
    }
}
