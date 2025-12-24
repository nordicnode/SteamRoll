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
    private const int DEFAULT_PORT = 27051;
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
            var bindAddress = _settingsService?.Settings.BindToLocalIpOnly == true
                ? System.Net.IPAddress.Parse(NetworkUtils.GetLocalIpAddress())
                : IPAddress.Any;
            
            _listener = new TcpListener(bindAddress, port);
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

    public void Dispose()
    {
        StopListening();
        _cts?.Dispose();
    }
}
