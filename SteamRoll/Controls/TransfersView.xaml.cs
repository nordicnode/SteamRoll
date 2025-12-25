using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SteamRoll.Services.Transfer;

namespace SteamRoll.Controls;

/// <summary>
/// Dedicated view for managing and viewing transfers.
/// </summary>
public partial class TransfersView : UserControl
{
    private readonly TransferManager _transferManager;
    private ObservableCollection<TransferInfo> _filteredHistory = new();
    private string _currentFilter = "All";

    // Static converter for bytes display
    public static readonly BytesToStringConverter BytesConverter = new();

    /// <summary>
    /// Event raised when user clicks the back button.
    /// </summary>
    public event RoutedEventHandler? BackClicked;

    public TransfersView()
    {
        InitializeComponent();
        _transferManager = TransferManager.Instance;

        // Bind to transfer manager collections
        ActiveTransfersList.ItemsSource = _transferManager.ActiveTransfers;
        CompletedTransfersList.ItemsSource = _filteredHistory;

        // Subscribe to collection changes
        _transferManager.ActiveTransfers.CollectionChanged += OnActiveTransfersChanged;
        _transferManager.CompletedTransfers.CollectionChanged += OnCompletedTransfersChanged;

        // Initialize UI
        UpdateEmptyStates();
        UpdateSummary();
        ApplyHistoryFilter();
    }

    private void OnActiveTransfersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateEmptyStates();
            UpdateSummary();
        });
    }

    private void OnCompletedTransfersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateEmptyStates();
            UpdateSummary();
            ApplyHistoryFilter();
        });
    }

    private void UpdateEmptyStates()
    {
        var hasActiveTransfers = _transferManager.ActiveTransfers.Count > 0;
        NoActiveTransfersPanel.Visibility = hasActiveTransfers ? Visibility.Collapsed : Visibility.Visible;
        ActiveBadge.Visibility = hasActiveTransfers ? Visibility.Visible : Visibility.Collapsed;
        ActiveBadgeCount.Text = _transferManager.ActiveTransfers.Count.ToString();

        var hasHistory = _filteredHistory.Count > 0;
        NoHistoryPanel.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;
        ClearHistoryBtn.Visibility = _transferManager.CompletedTransfers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSummary()
    {
        var activeCount = _transferManager.ActiveTransfers.Count;
        ActiveCountText.Text = $"{activeCount} active";

        // Calculate total transferred today
        var today = DateTime.Today;
        var todayTransfers = _transferManager.CompletedTransfers
            .Where(t => t.EndTime?.Date == today && t.Status == TransferStatus.Completed)
            .Sum(t => t.TotalBytes);
        
        // Add in-progress transfers
        todayTransfers += _transferManager.ActiveTransfers.Sum(t => t.TransferredBytes);

        TotalTransferredText.Text = $"{FormatBytes(todayTransfers)} transferred today";
    }

    private void ApplyHistoryFilter()
    {
        _filteredHistory.Clear();

        var source = _transferManager.CompletedTransfers.AsEnumerable();

        if (_currentFilter == "Sent")
            source = source.Where(t => t.IsSending);
        else if (_currentFilter == "Received")
            source = source.Where(t => !t.IsSending);

        foreach (var transfer in source)
        {
            _filteredHistory.Add(transfer);
        }

        UpdateEmptyStates();
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Content is string filter)
        {
            _currentFilter = filter;
            ApplyHistoryFilter();
        }
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TransferInfo transfer)
        {
            _transferManager.CancelTransfer(transfer.Id);
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _transferManager.ClearHistory();
        _filteredHistory.Clear();
        UpdateEmptyStates();
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        BackClicked?.Invoke(this, e);
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    // Cleanup on unload
    public void Cleanup()
    {
        _transferManager.ActiveTransfers.CollectionChanged -= OnActiveTransfersChanged;
        _transferManager.CompletedTransfers.CollectionChanged -= OnCompletedTransfersChanged;
    }
}

/// <summary>
/// Converter for displaying bytes as human-readable string.
/// </summary>
public class BytesToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }
        return "0 B";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
