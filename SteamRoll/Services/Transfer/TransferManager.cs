using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SteamRoll.Services.Transfer;

/// <summary>
/// Status of a transfer.
/// </summary>
public enum TransferStatus
{
    Pending,
    Active,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Information about a transfer for tracking and display.
/// </summary>
public class TransferInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Unique identifier for this transfer.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Name of the game being transferred.
    /// </summary>
    public string GameName { get; set; } = "";

    /// <summary>
    /// App ID of the game (for loading images).
    /// </summary>
    public int AppId { get; set; }

    /// <summary>
    /// Total size in bytes.
    /// </summary>
    public long TotalBytes { get; set; }

    private long _transferredBytes;
    /// <summary>
    /// Bytes transferred so far.
    /// </summary>
    public long TransferredBytes
    {
        get => _transferredBytes;
        set
        {
            if (_transferredBytes != value)
            {
                _transferredBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressDisplay));
                OnPropertyChanged(nameof(SpeedDisplay));
                OnPropertyChanged(nameof(TimeRemainingDisplay));
            }
        }
    }

    /// <summary>
    /// Total number of files in the transfer.
    /// </summary>
    public int TotalFiles { get; set; }

    private int _transferredFiles;
    /// <summary>
    /// Number of files transferred so far.
    /// </summary>
    public int TransferredFiles
    {
        get => _transferredFiles;
        set
        {
            if (_transferredFiles != value)
            {
                _transferredFiles = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FilesDisplay));
            }
        }
    }

    /// <summary>
    /// Whether this is an outgoing (sending) transfer.
    /// </summary>
    public bool IsSending { get; set; }

    private TransferStatus _status = TransferStatus.Pending;
    /// <summary>
    /// Current status of the transfer.
    /// </summary>
    public TransferStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(IsActive));
            }
        }
    }

    /// <summary>
    /// When the transfer started.
    /// </summary>
    public DateTime StartTime { get; set; } = DateTime.Now;

    /// <summary>
    /// When the transfer ended (if completed/failed).
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Error message if the transfer failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Name of the peer involved in the transfer.
    /// </summary>
    public string? PeerName { get; set; }

    // Calculated properties

    /// <summary>
    /// Progress as a percentage (0-100).
    /// </summary>
    public double ProgressPercent => TotalBytes > 0 ? (TransferredBytes * 100.0 / TotalBytes) : 0;

    /// <summary>
    /// Progress display string.
    /// </summary>
    public string ProgressDisplay => $"{FormatUtils.FormatBytes(TransferredBytes)} / {FormatUtils.FormatBytes(TotalBytes)}";

    /// <summary>
    /// Files progress display.
    /// </summary>
    public string FilesDisplay => $"{TransferredFiles:N0} / {TotalFiles:N0} files";

    /// <summary>
    /// Transfer speed display.
    /// </summary>
    public string SpeedDisplay
    {
        get
        {
            var elapsed = DateTime.Now - StartTime;
            if (elapsed.TotalSeconds < 1 || TransferredBytes == 0) return "Calculating...";
            var bytesPerSecond = TransferredBytes / elapsed.TotalSeconds;
            return $"{FormatUtils.FormatBytes((long)bytesPerSecond)}/s";
        }
    }

    /// <summary>
    /// Estimated time remaining display.
    /// </summary>
    public string TimeRemainingDisplay
    {
        get
        {
            var elapsed = DateTime.Now - StartTime;
            if (elapsed.TotalSeconds < 1 || TransferredBytes == 0) return "";
            var bytesPerSecond = TransferredBytes / elapsed.TotalSeconds;
            if (bytesPerSecond < 1) return "";
            var remainingBytes = TotalBytes - TransferredBytes;
            var remainingSeconds = remainingBytes / bytesPerSecond;
            var ts = TimeSpan.FromSeconds(remainingSeconds);
            if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m remaining";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s remaining";
            return $"{ts.Seconds}s remaining";
        }
    }

    /// <summary>
    /// Whether the transfer is currently active.
    /// </summary>
    public bool IsActive => Status == TransferStatus.Active || Status == TransferStatus.Pending;

    /// <summary>
    /// Human-readable status for display.
    /// </summary>
    public string StatusDisplay => Status switch
    {
        TransferStatus.Pending => "⏳ Pending",
        TransferStatus.Active => IsSending ? "📤 Sending" : "📥 Receiving",
        TransferStatus.Paused => "⏸ Paused",
        TransferStatus.Completed => "✓ Completed",
        TransferStatus.Failed => "✗ Failed",
        TransferStatus.Cancelled => "⊘ Cancelled",
        _ => "Unknown"
    };

    /// <summary>
    /// Direction icon for display.
    /// </summary>
    public string DirectionIcon => IsSending ? "📤" : "📥";

    /// <summary>
    /// Header image URL from Steam CDN.
    /// </summary>
    public string HeaderImageUrl => AppId > 0 
        ? $"https://steamcdn-a.akamaihd.net/steam/apps/{AppId}/capsule_184x69.jpg" 
        : "";
}

/// <summary>
/// Singleton service for tracking all transfers.
/// Uses IDispatcherService for UI thread marshalling to enable unit testing.
/// </summary>
public class TransferManager : INotifyPropertyChanged
{
    private static readonly Lazy<TransferManager> _instance = new(() => new TransferManager());
    public static TransferManager Instance => _instance.Value;
    
    private readonly IDispatcherService _dispatcher;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private const int MaxHistoryItems = 50;
    
    /// <summary>
    /// Minimum interval between progress updates to prevent UI thread flooding.
    /// </summary>
    private const int PROGRESS_UPDATE_INTERVAL_MS = 100;
    
    /// <summary>
    /// Tracks the last progress update time for each transfer to enable throttling.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, DateTime> _lastProgressUpdate = new();

    /// <summary>
    /// Currently active transfers.
    /// </summary>
    public ObservableCollection<TransferInfo> ActiveTransfers { get; } = new();

    /// <summary>
    /// Completed transfers (history).
    /// </summary>
    public ObservableCollection<TransferInfo> CompletedTransfers { get; } = new();

    /// <summary>
    /// Whether there are any active transfers.
    /// </summary>
    public bool HasActiveTransfers => ActiveTransfers.Count > 0;

    /// <summary>
    /// Total number of active transfers.
    /// </summary>
    public int ActiveCount => ActiveTransfers.Count;

    private TransferManager() : this(new WpfDispatcherService())
    {
    }
    
    /// <summary>
    /// Constructor for dependency injection (e.g., unit testing with InlineDispatcherService).
    /// </summary>
    public TransferManager(IDispatcherService dispatcher)
    {
        _dispatcher = dispatcher;
        ActiveTransfers.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HasActiveTransfers));
            OnPropertyChanged(nameof(ActiveCount));
        };
    }

    /// <summary>
    /// Creates and starts tracking a new transfer.
    /// </summary>
    public TransferInfo StartTransfer(string gameName, long totalBytes, int totalFiles, bool isSending, int appId = 0, string? peerName = null)
    {
        var transfer = new TransferInfo
        {
            GameName = gameName,
            TotalBytes = totalBytes,
            TotalFiles = totalFiles,
            IsSending = isSending,
            AppId = appId,
            PeerName = peerName,
            Status = TransferStatus.Active,
            StartTime = DateTime.Now
        };

        _dispatcher.Invoke(() =>
        {
            ActiveTransfers.Add(transfer);
        });

        return transfer;
    }

    /// <summary>
    /// Updates progress for a transfer.
    /// Throttled to prevent UI thread flooding - only fires PropertyChanged if:
    /// - Transfer is complete (100%)
    /// - At least PROGRESS_UPDATE_INTERVAL_MS has elapsed since last update
    /// </summary>
    public void UpdateProgress(Guid transferId, long transferredBytes, int transferredFiles)
    {
        var transfer = ActiveTransfers.FirstOrDefault(t => t.Id == transferId);
        if (transfer == null) return;

        var now = DateTime.Now;
        var isComplete = transferredBytes >= transfer.TotalBytes;
        
        // Check if we should throttle this update
        if (!isComplete)
        {
            if (_lastProgressUpdate.TryGetValue(transferId, out var lastUpdate) &&
                (now - lastUpdate).TotalMilliseconds < PROGRESS_UPDATE_INTERVAL_MS)
            {
                // Store the values but don't trigger property change notifications yet
                // The next non-throttled update or completion will push the latest values
                return;
            }
        }

        // Record this update time
        _lastProgressUpdate[transferId] = now;
        
        // Now update the values (which will trigger PropertyChanged)
        transfer.TransferredBytes = transferredBytes;
        transfer.TransferredFiles = transferredFiles;
    }

    /// <summary>
    /// Marks a transfer as completed.
    /// </summary>
    public void CompleteTransfer(Guid transferId, bool success, string? errorMessage = null)
    {
        // Clean up throttle tracking
        _lastProgressUpdate.TryRemove(transferId, out _);
        
        _dispatcher.Invoke(() =>
        {
            var transfer = ActiveTransfers.FirstOrDefault(t => t.Id == transferId);
            if (transfer != null)
            {
                transfer.Status = success ? TransferStatus.Completed : TransferStatus.Failed;
                transfer.ErrorMessage = errorMessage;
                transfer.EndTime = DateTime.Now;

                ActiveTransfers.Remove(transfer);
                CompletedTransfers.Insert(0, transfer);

                // Limit history size
                while (CompletedTransfers.Count > MaxHistoryItems)
                {
                    CompletedTransfers.RemoveAt(CompletedTransfers.Count - 1);
                }
            }
        });
        
        // Persist history after completion
        _ = SaveHistoryAsync();
    }

    /// <summary>
    /// Cancels an active transfer.
    /// </summary>
    public void CancelTransfer(Guid transferId)
    {
        // Clean up throttle tracking
        _lastProgressUpdate.TryRemove(transferId, out _);
        
        _dispatcher.Invoke(() =>
        {
            var transfer = ActiveTransfers.FirstOrDefault(t => t.Id == transferId);
            if (transfer != null)
            {
                transfer.Status = TransferStatus.Cancelled;
                transfer.EndTime = DateTime.Now;
                ActiveTransfers.Remove(transfer);
                CompletedTransfers.Insert(0, transfer);
            }
        });
    }

    /// <summary>
    /// Clears the completed transfers history.
    /// </summary>
    public void ClearHistory()
    {
        _dispatcher.Invoke(() =>
        {
            CompletedTransfers.Clear();
        });
        SaveHistoryAsync().ConfigureAwait(false);
    }

    private static string HistoryFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamRoll", "transfer_history.json");

    /// <summary>
    /// Saves completed transfers history to disk.
    /// </summary>
    public async Task SaveHistoryAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(HistoryFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Create serializable version (skip large/transient properties)
            var historyData = CompletedTransfers.Select(t => new TransferHistoryEntry
            {
                Id = t.Id,
                GameName = t.GameName,
                AppId = t.AppId,
                TotalBytes = t.TotalBytes,
                TransferredBytes = t.TransferredBytes,
                IsSending = t.IsSending,
                Status = t.Status,
                StartTime = t.StartTime,
                EndTime = t.EndTime,
                PeerName = t.PeerName
            }).ToList();

            var json = JsonSerializer.Serialize(historyData, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(HistoryFilePath, json);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to save transfer history: {ex.Message}", "TransferManager");
        }
    }

    /// <summary>
    /// Loads completed transfers history from disk.
    /// </summary>
    public async Task LoadHistoryAsync()
    {
        try
        {
            if (!File.Exists(HistoryFilePath)) return;

            var json = await File.ReadAllTextAsync(HistoryFilePath);
            var historyData = JsonSerializer.Deserialize<List<TransferHistoryEntry>>(json);
            if (historyData == null) return;

            _dispatcher.Invoke(() =>
            {
                CompletedTransfers.Clear();
                foreach (var entry in historyData.Take(MaxHistoryItems))
                {
                    CompletedTransfers.Add(new TransferInfo
                    {
                        Id = entry.Id,
                        GameName = entry.GameName,
                        AppId = entry.AppId,
                        TotalBytes = entry.TotalBytes,
                        TransferredBytes = entry.TransferredBytes,
                        IsSending = entry.IsSending,
                        Status = entry.Status,
                        StartTime = entry.StartTime,
                        EndTime = entry.EndTime,
                        PeerName = entry.PeerName
                    });
                }
            });
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to load transfer history: {ex.Message}", "TransferManager");
        }
    }
}

/// <summary>
/// Serializable entry for transfer history persistence.
/// </summary>
public class TransferHistoryEntry
{
    public Guid Id { get; set; }
    public string GameName { get; set; } = "";
    public int AppId { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public bool IsSending { get; set; }
    public TransferStatus Status { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? PeerName { get; set; }
}

