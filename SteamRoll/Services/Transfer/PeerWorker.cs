using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using System.Net.Sockets;

namespace SteamRoll.Services.Transfer;

/// <summary>
/// Represents a single peer connection for swarm downloads.
/// Handles requesting and receiving blocks from one peer.
/// </summary>
public class PeerWorker : IDisposable
{
    private const int BUFFER_SIZE = 81920; // 80KB read buffer
    private const int CONNECTION_TIMEOUT_MS = 5000;
    private const int READ_TIMEOUT_MS = 30000;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly object _lock = new();
    private bool _disposed;

    // Statistics
    private long _totalBytesReceived;
    private int _blocksCompleted;
    private int _blocksFailed;
    private readonly Stopwatch _speedTimer = new();

    /// <summary>
    /// Unique identifier for this peer.
    /// </summary>
    public Guid PeerId { get; }

    /// <summary>
    /// IP address of this peer.
    /// </summary>
    public string IpAddress { get; }

    /// <summary>
    /// Port number for transfers.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Friendly name of this peer's device (if known).
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Whether this peer is currently connected.
    /// </summary>
    public bool IsConnected => _client?.Connected == true;

    /// <summary>
    /// Total bytes received from this peer.
    /// </summary>
    public long TotalBytesReceived => _totalBytesReceived;

    /// <summary>
    /// Number of blocks successfully received.
    /// </summary>
    public int BlocksCompleted => _blocksCompleted;

    /// <summary>
    /// Number of blocks that failed.
    /// </summary>
    public int BlocksFailed => _blocksFailed;

    /// <summary>
    /// Measured download speed from this peer (bytes/sec).
    /// Updated after each block.
    /// </summary>
    public double MeasuredSpeedBytesPerSec { get; private set; }

    public PeerWorker(Guid peerId, string ipAddress, int port, string? deviceName = null)
    {
        PeerId = peerId;
        IpAddress = ipAddress;
        Port = port;
        DeviceName = deviceName;
    }

    public PeerWorker(SwarmPeerInfo peerInfo) 
        : this(peerInfo.PeerId, peerInfo.IpAddress, peerInfo.Port, peerInfo.DeviceName)
    {
    }

    /// <summary>
    /// Connects to the peer. Must be called before requesting blocks.
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _client = new TcpClient();
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CONNECTION_TIMEOUT_MS);

            await _client.ConnectAsync(IpAddress, Port, cts.Token);
            _stream = _client.GetStream();
            _stream.ReadTimeout = READ_TIMEOUT_MS;
            _stream.WriteTimeout = READ_TIMEOUT_MS;

            LogService.Instance.Debug($"Connected to peer {IpAddress}:{Port}", "PeerWorker");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to connect to {IpAddress}:{Port}: {ex.Message}", "PeerWorker");
            return false;
        }
    }

    /// <summary>
    /// Requests a specific block from this peer.
    /// </summary>
    /// <param name="gameName">Name of the game package.</param>
    /// <param name="filePath">Relative path within package.</param>
    /// <param name="block">Block to request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Block data if successful, null if failed.</returns>
    public async Task<byte[]?> RequestBlockAsync(string gameName, string filePath, BlockJob block, CancellationToken ct)
    {
        if (_stream == null || !IsConnected)
        {
            LogService.Instance.Warning("Not connected, cannot request block", "PeerWorker");
            return null;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();

            // Send block request header
            var requestHeader = new TransferHeader
            {
                Magic = ProtocolConstants.TRANSFER_MAGIC_V3,
                GameName = gameName,
                TransferType = "BlockRequest",
                TotalFiles = 1,
                TotalSize = block.Length
            };

            await TransferUtils.SendJsonAsync(_stream, requestHeader, ct);

            // Send block request details
            var request = new RequestBlockMessage(gameName, filePath, block.Index, block.Offset, block.Length);
            await TransferUtils.SendJsonAsync(_stream, request, ct);

            // Receive block data response
            var response = await TransferUtils.ReceiveJsonAsync<BlockDataMessage>(_stream, ct);
            
            if (response == null || !response.Success || response.Data == null)
            {
                _blocksFailed++;
                LogService.Instance.Warning(
                    $"Block {block.Index} request failed from {IpAddress}: {response?.Error ?? "no response"}", 
                    "PeerWorker");
                return null;
            }

            // Verify hash if provided
            if (!string.IsNullOrEmpty(response.Hash))
            {
                var hasher = new XxHash64();
                hasher.Append(response.Data);
                var computedHash = Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
                
                if (!string.Equals(computedHash, response.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    _blocksFailed++;
                    LogService.Instance.Warning(
                        $"Block {block.Index} hash mismatch from {IpAddress}", 
                        "PeerWorker");
                    return null;
                }
            }

            // Update statistics
            stopwatch.Stop();
            Interlocked.Add(ref _totalBytesReceived, response.Data.Length);
            Interlocked.Increment(ref _blocksCompleted);
            
            // Calculate speed (exponential moving average)
            var blockSpeed = response.Data.Length / stopwatch.Elapsed.TotalSeconds;
            MeasuredSpeedBytesPerSec = MeasuredSpeedBytesPerSec > 0 
                ? MeasuredSpeedBytesPerSec * 0.7 + blockSpeed * 0.3 // EMA
                : blockSpeed;

            return response.Data;
        }
        catch (Exception ex)
        {
            _blocksFailed++;
            LogService.Instance.Warning(
                $"Exception requesting block {block.Index} from {IpAddress}: {ex.Message}", 
                "PeerWorker");
            return null;
        }
    }

    /// <summary>
    /// Gets current statistics for this peer.
    /// </summary>
    public PeerStats GetStats()
    {
        return new PeerStats
        {
            IpAddress = IpAddress,
            BytesReceived = _totalBytesReceived,
            SpeedBytesPerSec = MeasuredSpeedBytesPerSec,
            BlocksCompleted = _blocksCompleted,
            BlocksFailed = _blocksFailed,
            IsConnected = IsConnected
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _stream?.Close();
            _client?.Close();
        }
        catch { /* Ignore cleanup errors */ }

        _stream = null;
        _client = null;
    }
}
