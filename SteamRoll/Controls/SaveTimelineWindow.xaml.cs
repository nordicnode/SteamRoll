using System.Windows;
using System.Windows.Input;
using SteamRoll.Services;

namespace SteamRoll.Controls;

/// <summary>
/// Window for viewing and managing save restore points (Time Machine).
/// </summary>
public partial class SaveTimelineWindow : Window
{
    private readonly SaveSyncService _saveSyncService;
    private readonly int _appId;
    private readonly string _gameName;

    public SaveTimelineWindow(SaveSyncService saveSyncService, int appId, string gameName)
    {
        InitializeComponent();
        
        _saveSyncService = saveSyncService;
        _appId = appId;
        _gameName = gameName;

        TitleText.Text = $"Time Machine - {gameName}";
        GameNameText.Text = gameName;

        LoadRestorePoints();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void LoadRestorePoints()
    {
        var points = _saveSyncService.GetRestorePoints(_appId);
        
        RestorePointsList.ItemsSource = points;
        RestorePointCountText.Text = $"{points.Count} restore point{(points.Count != 1 ? "s" : "")}";

        // Show/hide empty state
        EmptyState.Visibility = points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TimelineScroll.Visibility = points.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CreateRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RestorePointDialog
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.Confirmed)
        {
            CreateRestorePointBtn.IsEnabled = false;
            
            try
            {
                var point = await _saveSyncService.CreateRestorePointAsync(
                    _appId,
                    dialog.RestorePointName,
                    "",
                    dialog.IsPinned);

                if (point != null)
                {
                    LoadRestorePoints();
                    
                    MessageBox.Show(
                        $"Restore point '{point.Name}' created successfully!",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        "No save data found.\n\nRun the game at least once to create save files before creating a restore point.",
                        "No Save Data",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                CreateRestorePointBtn.IsEnabled = true;
            }
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        if (button.Tag is not Guid restorePointId) return;

        var points = _saveSyncService.GetRestorePoints(_appId);
        var point = points.FirstOrDefault(p => p.Id == restorePointId);
        if (point == null) return;

        var result = MessageBox.Show(
            $"Restore saves to '{point.Name}'?\n\nYour current saves will be backed up first.",
            "Confirm Restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            button.IsEnabled = false;
            
            try
            {
                var success = await _saveSyncService.RestoreFromPointAsync(_appId, restorePointId);

                if (success)
                {
                    MessageBox.Show(
                        $"Successfully restored to '{point.Name}'!",
                        "Restored",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        "Failed to restore. Check the logs for details.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        if (button.Tag is not Guid restorePointId) return;

        var points = _saveSyncService.GetRestorePoints(_appId);
        var point = points.FirstOrDefault(p => p.Id == restorePointId);
        if (point == null) return;

        var result = MessageBox.Show(
            $"Delete restore point '{point.Name}'?\n\nThis cannot be undone.",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            var success = await _saveSyncService.DeleteRestorePointAsync(_appId, restorePointId);

            if (success)
            {
                LoadRestorePoints();
            }
            else
            {
                MessageBox.Show(
                    "Failed to delete restore point.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private void RestorePointItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Border border)
        {
            border.BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#58A6FF"));
        }
    }

    private void RestorePointItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Border border)
        {
            border.BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#30363D"));
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
