using System.Collections.Concurrent;
using System.Diagnostics;

namespace SteamRoll.Services.Transfer;

/// <summary>
/// The "General" - orchestrates multi-peer swarm downloads.
/// Manages multiple PeerWorker instances downloading blocks in parallel.
/// Implements work-stealing for slow peer handling.
/// </summary>
public class SwarmManager : IDisposable
{
    private readonly LanDiscoveryService? _discoveryService;
    private readonly SettingsService? _settingsService;
    private readonly SwarmCoordinator _coordinator = new();
    private readonly ConcurrentDictionary<Guid, PeerWorker> _workers = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    /// Maximum concurrent peers for a single swarm download.
    /// </summary>
    public int MaxConcurrentPeers { get; set; } = 8;

    /// <summary>
    /// Raised when download progress changes.
    /// </summary>
    public event EventHandler<SwarmProgress>? ProgressChanged;

    /// <summary>
    /// Raised when a peer connects or disconnects.
    /// </summary>
#pragma warning disable CS0067 // Event is never used - will be used for UI integration
    public event EventHandler<SwarmPeerInfo>? PeerStatusChanged;
#pragma warning restore CS0067

    public SwarmManager(LanDiscoveryService? discoveryService = null, SettingsService? settingsService = null)
    {
        _discoveryService = discoveryService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Downloads a file using multiple peers (swarm mode).
    /// Falls back to single-peer mode if only one peer is available.
    /// </summary>
    /// <param name="gameName">Name of the game package.</param>
    /// <param name="filePath">Relative path within the package.</param>
    /// <param name="outputPath">Local path to write the downloaded file.</param>
    /// <param name="fileSize">Expected file size in bytes.</param>
    /// <param name="peers">List of peers that have this file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the swarm download.</returns>
    public async Task<SwarmResult> DownloadSwarmAsync(
        string gameName,
        string filePath,
        string outputPath,
        long fileSize,
        List<SwarmPeerInfo> peers,
        CancellationToken ct = default)
    {
        if (peers.Count == 0)
        {
            return new SwarmResult
            {
                Success = false,
                GameName = gameName,
                FilePath = filePath,
                Error = "No peers available"
            };
        }

        var stopwatch = Stopwatch.StartNew();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            LogService.Instance.Info(
                $"Starting swarm download of {gameName}/{filePath} ({FormatUtils.FormatBytes(fileSize)}) from {peers.Count} peer(s)", 
                "SwarmManager");

            // 1. Initialize block jobs
            _coordinator.Reset();
            var blocks = _coordinator.CreateBlockJobs(fileSize);

            // 2. Connect to peers
            var availablePeers = await ConnectToPeersAsync(peers.Take(MaxConcurrentPeers).ToList(), _cts.Token);
            
            if (availablePeers.Count == 0)
            {
                return new SwarmResult
                {
                    Success = false,
                    GameName = gameName,
                    FilePath = filePath,
                    Error = "Failed to connect to any peers"
                };
            }

            // 3. Open file for random access writing
            using var writer = new RandomAccessWriter(outputPath, fileSize);

            // 4. Spin up download tasks for each connected peer
            var peerTasks = availablePeers.Select(peer => 
                ProcessPeerQueueAsync(peer, gameName, filePath, writer, _cts.Token));

            // 5. Also start work-stealing task
            var stealingTask = WorkStealingLoopAsync(gameName, filePath, writer, _cts.Token);

            // 6. Wait for all peer tasks to complete
            await Task.WhenAll(peerTasks);
            
            // Cancel work stealing since main download is done
            _cts.Cancel();
            try { await stealingTask; } catch (OperationCanceledException) { }

            stopwatch.Stop();

            // 7. Verify completion
            if (!_coordinator.IsComplete)
            {
                var missing = _coordinator.TotalBlocks - _coordinator.CompletedBlocks;
                return new SwarmResult
                {
                    Success = false,
                    GameName = gameName,
                    FilePath = filePath,
                    Error = $"Download incomplete: {missing} blocks missing",
                    TotalBytes = _coordinator.CompletedBytes,
                    Duration = stopwatch.Elapsed,
                    PeersUsed = availablePeers.Count
                };
            }

            // Ensure all data is written
            writer.Flush();

            var result = new SwarmResult
            {
                Success = true,
                GameName = gameName,
                FilePath = filePath,
                TotalBytes = fileSize,
                Duration = stopwatch.Elapsed,
                PeersUsed = availablePeers.Count
            };

            // Collect peer contributions
            foreach (var worker in _workers.Values)
            {
                result.PeerContributions[worker.PeerId] = worker.GetStats();
            }

            LogService.Instance.Info(
                $"Swarm download complete: {FormatUtils.FormatBytes(fileSize)} in {stopwatch.Elapsed:hh\\:mm\\:ss} " +
                $"({FormatUtils.FormatBytes((long)result.AverageSpeedBytesPerSec)}/s avg, {availablePeers.Count} peers)", 
                "SwarmManager");

            return result;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Swarm download failed: {ex.Message}", ex, "SwarmManager");
            return new SwarmResult
            {
                Success = false,
                GameName = gameName,
                FilePath = filePath,
                Error = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
        finally
        {
            // Cleanup workers
            foreach (var worker in _workers.Values)
            {
                worker.Dispose();
            }
            _workers.Clear();
        }
    }

    /// <summary>
    /// Connects to a list of peers, returning those that connected successfully.
    /// </summary>
    private async Task<List<PeerWorker>> ConnectToPeersAsync(List<SwarmPeerInfo> peers, CancellationToken ct)
    {
        var connected = new List<PeerWorker>();
        var connectionTasks = peers.Select(async peer =>
        {
            var worker = new PeerWorker(peer);
            if (await worker.ConnectAsync(ct))
            {
                _workers[peer.PeerId] = worker;
                return worker;
            }
            worker.Dispose();
            return null;
        });

        var results = await Task.WhenAll(connectionTasks);
        connected.AddRange(results.Where(w => w != null)!);

        LogService.Instance.Info($"Connected to {connected.Count}/{peers.Count} peers", "SwarmManager");
        return connected;
    }

    /// <summary>
    /// Per-peer download loop. Continuously requests blocks until queue is empty.
    /// </summary>
    private async Task ProcessPeerQueueAsync(
        PeerWorker peer, 
        string gameName, 
        string filePath,
        RandomAccessWriter writer, 
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_coordinator.IsComplete)
            {
                // Get next block to download
                var block = _coordinator.DequeueBlock(peer.PeerId);
                
                if (block == null)
                {
                    // No more pending blocks - wait a bit and check for work stealing opportunities
                    await Task.Delay(100, ct);
                    continue;
                }

                try
                {
                    // Request block from peer
                    var data = await peer.RequestBlockAsync(gameName, filePath, block, ct);

                    if (data != null && data.Length == block.Length)
                    {
                        // Write to file (thread-safe)
                        writer.Write(block.Offset, data);
                        _coordinator.MarkComplete(block.Index);

                        // Report progress
                        ReportProgress(gameName, filePath);
                    }
                    else
                    {
                        // Failed - return to queue for someone else
                        _coordinator.MarkFailed(block.Index, "Incomplete or null data");
                    }
                }
                catch (Exception ex)
                {
                    _coordinator.MarkFailed(block.Index, ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Peer {peer.IpAddress} download loop error: {ex.Message}", "SwarmManager");
        }
    }

    /// <summary>
    /// Work stealing loop - handles slow peers by reassigning stalled blocks.
    /// </summary>
    private async Task WorkStealingLoopAsync(
        string gameName,
        string filePath,
        RandomAccessWriter writer,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_coordinator.IsComplete)
            {
                await Task.Delay(5000, ct); // Check every 5 seconds

                var stalledBlocks = _coordinator.GetStalledBlocks();
                
                foreach (var stalled in stalledBlocks)
                {
                    // Find a faster peer to reassign to
                    var fastPeer = _workers.Values
                        .Where(w => w.IsConnected && w.PeerId != stalled.AssignedPeerId)
                        .OrderByDescending(w => w.MeasuredSpeedBytesPerSec)
                        .FirstOrDefault();

                    if (fastPeer != null)
                    {
                        if (_coordinator.ReassignBlock(stalled.Index, fastPeer.PeerId))
                        {
                            LogService.Instance.Info(
                                $"Work stealing: reassigned block {stalled.Index} from slow peer to {fastPeer.IpAddress}", 
                                "SwarmManager");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
    }

    /// <summary>
    /// Reports current progress to listeners.
    /// </summary>
    private void ReportProgress(string gameName, string filePath)
    {
        var progress = _coordinator.GetProgress(gameName, filePath);

        // Add peer contributions
        foreach (var worker in _workers.Values)
        {
            progress.PeerContributions[worker.PeerId] = worker.GetStats();
        }

        ProgressChanged?.Invoke(this, progress);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();

        foreach (var worker in _workers.Values)
        {
            worker.Dispose();
        }
        _workers.Clear();

        _cts?.Dispose();
    }
}
