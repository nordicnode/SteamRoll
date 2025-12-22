using System.Collections.Specialized;
using System.Windows;
using SteamRoll.Services.Transfer;

namespace SteamRoll.Controls;

/// <summary>
/// Window for viewing and managing active and completed transfers.
/// </summary>
public partial class TransferManagerWindow : Window
{
    private readonly TransferManager _transferManager;

    public TransferManagerWindow()
    {
        InitializeComponent();
        _transferManager = TransferManager.Instance;

        // Bind to transfer manager collections
        ActiveTransfersList.ItemsSource = _transferManager.ActiveTransfers;
        CompletedTransfersList.ItemsSource = _transferManager.CompletedTransfers;

        // Subscribe to collection changes
        _transferManager.ActiveTransfers.CollectionChanged += OnActiveTransfersChanged;
        _transferManager.CompletedTransfers.CollectionChanged += OnCompletedTransfersChanged;

        UpdateEmptyStates();
        UpdateSummary();
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
        });
    }

    private void UpdateEmptyStates()
    {
        NoActiveTransfersPanel.Visibility = _transferManager.ActiveTransfers.Count == 0 
            ? Visibility.Visible 
            : Visibility.Collapsed;

        NoHistoryPanel.Visibility = _transferManager.CompletedTransfers.Count == 0 
            ? Visibility.Visible 
            : Visibility.Collapsed;

        ClearHistoryBtn.Visibility = _transferManager.CompletedTransfers.Count > 0 
            ? Visibility.Visible 
            : Visibility.Collapsed;
    }

    private void UpdateSummary()
    {
        var activeCount = _transferManager.ActiveTransfers.Count;
        var completedCount = _transferManager.CompletedTransfers.Count;

        if (activeCount == 0 && completedCount == 0)
        {
            SummaryText.Text = "No transfers";
        }
        else if (activeCount > 0)
        {
            var plural = activeCount == 1 ? "" : "s";
            SummaryText.Text = $"{activeCount} active transfer{plural}";
        }
        else
        {
            var plural = completedCount == 1 ? "" : "s";
            SummaryText.Text = $"{completedCount} completed transfer{plural}";
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _transferManager.ClearHistory();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Unsubscribe from events
        _transferManager.ActiveTransfers.CollectionChanged -= OnActiveTransfersChanged;
        _transferManager.CompletedTransfers.CollectionChanged -= OnCompletedTransfersChanged;
    }
}
