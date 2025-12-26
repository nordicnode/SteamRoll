using System.Collections.Concurrent;

namespace SteamRoll.Services.Transfer;

/// <summary>
/// Manages block state and assignment for swarm downloads.
/// Tracks which blocks are pending, in-flight, or completed across multiple peers.
/// Implements work-stealing for slow peer handling.
/// </summary>
public class SwarmCoordinator
{
    /// <summary>
    /// Default chunk size: 4MB. Balance between:
    /// - Too small: More overhead from request/response cycles
    /// - Too large: Less granular load balancing, slower recovery from peer failures
    /// </summary>
    public const int CHUNK_SIZE = 4 * 1024 * 1024; // 4MB

    /// <summary>
    /// Maximum failed attempts before permanently skipping a block.
    /// </summary>
    public const int MAX_RETRY_ATTEMPTS = 3;

    /// <summary>
    /// Time after which an in-flight block is considered stalled (for work stealing).
    /// </summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<BlockJob> _pendingBlocks = new();
    private readonly ConcurrentDictionary<int, BlockJob> _inFlightBlocks = new();
    private readonly ConcurrentDictionary<int, BlockJob> _completedBlocks = new();
    private readonly object _lock = new();

    private long _fileSize;
    private int _totalBlocks;

    /// <summary>
    /// Gets the total number of blocks in this file.
    /// </summary>
    public int TotalBlocks => _totalBlocks;

    /// <summary>
    /// Gets the number of completed blocks.
    /// </summary>
    public int CompletedBlocks => _completedBlocks.Count;

    /// <summary>
    /// Gets the number of blocks currently being downloaded.
    /// </summary>
    public int InFlightBlocks => _inFlightBlocks.Count;

    /// <summary>
    /// Gets the number of blocks waiting to be assigned.
    /// </summary>
    public int PendingBlocks => _pendingBlocks.Count;

    /// <summary>
    /// Checks if all blocks have been completed.
    /// </summary>
    public bool IsComplete => _completedBlocks.Count == _totalBlocks;

    /// <summary>
    /// Gets total bytes completed.
    /// </summary>
    public long CompletedBytes => _completedBlocks.Values.Sum(b => (long)b.Length);

    /// <summary>
    /// Initializes block jobs for a file of the given size.
    /// </summary>
    /// <param name="fileSize">Total file size in bytes.</param>
    /// <returns>List of created block jobs.</returns>
    public List<BlockJob> CreateBlockJobs(long fileSize)
    {
        _fileSize = fileSize;
        var blocks = new List<BlockJob>();

        long offset = 0;
        int index = 0;

        while (offset < fileSize)
        {
            var length = (int)Math.Min(CHUNK_SIZE, fileSize - offset);
            var block = new BlockJob
            {
                Index = index,
                Offset = offset,
                Length = length,
                IsCompleted = false,
                AssignedPeerId = null,
                AssignmentTime = null,
                FailedAttempts = 0
            };

            blocks.Add(block);
            _pendingBlocks.Enqueue(block);

            offset += length;
            index++;
        }

        _totalBlocks = blocks.Count;

        LogService.Instance.Info(
            $"Created {_totalBlocks} blocks for {FormatUtils.FormatBytes(fileSize)} file ({CHUNK_SIZE / 1024 / 1024}MB chunks)", 
            "SwarmCoordinator");

        return blocks;
    }

    /// <summary>
    /// Dequeues the next available block for a peer to download.
    /// Marks the block as in-flight with the peer's ID.
    /// </summary>
    /// <param name="peerId">ID of the peer that will download this block.</param>
    /// <returns>The next available block, or null if none available.</returns>
    public BlockJob? DequeueBlock(Guid peerId)
    {
        if (_pendingBlocks.TryDequeue(out var block))
        {
            lock (_lock)
            {
                block.AssignedPeerId = peerId;
                block.AssignmentTime = DateTime.UtcNow;
                _inFlightBlocks[block.Index] = block;
            }

            return block;
        }

        return null;
    }

    /// <summary>
    /// Marks a block as successfully completed.
    /// </summary>
    /// <param name="blockIndex">Index of the completed block.</param>
    public void MarkComplete(int blockIndex)
    {
        lock (_lock)
        {
            if (_inFlightBlocks.TryRemove(blockIndex, out var block))
            {
                block.IsCompleted = true;
                block.AssignedPeerId = null;
                _completedBlocks[blockIndex] = block;
            }
        }
    }

    /// <summary>
    /// Marks a block as failed and returns it to the pending queue for retry.
    /// </summary>
    /// <param name="blockIndex">Index of the failed block.</param>
    /// <param name="reason">Optional reason for failure (for logging).</param>
    public void MarkFailed(int blockIndex, string? reason = null)
    {
        lock (_lock)
        {
            if (_inFlightBlocks.TryRemove(blockIndex, out var block))
            {
                block.FailedAttempts++;
                block.AssignedPeerId = null;
                block.AssignmentTime = null;

                if (block.FailedAttempts < MAX_RETRY_ATTEMPTS)
                {
                    _pendingBlocks.Enqueue(block);
                    LogService.Instance.Warning(
                        $"Block {blockIndex} failed (attempt {block.FailedAttempts}): {reason ?? "unknown"}. Re-queued.", 
                        "SwarmCoordinator");
                }
                else
                {
                    LogService.Instance.Error(
                        $"Block {blockIndex} permanently failed after {MAX_RETRY_ATTEMPTS} attempts", 
                        category: "SwarmCoordinator");
                }
            }
        }
    }

    /// <summary>
    /// Gets the slowest in-flight block for work stealing.
    /// Used when the pending queue is empty but blocks are still in progress.
    /// </summary>
    /// <param name="excludePeerId">Optional peer ID to exclude (don't steal from this peer).</param>
    /// <returns>The longest-running in-flight block, or null if none qualify.</returns>
    public BlockJob? GetSlowestInFlightBlock(Guid? excludePeerId = null)
    {
        var now = DateTime.UtcNow;

        var candidates = _inFlightBlocks.Values
            .Where(b => b.AssignedPeerId != excludePeerId)
            .Where(b => b.AssignmentTime.HasValue && (now - b.AssignmentTime.Value) > StallTimeout)
            .OrderByDescending(b => now - b.AssignmentTime!.Value)
            .ToList();

        return candidates.FirstOrDefault();
    }

    /// <summary>
    /// Gets all stalled blocks that have exceeded the timeout.
    /// Used for proactive work stealing.
    /// </summary>
    /// <returns>List of stalled blocks.</returns>
    public List<BlockJob> GetStalledBlocks()
    {
        var now = DateTime.UtcNow;

        return _inFlightBlocks.Values
            .Where(b => b.AssignmentTime.HasValue && (now - b.AssignmentTime.Value) > StallTimeout)
            .ToList();
    }

    /// <summary>
    /// Reassigns a stalled block to a different peer.
    /// This is speculative execution - both peers may complete it.
    /// </summary>
    /// <param name="blockIndex">Index of the block to reassign.</param>
    /// <param name="newPeerId">ID of the new peer.</param>
    /// <returns>True if reassigned, false if block not found or already completed.</returns>
    public bool ReassignBlock(int blockIndex, Guid newPeerId)
    {
        lock (_lock)
        {
            if (_completedBlocks.ContainsKey(blockIndex))
            {
                // Already completed by original peer
                return false;
            }

            if (_inFlightBlocks.TryGetValue(blockIndex, out var block))
            {
                block.AssignedPeerId = newPeerId;
                block.AssignmentTime = DateTime.UtcNow;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Gets current progress information.
    /// </summary>
    public SwarmProgress GetProgress(string gameName, string filePath)
    {
        return new SwarmProgress
        {
            GameName = gameName,
            FilePath = filePath,
            TotalBytes = _fileSize,
            CompletedBytes = CompletedBytes,
            TotalBlocks = _totalBlocks,
            CompletedBlocks = CompletedBlocks,
            InFlightBlocks = InFlightBlocks
        };
    }

    /// <summary>
    /// Resets the coordinator for a new download.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _pendingBlocks.Clear();
            _inFlightBlocks.Clear();
            _completedBlocks.Clear();
            _totalBlocks = 0;
            _fileSize = 0;
        }
    }
}
