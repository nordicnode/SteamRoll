using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using SteamRoll.Services.Transfer;

namespace SteamRoll.Services;

/// <summary>
/// Handles TCP-based file transfers between SteamRoll instances.
/// Transfers entire game package folders with progress tracking.
/// </summary>
public class TransferService : IDisposable
{
    private const int DEFAULT_PORT = AppConstants.DEFAULT_TRANSFER_PORT;
    private const string PROTOCOL_MAGIC_V2 = ProtocolConstants.TRANSFER_MAGIC_V2;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _receiveBasePath;
    private readonly SettingsService? _settingsService;
    private readonly ConcurrentDictionary<Guid, Task> _activeTransfers = new();

    // Limits concurrent transfers to the same destination path to prevent file corruption
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly TransferSender _sender;
    private readonly TransferReceiver _receiver;
    private SwarmManager? _swarmManager;
    private LanDiscoveryService? _discoveryService;

    public event EventHandler<TransferProgress>? ProgressChanged;
    public event EventHandler<TransferResult>? TransferComplete;
    public event EventHandler<string>? Error;
    public event EventHandler<TransferApprovalEventArgs>? TransferApprovalRequested;

    // Events for receiver logic
    public event Func<List<Models.RemoteGame>>? LocalLibraryRequested;
    public event Func<string, string, int, Task>? PullPackageRequested;

    public bool IsListening { get; private set; }
    public int Port { get; private set; }

    public TransferService(string receiveBasePath, SettingsService? settingsService = null)
    {
        _receiveBasePath = receiveBasePath;
        _settingsService = settingsService;
        Port = DEFAULT_PORT;

        _sender = new TransferSender(settingsService);
        _sender.ProgressChanged += (s, e) => ProgressChanged?.Invoke(this, e);
        _sender.TransferComplete += (s, e) => TransferComplete?.Invoke(this, e);
        _sender.Error += (s, e) => Error?.Invoke(this, e);

        _receiver = new TransferReceiver(receiveBasePath, _pathLocks);
        _receiver.ProgressChanged += (s, e) => ProgressChanged?.Invoke(this, e);
        _receiver.TransferComplete += (s, e) => TransferComplete?.Invoke(this, e);
        _receiver.Error += (s, e) => Error?.Invoke(this, e);
        _receiver.TransferApprovalRequested += (s, e) => TransferApprovalRequested?.Invoke(this, e);

        // Relay receiver events
        _receiver.LocalLibraryRequested += () => LocalLibraryRequested?.Invoke() ?? new List<Models.RemoteGame>();
        _receiver.PullPackageRequested += (name, ip, port) => PullPackageRequested?.Invoke(name, ip, port) ?? Task.CompletedTask;
    }
    
    /// <summary>
    /// Updates the base path for received packages.
    /// </summary>
    public void UpdateReceivePath(string newPath)
    {
        _receiveBasePath = newPath;
        _receiver.UpdateReceivePath(newPath);
        LogService.Instance.Info($"Transfer receive path updated to: {newPath}", "TransferService");
    }

    /// <summary>
    /// Starts listening for incoming transfers.
    /// </summary>
    public bool StartListening(int port = DEFAULT_PORT)
    {
        if (IsListening) return true;

        try
        {
            Port = port;
            _cts = new CancellationTokenSource();
            
            // Determine bind address based on settings
            // BindToLocalIpOnly improves security on public networks
            if (_settingsService?.Settings.BindToLocalIpOnly == true)
            {
                var bindAddress = System.Net.IPAddress.Parse(NetworkUtils.GetLocalIpAddress());
                _listener = new TcpListener(bindAddress, port);
            }
            else
            {
                // Use TcpListener.Create to support Dual Mode (IPv4 + IPv6)
                _listener = TcpListener.Create(port);
            }
            
            _listener.Start();
            IsListening = true;

            _ = AcceptClientsAsync(_cts.Token);

            LogService.Instance.Info($"Transfer service listening on port {port}", "TransferService");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to start transfer listener on port {port}", ex, "TransferService");
            Error?.Invoke(this, $"Failed to start listener: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stops listening for transfers.
    /// </summary>
    public void StopListening()
    {
        IsListening = false;
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
    }

    /// <summary>
    /// Sends a game package folder to a peer.
    /// </summary>
    public Task<bool> SendPackageAsync(string targetIp, int targetPort, string packagePath, CancellationToken ct = default)
    {
        return _sender.SendFileOrFolderAsync(targetIp, targetPort, packagePath, "Package", null, ct);
    }

    /// <summary>
    /// Sends a save game sync zip to a peer.
    /// </summary>
    public Task<bool> SendSaveSyncAsync(string targetIp, int targetPort, string zipPath, string gameName, CancellationToken ct = default)
    {
        return _sender.SendFileOrFolderAsync(targetIp, targetPort, zipPath, "SaveSync", gameName, ct);
    }

    /// <summary>
    /// Requests a package from a peer (pull).
    /// </summary>
    public async Task RequestPullPackageAsync(string targetIp, int targetPort, string gameName)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(targetIp, targetPort);
            using var stream = client.GetStream();

            var header = new TransferHeader
            {
                Magic = PROTOCOL_MAGIC_V2,
                TransferType = "PullRequest",
                GameName = gameName,
                TotalFiles = 0,
                TotalSize = 0
            };

            await TransferUtils.SendJsonAsync(stream, header, default);
            await TransferUtils.SendJsonAsync(stream, new List<TransferFileInfo>(), default);
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Pull request failed: {ex.Message}", ex, "TransferService");
            throw;
        }
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);

                var transferId = Guid.NewGuid();
                // Delegate to receiver
                var task = _receiver.HandleIncomingTransferAsync(client, ct);

                _activeTransfers.TryAdd(transferId, task);

                _ = task.ContinueWith(t => _activeTransfers.TryRemove(transferId, out _), TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogService.Instance.Debug($"Accept error: {ex.Message}", "TransferService");
            }
        }
    }

    /// <summary>
    /// Requests list of available games from a peer.
    /// </summary>
    public async Task<List<Models.RemoteGame>?> RequestLibraryListAsync(string targetIp, int targetPort)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(targetIp, targetPort);
            using var stream = client.GetStream();

            var header = new TransferHeader
            {
                Magic = PROTOCOL_MAGIC_V2,
                TransferType = "ListRequest",
                TotalFiles = 0,
                TotalSize = 0
            };

            await TransferUtils.SendJsonAsync(stream, header, default);
            await TransferUtils.SendJsonAsync(stream, new List<TransferFileInfo>(), default);

            return await TransferUtils.ReceiveJsonAsync<List<Models.RemoteGame>>(stream, default);
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"List request failed: {ex.Message}", ex, "TransferService");
            throw;
        }
    }

    /// <summary>
    /// Runs a speed test against a peer.
    /// Respects configured bandwidth limits to avoid LAN saturation.
    /// </summary>
    public async Task<double> RunSpeedTestAsync(string targetIp, int targetPort, long testSizeBytes = 50 * 1024 * 1024, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(targetIp, targetPort, ct);
            using var stream = client.GetStream();

            var header = new TransferHeader
            {
                Magic = PROTOCOL_MAGIC_V2,
                TransferType = "SpeedTest",
                TotalSize = testSizeBytes,
                TotalFiles = 1,
                Compression = "None"
            };

            await TransferUtils.SendJsonAsync(stream, header, ct);
            await TransferUtils.SendJsonAsync(stream, new List<TransferFileInfo>(), ct);

            // Send junk data with bandwidth limiting to prevent LAN saturation
            var buffer = new byte[81920];
            Random.Shared.NextBytes(buffer);
            long sent = 0;

            // Apply configured bandwidth limit (if any)
            var limiter = new BandwidthLimiter(() => _settingsService?.Settings.TransferSpeedLimit ?? 0);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (sent < testSizeBytes)
            {
                var toWrite = (int)Math.Min(buffer.Length, testSizeBytes - sent);
                
                // Throttle based on configured limit to prevent network saturation
                await limiter.WaitAsync(toWrite, ct);
                
                await stream.WriteAsync(buffer, 0, toWrite, ct);
                sent += toWrite;
            }

            var complete = await TransferUtils.ReceiveJsonAsync<TransferComplete>(stream, ct);
            sw.Stop();

            if (complete?.Success == true && sw.Elapsed.TotalSeconds > 0)
            {
                var mbps = (sent * 8.0) / (sw.Elapsed.TotalSeconds * 1024 * 1024);
                return mbps;
            }
            return 0;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Speed test failed: {ex.Message}", ex, "TransferService");
            throw;
        }
    }

    /// <summary>
    /// Initializes swarm download capability with the discovery service.
    /// Call this after both TransferService and LanDiscoveryService are started.
    /// </summary>
    public void InitializeSwarm(LanDiscoveryService discoveryService)
    {
        _discoveryService = discoveryService;
        _swarmManager = new SwarmManager(discoveryService, _settingsService);
        
        // Wire up progress events
        _swarmManager.ProgressChanged += (s, progress) =>
        {
            ProgressChanged?.Invoke(this, new TransferProgress
            {
                GameName = progress.GameName,
                CurrentFile = progress.FilePath,
                TotalBytes = progress.TotalBytes,
                BytesTransferred = progress.CompletedBytes,
                IsSending = false // Receiving from swarm
            });
        };

        // Wire up game availability callback so we respond to swarm queries
        discoveryService.CheckGameAvailabilityCallback = gameName =>
        {
            var packagePath = Path.Combine(_receiveBasePath, gameName);
            if (Directory.Exists(packagePath))
            {
                var size = Directory.GetFiles(packagePath, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
                return (true, size);
            }
            return (false, 0);
        };

        LogService.Instance.Info("Swarm download capability initialized", "TransferService");
    }

    /// <summary>
    /// Downloads a package using swarm mode (multiple peers simultaneously).
    /// Automatically discovers peers that have the game using LAN discovery.
    /// Falls back to single-peer download if only one source is available.
    /// </summary>
    /// <param name="gameName">Name of the game to download.</param>
    /// <param name="filePath">Relative path of the file within the package.</param>
    /// <param name="fileSize">Expected file size in bytes.</param>
    /// <param name="peers">Optional pre-discovered peers. If null, auto-discovers.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Swarm download result with statistics.</returns>
    public async Task<SwarmResult> DownloadSwarmAsync(
        string gameName, 
        string filePath, 
        long fileSize,
        List<SwarmPeerInfo>? peers = null,
        CancellationToken ct = default)
    {
        if (_swarmManager == null || _discoveryService == null)
        {
            return new SwarmResult
            {
                Success = false,
                GameName = gameName,
                Error = "Swarm not initialized. Call InitializeSwarm first."
            };
        }

        // Auto-discover peers if not provided
        if (peers == null || peers.Count == 0)
        {
            LogService.Instance.Info($"Discovering swarm peers for {gameName}...", "TransferService");
            peers = await _discoveryService.DiscoverSwarmPeersAsync(gameName, TimeSpan.FromSeconds(3));
        }

        if (peers.Count == 0)
        {
            return new SwarmResult
            {
                Success = false,
                GameName = gameName,
                Error = "No peers found with this game"
            };
        }

        // Determine output path
        var outputPath = Path.Combine(_receiveBasePath, gameName, filePath);

        LogService.Instance.Info($"Starting swarm download: {gameName}/{filePath} from {peers.Count} peer(s)", "TransferService");

        return await _swarmManager.DownloadSwarmAsync(
            gameName,
            filePath,
            outputPath,
            fileSize,
            peers,
            ct);
    }

    /// <summary>
    /// Checks if swarm download is available (initialized with discovery service).
    /// </summary>
    public bool IsSwarmEnabled => _swarmManager != null && _discoveryService != null;

    /// <summary>
    /// Gets swarm peers that have a specific game.
    /// </summary>
    public async Task<List<SwarmPeerInfo>> GetSwarmPeersAsync(string gameName, TimeSpan? timeout = null)
    {
        if (_discoveryService == null)
            return new List<SwarmPeerInfo>();

        return await _discoveryService.DiscoverSwarmPeersAsync(gameName, timeout);
    }

    public void Dispose()
    {
        StopListening();
        _swarmManager?.Dispose();
        _cts?.Dispose();
    }
}
