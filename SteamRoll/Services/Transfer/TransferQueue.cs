using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SteamRoll.Services.Transfer;

/// <summary>
/// Priority levels for queued transfers.
/// </summary>
public enum TransferPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

/// <summary>
/// Represents a transfer waiting in the queue.
/// </summary>
public class QueuedTransfer : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Unique identifier for this queued transfer.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Name of the game to transfer.
    /// </summary>
    public required string GameName { get; init; }

    /// <summary>
    /// App ID of the game.
    /// </summary>
    public int AppId { get; init; }

    /// <summary>
    /// Path to the package to transfer.
    /// </summary>
    public required string PackagePath { get; init; }

    /// <summary>
    /// Peer to send to or receive from.
    /// </summary>
    public required string PeerAddress { get; init; }

    /// <summary>
    /// Port for the transfer.
    /// </summary>
    public int PeerPort { get; init; }

    /// <summary>
    /// Name of the peer.
    /// </summary>
    public string? PeerName { get; init; }

    /// <summary>
    /// Whether this is an outgoing transfer.
    /// </summary>
    public bool IsSending { get; init; }

    /// <summary>
    /// Estimated total bytes (may not be known until transfer starts).
    /// </summary>
    public long EstimatedBytes { get; init; }

    /// <summary>
    /// Estimated file count.
    /// </summary>
    public int EstimatedFiles { get; init; }

    private TransferPriority _priority = TransferPriority.Normal;
    /// <summary>
    /// Priority of this transfer in the queue.
    /// </summary>
    public TransferPriority Priority
    {
        get => _priority;
        set
        {
            if (_priority != value)
            {
                _priority = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PriorityDisplay));
            }
        }
    }

    private bool _isPaused;
    /// <summary>
    /// Whether this transfer is paused.
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused != value)
            {
                _isPaused = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    /// <summary>
    /// When this transfer was queued.
    /// </summary>
    public DateTime QueuedAt { get; init; } = DateTime.Now;

    /// <summary>
    /// Scheduled start time (null = start ASAP).
    /// </summary>
    public DateTime? ScheduledTime { get; init; }

    /// <summary>
    /// Display string for priority.
    /// </summary>
    public string PriorityDisplay => Priority switch
    {
        TransferPriority.High => "🔴 High",
        TransferPriority.Normal => "🟡 Normal",
        TransferPriority.Low => "🟢 Low",
        _ => "Normal"
    };

    /// <summary>
    /// Display string for status.
    /// </summary>
    public string StatusDisplay
    {
        get
        {
            if (IsPaused) return "⏸ Paused";
            if (ScheduledTime.HasValue && ScheduledTime > DateTime.Now)
                return $"⏰ Scheduled {ScheduledTime:t}";
            return "⏳ Queued";
        }
    }

    /// <summary>
    /// Header image URL from Steam CDN.
    /// </summary>
    public string HeaderImageUrl => AppId > 0 
        ? $"https://steamcdn-a.akamaihd.net/steam/apps/{AppId}/capsule_184x69.jpg" 
        : "";
}

/// <summary>
/// Manages a queue of transfers with priority ordering, pause/resume, and scheduling.
/// </summary>
public class TransferQueue : INotifyPropertyChanged
{
    private static readonly Lazy<TransferQueue> _instance = new(() => new TransferQueue());
    public static TransferQueue Instance => _instance.Value;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Event raised when a transfer is ready to start.
    /// </summary>
    public event EventHandler<QueuedTransfer>? TransferReady;

    private readonly IDispatcherService _dispatcher;
    private readonly object _queueLock = new();
    private readonly string _queueFile;
    private bool _isProcessing;
    private int _maxConcurrent = 1;

    /// <summary>
    /// Queued transfers waiting to be processed.
    /// </summary>
    public ObservableCollection<QueuedTransfer> Queue { get; } = new();

    /// <summary>
    /// Maximum concurrent transfers allowed.
    /// </summary>
    public int MaxConcurrentTransfers
    {
        get => _maxConcurrent;
        set
        {
            if (value > 0 && value != _maxConcurrent)
            {
                _maxConcurrent = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Number of items in the queue.
    /// </summary>
    public int Count => Queue.Count;

    /// <summary>
    /// Whether there are queued transfers.
    /// </summary>
    public bool HasQueuedTransfers => Queue.Count > 0;

    private TransferQueue() : this(new WpfDispatcherService())
    {
    }

    public TransferQueue(IDispatcherService dispatcher)
    {
        _dispatcher = dispatcher;
        
        // Store queue state in LocalApplicationData
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamRoll");
        _queueFile = Path.Combine(cacheDir, "transfer_queue.json");
        
        Queue.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasQueuedTransfers));
        };

        LoadQueue();
    }

    /// <summary>
    /// Adds a transfer to the queue.
    /// </summary>
    public void Enqueue(QueuedTransfer transfer)
    {
        _dispatcher.Invoke(() =>
        {
            lock (_queueLock)
            {
                // Insert based on priority (higher priority = earlier in queue)
                int insertIndex = 0;
                for (int i = 0; i < Queue.Count; i++)
                {
                    if ((int)Queue[i].Priority >= (int)transfer.Priority)
                    {
                        insertIndex = i + 1;
                    }
                    else
                    {
                        break;
                    }
                }
                Queue.Insert(insertIndex, transfer);
            }
        });

        SaveQueue();
        TryProcessNext();

        LogService.Instance.Info($"Queued transfer: {transfer.GameName} (Priority: {transfer.Priority})", "TransferQueue");
    }

    /// <summary>
    /// Removes a transfer from the queue.
    /// </summary>
    public bool Remove(Guid transferId)
    {
        bool removed = false;
        _dispatcher.Invoke(() =>
        {
            lock (_queueLock)
            {
                var transfer = Queue.FirstOrDefault(t => t.Id == transferId);
                if (transfer != null)
                {
                    Queue.Remove(transfer);
                    removed = true;
                }
            }
        });

        if (removed)
        {
            SaveQueue();
            LogService.Instance.Info($"Removed transfer from queue: {transferId}", "TransferQueue");
        }

        return removed;
    }

    /// <summary>
    /// Pauses a queued transfer.
    /// </summary>
    public void Pause(Guid transferId)
    {
        var transfer = Queue.FirstOrDefault(t => t.Id == transferId);
        if (transfer != null)
        {
            transfer.IsPaused = true;
            SaveQueue();
            LogService.Instance.Info($"Paused queued transfer: {transfer.GameName}", "TransferQueue");
        }
    }

    /// <summary>
    /// Resumes a paused queued transfer.
    /// </summary>
    public void Resume(Guid transferId)
    {
        var transfer = Queue.FirstOrDefault(t => t.Id == transferId);
        if (transfer != null)
        {
            transfer.IsPaused = false;
            SaveQueue();
            TryProcessNext();
            LogService.Instance.Info($"Resumed queued transfer: {transfer.GameName}", "TransferQueue");
        }
    }

    /// <summary>
    /// Changes the priority of a queued transfer.
    /// </summary>
    public void SetPriority(Guid transferId, TransferPriority priority)
    {
        _dispatcher.Invoke(() =>
        {
            lock (_queueLock)
            {
                var transfer = Queue.FirstOrDefault(t => t.Id == transferId);
                if (transfer != null && transfer.Priority != priority)
                {
                    // Remove and re-insert with new priority
                    Queue.Remove(transfer);
                    transfer.Priority = priority;
                    
                    int insertIndex = 0;
                    for (int i = 0; i < Queue.Count; i++)
                    {
                        if ((int)Queue[i].Priority >= (int)priority)
                        {
                            insertIndex = i + 1;
                        }
                        else
                        {
                            break;
                        }
                    }
                    Queue.Insert(insertIndex, transfer);
                }
            }
        });

        SaveQueue();
        LogService.Instance.Info($"Changed transfer priority: {transferId} -> {priority}", "TransferQueue");
    }

    /// <summary>
    /// Moves a transfer to the front of the queue (highest priority).
    /// </summary>
    public void MoveToFront(Guid transferId)
    {
        _dispatcher.Invoke(() =>
        {
            lock (_queueLock)
            {
                var transfer = Queue.FirstOrDefault(t => t.Id == transferId);
                if (transfer != null && Queue.IndexOf(transfer) > 0)
                {
                    Queue.Remove(transfer);
                    transfer.Priority = TransferPriority.High;
                    Queue.Insert(0, transfer);
                }
            }
        });

        SaveQueue();
        TryProcessNext();
    }

    /// <summary>
    /// Called when a transfer completes to process the next item.
    /// </summary>
    public void OnTransferCompleted()
    {
        _isProcessing = false;
        TryProcessNext();
    }

    /// <summary>
    /// Attempts to start the next queued transfer.
    /// </summary>
    private void TryProcessNext()
    {
        if (_isProcessing) return;

        QueuedTransfer? nextTransfer = null;

        lock (_queueLock)
        {
            // Check concurrent limit
            var activeCount = TransferManager.Instance.ActiveTransfers.Count;
            if (activeCount >= _maxConcurrent) return;

            // Find next eligible transfer (not paused, not scheduled for later)
            var now = DateTime.Now;
            nextTransfer = Queue.FirstOrDefault(t => 
                !t.IsPaused && 
                (!t.ScheduledTime.HasValue || t.ScheduledTime <= now));
        }

        if (nextTransfer != null)
        {
            _isProcessing = true;
            
            _dispatcher.Invoke(() =>
            {
                lock (_queueLock)
                {
                    Queue.Remove(nextTransfer);
                }
            });

            SaveQueue();
            TransferReady?.Invoke(this, nextTransfer);
        }
    }

    /// <summary>
    /// Clears all queued transfers.
    /// </summary>
    public void Clear()
    {
        _dispatcher.Invoke(() =>
        {
            lock (_queueLock)
            {
                Queue.Clear();
            }
        });

        SaveQueue();
        LogService.Instance.Info("Cleared transfer queue", "TransferQueue");
    }

    /// <summary>
    /// Persists the queue to disk.
    /// </summary>
    private void SaveQueue()
    {
        try
        {
            var dir = Path.GetDirectoryName(_queueFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            lock (_queueLock)
            {
                var data = Queue.Select(t => new QueuedTransferData
                {
                    Id = t.Id,
                    GameName = t.GameName,
                    AppId = t.AppId,
                    PackagePath = t.PackagePath,
                    PeerAddress = t.PeerAddress,
                    PeerPort = t.PeerPort,
                    PeerName = t.PeerName,
                    IsSending = t.IsSending,
                    EstimatedBytes = t.EstimatedBytes,
                    EstimatedFiles = t.EstimatedFiles,
                    Priority = t.Priority,
                    IsPaused = t.IsPaused,
                    QueuedAt = t.QueuedAt,
                    ScheduledTime = t.ScheduledTime
                }).ToList();

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_queueFile, json);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to save transfer queue: {ex.Message}", "TransferQueue");
        }
    }

    /// <summary>
    /// Loads the queue from disk.
    /// </summary>
    private void LoadQueue()
    {
        try
        {
            if (!File.Exists(_queueFile)) return;

            var json = File.ReadAllText(_queueFile);
            var data = JsonSerializer.Deserialize<List<QueuedTransferData>>(json);

            if (data != null)
            {
                foreach (var item in data)
                {
                    // Skip transfers with missing packages
                    if (!Directory.Exists(item.PackagePath)) continue;

                    var transfer = new QueuedTransfer
                    {
                        GameName = item.GameName,
                        AppId = item.AppId,
                        PackagePath = item.PackagePath,
                        PeerAddress = item.PeerAddress,
                        PeerPort = item.PeerPort,
                        PeerName = item.PeerName,
                        IsSending = item.IsSending,
                        EstimatedBytes = item.EstimatedBytes,
                        EstimatedFiles = item.EstimatedFiles,
                        Priority = item.Priority,
                        IsPaused = item.IsPaused,
                        QueuedAt = item.QueuedAt,
                        ScheduledTime = item.ScheduledTime
                    };
                    
                    Queue.Add(transfer);
                }

                LogService.Instance.Info($"Loaded {Queue.Count} queued transfers", "TransferQueue");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to load transfer queue: {ex.Message}", "TransferQueue");
        }
    }

    /// <summary>
    /// Data transfer object for serialization.
    /// </summary>
    private class QueuedTransferData
    {
        public Guid Id { get; set; }
        public string GameName { get; set; } = "";
        public int AppId { get; set; }
        public string PackagePath { get; set; } = "";
        public string PeerAddress { get; set; } = "";
        public int PeerPort { get; set; }
        public string? PeerName { get; set; }
        public bool IsSending { get; set; }
        public long EstimatedBytes { get; set; }
        public int EstimatedFiles { get; set; }
        public TransferPriority Priority { get; set; }
        public bool IsPaused { get; set; }
        public DateTime QueuedAt { get; set; }
        public DateTime? ScheduledTime { get; set; }
    }
}
