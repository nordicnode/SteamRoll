using System.Collections.ObjectModel;
using System.Windows.Input;
using SteamRoll.Services;
using SteamRoll.Services.Transfer;

namespace SteamRoll.ViewModels;

/// <summary>
/// ViewModel for the transfers view, managing active and completed transfers.
/// Wraps TransferManager singleton to provide data-binding and commands.
/// </summary>
public class TransfersViewModel : ViewModelBase
{
    private readonly TransferManager _transferManager;
    private bool _isVisible;
    private TransferInfo? _selectedTransfer;

    public TransfersViewModel() : this(TransferManager.Instance) { }

    public TransfersViewModel(TransferManager transferManager)
    {
        _transferManager = transferManager;

        // Initialize commands
        BackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
        CancelTransferCommand = new RelayCommand<TransferInfo>(CancelTransfer, CanCancelTransfer);
        ClearHistoryCommand = new RelayCommand(ClearHistory, () => CompletedTransfers.Count > 0);
        RetryTransferCommand = new RelayCommand<TransferInfo>(RetryTransfer, CanRetryTransfer);

        // Forward property changes from TransferManager
        _transferManager.PropertyChanged += (s, e) =>
        {
            OnPropertyChanged(e.PropertyName);
            if (e.PropertyName == nameof(TransferManager.HasActiveTransfers) ||
                e.PropertyName == nameof(TransferManager.ActiveCount))
            {
                OnPropertyChanged(nameof(StatusSummary));
                OnPropertyChanged(nameof(TotalProgress));
                OnPropertyChanged(nameof(TotalSpeed));
            }
        };

        // Monitor collection changes for summary updates
        ActiveTransfers.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(StatusSummary));
            OnPropertyChanged(nameof(TotalProgress));
            OnPropertyChanged(nameof(HasActiveTransfers));
        };

        CompletedTransfers.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HasCompletedTransfers));
            OnPropertyChanged(nameof(CompletedCount));
        };
    }

    #region Properties - Delegated to TransferManager

    /// <summary>
    /// Currently active transfers (from TransferManager).
    /// </summary>
    public ObservableCollection<TransferInfo> ActiveTransfers => _transferManager.ActiveTransfers;

    /// <summary>
    /// Completed transfers history (from TransferManager).
    /// </summary>
    public ObservableCollection<TransferInfo> CompletedTransfers => _transferManager.CompletedTransfers;

    /// <summary>
    /// Whether there are any active transfers.
    /// </summary>
    public bool HasActiveTransfers => _transferManager.HasActiveTransfers;

    /// <summary>
    /// Number of active transfers.
    /// </summary>
    public int ActiveCount => _transferManager.ActiveCount;

    #endregion

    #region Properties - ViewModel State

    /// <summary>
    /// Whether the transfers view is visible.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>
    /// Currently selected transfer for details.
    /// </summary>
    public TransferInfo? SelectedTransfer
    {
        get => _selectedTransfer;
        set => SetProperty(ref _selectedTransfer, value);
    }

    /// <summary>
    /// Whether there are any completed transfers.
    /// </summary>
    public bool HasCompletedTransfers => CompletedTransfers.Count > 0;

    /// <summary>
    /// Number of completed transfers.
    /// </summary>
    public int CompletedCount => CompletedTransfers.Count;

    /// <summary>
    /// Summary status text for header display.
    /// </summary>
    public string StatusSummary
    {
        get
        {
            if (!HasActiveTransfers)
                return "No active transfers";
            
            var sending = ActiveTransfers.Count(t => t.IsSending);
            var receiving = ActiveTransfers.Count - sending;
            
            if (sending > 0 && receiving > 0)
                return $"📤 {sending} sending, 📥 {receiving} receiving";
            if (sending > 0)
                return $"📤 Sending {sending} {(sending == 1 ? "game" : "games")}";
            return $"📥 Receiving {receiving} {(receiving == 1 ? "game" : "games")}";
        }
    }

    /// <summary>
    /// Total progress across all active transfers (0-100).
    /// </summary>
    public double TotalProgress
    {
        get
        {
            if (!HasActiveTransfers) return 0;
            var totalBytes = ActiveTransfers.Sum(t => t.TotalBytes);
            var transferredBytes = ActiveTransfers.Sum(t => t.TransferredBytes);
            return totalBytes > 0 ? (transferredBytes * 100.0 / totalBytes) : 0;
        }
    }

    /// <summary>
    /// Aggregate speed display for all active transfers.
    /// </summary>
    public string TotalSpeed
    {
        get
        {
            if (!HasActiveTransfers) return "";
            
            var totalBytes = ActiveTransfers.Sum(t => t.TransferredBytes);
            var oldestStart = ActiveTransfers.Min(t => t.StartTime);
            var elapsed = DateTime.Now - oldestStart;
            
            if (elapsed.TotalSeconds < 1) return "Calculating...";
            var bytesPerSecond = totalBytes / elapsed.TotalSeconds;
            return $"{FormatUtils.FormatBytes((long)bytesPerSecond)}/s";
        }
    }

    /// <summary>
    /// Total bytes transferred across all active transfers.
    /// </summary>
    public long TotalBytesTransferred => ActiveTransfers.Sum(t => t.TransferredBytes);

    /// <summary>
    /// Total bytes to transfer across all active transfers.
    /// </summary>
    public long TotalBytes => ActiveTransfers.Sum(t => t.TotalBytes);

    #endregion

    #region Commands

    public ICommand BackCommand { get; }
    public ICommand CancelTransferCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand RetryTransferCommand { get; }

    #endregion

    #region Events

    /// <summary>
    /// Raised when back navigation is requested.
    /// </summary>
    public event EventHandler? BackRequested;

    /// <summary>
    /// Raised when a transfer retry is requested (View handles re-initiating).
    /// </summary>
    public event EventHandler<TransferInfo>? RetryRequested;

    #endregion

    #region Methods

    /// <summary>
    /// Starts tracking a new transfer.
    /// </summary>
    public TransferInfo StartTransfer(string gameName, long totalBytes, int totalFiles, bool isSending, int appId = 0, string? peerName = null)
    {
        return _transferManager.StartTransfer(gameName, totalBytes, totalFiles, isSending, appId, peerName);
    }

    /// <summary>
    /// Updates progress for a transfer.
    /// </summary>
    public void UpdateProgress(Guid transferId, long transferredBytes, int transferredFiles)
    {
        _transferManager.UpdateProgress(transferId, transferredBytes, transferredFiles);
        OnPropertyChanged(nameof(TotalProgress));
        OnPropertyChanged(nameof(TotalSpeed));
        OnPropertyChanged(nameof(TotalBytesTransferred));
    }

    /// <summary>
    /// Marks a transfer as completed.
    /// </summary>
    public void CompleteTransfer(Guid transferId, bool success, string? errorMessage = null)
    {
        _transferManager.CompleteTransfer(transferId, success, errorMessage);
    }

    private void CancelTransfer(TransferInfo? transfer)
    {
        if (transfer == null) return;
        _transferManager.CancelTransfer(transfer.Id);
    }

    private static bool CanCancelTransfer(TransferInfo? transfer)
    {
        return transfer?.IsActive == true;
    }

    private void ClearHistory()
    {
        _transferManager.ClearHistory();
    }

    private void RetryTransfer(TransferInfo? transfer)
    {
        if (transfer == null) return;
        RetryRequested?.Invoke(this, transfer);
    }

    private static bool CanRetryTransfer(TransferInfo? transfer)
    {
        return transfer?.Status == TransferStatus.Failed || transfer?.Status == TransferStatus.Cancelled;
    }

    /// <summary>
    /// Refreshes computed properties (call periodically for speed updates).
    /// </summary>
    public void RefreshDisplayProperties()
    {
        OnPropertyChanged(nameof(TotalSpeed));
        OnPropertyChanged(nameof(TotalProgress));
        OnPropertyChanged(nameof(StatusSummary));
    }

    #endregion
}
