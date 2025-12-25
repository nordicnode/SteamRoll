using System.Text.Json;
using System.IO;

namespace SteamRoll.Services.Transfer;

// ========================================
// Transfer Protocol Types
// ========================================

public class TransferHeader
{
    public string Magic { get; set; } = "";
    public string? GameName { get; set; }
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public bool IsReceivedPackage { get; set; }

    /// <summary>
    /// Type of transfer (Package, SaveSync, etc).
    /// </summary>
    public string TransferType { get; set; } = "Package";

    /// <summary>
    /// Compression mode used for file transfer (e.g., "None", "GZip").
    /// </summary>
    public string Compression { get; set; } = "None";

    /// <summary>
    /// Whether this transfer supports delta sync for reduced bandwidth.
    /// </summary>
    public bool SupportsDelta { get; set; } = true;
}

public class TransferFileInfo
{
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }

    /// <summary>
    /// XxHash64 hash of the file for integrity verification.
    /// Note: Named Sha256 for backwards compatibility but uses XxHash64.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// Whether this file should use delta sync (only changed blocks).
    /// </summary>
    public bool UseDelta { get; set; }

    /// <summary>
    /// For delta transfers: literal data size to transfer.
    /// </summary>
    public long DeltaLiteralSize { get; set; }

    /// <summary>
    /// For delta transfers: number of instructions.
    /// </summary>
    public int DeltaInstructionCount { get; set; }
}


public class TransferAck
{
    public bool Accepted { get; set; }

    /// <summary>
    /// Optional rejection reason message.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// List of relative paths of files that the receiver already has and wants to skip.
    /// </summary>
    public List<string> SkippedFiles { get; set; } = new();

    /// <summary>
    /// List of files that the receiver has and wants to update via delta sync.
    /// Maps relative path to serialized block signatures.
    /// </summary>
    public Dictionary<string, string> DeltaSignatures { get; set; } = new();

    /// <summary>
    /// Whether the receiver supports delta sync protocol.
    /// </summary>
    public bool SupportsDelta { get; set; }
}

public class TransferComplete
{
    public bool Success { get; set; }
}

public class TransferProgress
{
    public string GameName { get; set; } = "";
    public string CurrentFile { get; set; } = "";
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public bool IsSending { get; set; }
    public TimeSpan? EstimatedTimeRemaining { get; set; }

    public double Percentage => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100 : 0;

    public string FormattedProgress
    {
        get
        {
            var transferred = FormatBytes(BytesTransferred);
            var total = FormatBytes(TotalBytes);
            var percentage = Percentage;
            var eta = EstimatedTimeRemaining.HasValue
                ? $" - ETA: {EstimatedTimeRemaining.Value:hh\\:mm\\:ss}"
                : "";
            return $"{transferred} / {total} ({percentage:F1}%){eta}";
        }
    }

    private static string FormatBytes(long bytes) => FormatUtils.FormatBytes(bytes);
}

public class TransferResult
{
    public bool Success { get; set; }
    public string GameName { get; set; } = "";
    public string Path { get; set; } = "";
    public long BytesTransferred { get; set; }
    public bool WasReceived { get; set; }
    public bool IsSaveSync { get; set; }

    /// <summary>
    /// Whether the package passed integrity verification after transfer.
    /// </summary>
    public bool VerificationPassed { get; set; } = true;

    /// <summary>
    /// List of verification errors if verification failed.
    /// </summary>
    public List<string> VerificationErrors { get; set; } = new();
}

public class ReceivedMarker
{
    public DateTime ReceivedAt { get; set; }
    public string ReceivedFrom { get; set; } = "";
}

/// <summary>
/// Event args for transfer approval requests.
/// Handler should call SetApproval(true/false) to approve or reject the transfer.
/// </summary>
public class TransferApprovalEventArgs : EventArgs
{
    private readonly TaskCompletionSource<bool> _approvalTcs = new();

    public string GameName { get; set; } = "";
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    
    /// <summary>
    /// Name of the peer sending the transfer (if known).
    /// </summary>
    public string? PeerName { get; set; }
    
    /// <summary>
    /// IP address of the peer sending the transfer.
    /// </summary>
    public string? PeerIp { get; set; }

    /// <summary>
    /// Gets whether the transfer was approved.
    /// </summary>
    public bool Approved { get; private set; }

    public string FormattedSize => FormatUtils.FormatBytes(SizeBytes);

    /// <summary>
    /// Sets the approval status and signals completion.
    /// Call this from the UI handler to approve or reject the transfer.
    /// </summary>
    /// <param name="approved">True to approve, false to reject.</param>
    public void SetApproval(bool approved)
    {
        Approved = approved;
        _approvalTcs.TrySetResult(approved);
    }

    /// <summary>
    /// Waits for the approval decision from the UI thread.
    /// </summary>
    /// <param name="timeout">Timeout for waiting. Defaults to 60 seconds.</param>
    /// <returns>True if approved, false if rejected or timed out.</returns>
    public async Task<bool> WaitForApprovalAsync(TimeSpan? timeout = null)
    {
        var actualTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(actualTimeout);

        try
        {
            return await _approvalTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout - reject by default
            return false;
        }
    }
}

/// <summary>
/// Tracks the state of an ongoing or interrupted transfer for resumption.
/// </summary>
public class TransferState
{
    public const string StateFileName = ".steamroll_transfer_state";

    /// <summary>
    /// Game name being transferred.
    /// </summary>
    public string GameName { get; set; } = "";

    /// <summary>
    /// Total number of files in the transfer.
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// Total size in bytes of all files.
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// Number of files fully received.
    /// </summary>
    public int FilesCompleted { get; set; }

    /// <summary>
    /// Total bytes received across all completed files.
    /// </summary>
    public long BytesReceived { get; set; }

    /// <summary>
    /// List of file paths that have been fully received (relative paths).
    /// </summary>
    public List<string> CompletedFiles { get; set; } = new();

    /// <summary>
    /// When this transfer was started.
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When this state was last updated.
    /// </summary>
    public DateTime LastUpdatedAt { get; set; }

    /// <summary>
    /// SHA256 hashes of the file list for verification (ensures same transfer).
    /// </summary>
    public string? FileListHash { get; set; }

    /// <summary>
    /// Whether this state is expired (older than 24 hours).
    /// </summary>
    public bool IsExpired => (DateTime.UtcNow - LastUpdatedAt).TotalHours > 24;

    /// <summary>
    /// Saves transfer state to disk using atomic write (temp file + move).
    /// This prevents state file corruption if the app crashes during save.
    /// </summary>
    public static void Save(string destPath, TransferState state)
    {
        try
        {
            var statePath = System.IO.Path.Combine(destPath, StateFileName);
            var tempPath = statePath + ".tmp";
            state.LastUpdatedAt = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            
            // Write to temp file first, then atomically move
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to save transfer state: {ex.Message}", "TransferState");
        }
    }

    /// <summary>
    /// Loads transfer state from disk if it exists.
    /// </summary>
    public static TransferState? Load(string destPath)
    {
        try
        {
            var statePath = System.IO.Path.Combine(destPath, StateFileName);
            if (!File.Exists(statePath)) return null;

            var json = File.ReadAllText(statePath);
            var state = JsonSerializer.Deserialize<TransferState>(json);

            // Don't return expired states
            if (state?.IsExpired == true)
            {
                Delete(destPath);
                return null;
            }

            return state;
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Could not load transfer state: {ex.Message}", "TransferState");
            return null;
        }
    }

    /// <summary>
    /// Deletes transfer state file (called on successful completion).
    /// </summary>
    public static void Delete(string destPath)
    {
        try
        {
            var statePath = System.IO.Path.Combine(destPath, StateFileName);
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Could not delete transfer state: {ex.Message}", "TransferState");
        }
    }

    /// <summary>
    /// Computes a hash of the file list to verify we're resuming the same transfer.
    /// </summary>
    public static string ComputeFileListHash(List<TransferFileInfo> files)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var combined = string.Join("|", files.Select(f => $"{f.RelativePath}:{f.Size}:{f.Sha256}"));
        var bytes = System.Text.Encoding.UTF8.GetBytes(combined);
        return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
    }
}
