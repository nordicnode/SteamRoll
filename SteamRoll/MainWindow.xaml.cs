using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SteamRoll.Controls;
using SteamRoll.Models;
using SteamRoll.Services;
using SteamRoll.Services.Transfer;

namespace SteamRoll;

/// <summary>
/// Main application window - handles game library display and user interactions.
/// </summary>
public partial class MainWindow : Window
{
    private readonly SteamLocator _steamLocator;
    private readonly LibraryScanner _libraryScanner;
    private readonly PackageScanner _packageScanner;
    private readonly GoldbergService _goldbergService;
    private readonly PackageBuilder _packageBuilder;
    private readonly DlcService _dlcService;
    private readonly CacheService _cacheService;
    private readonly LanDiscoveryService _lanDiscoveryService;
    private readonly TransferService _transferService;
    private readonly SettingsService _settingsService;
    private readonly SaveGameService _saveGameService;
    private readonly UpdateService _updateService;
    private readonly IntegrityService _integrityService;
    private readonly LibraryManager _libraryManager;
    private CancellationTokenSource? _currentOperationCts;
    private CancellationTokenSource? _scanCts;
    private bool _isLibraryViewActive = true;
    private List<InstalledGame> _allGames = new();
    private readonly object _gamesLock = new(); // Thread-safety lock for _allGames modifications
    private string _outputPath;

    // Per-game Goldberg configuration storage
    private readonly Dictionary<int, GoldbergConfig> _gameGoldbergConfigs = new();

    /// <summary>
    /// Gets a thread-safe snapshot of all games for read operations.
    /// </summary>
    private List<InstalledGame> GetGamesSnapshot()
    {
        lock (_gamesLock)
        {
            return _allGames.ToList();
        }
    }
    
    /// <summary>
    /// Gets a game by AppId in a thread-safe manner.
    /// </summary>
    private InstalledGame? FindGameByAppId(int appId)
    {
        lock (_gamesLock)
        {
            return _allGames.FirstOrDefault(g => g.AppId == appId);
        }
    }
    
    /// <summary>
    /// Finds a game by predicate in a thread-safe manner.
    /// </summary>
    private InstalledGame? FindGame(Func<InstalledGame, bool> predicate)
    {
        lock (_gamesLock)
        {
            return _allGames.FirstOrDefault(predicate);
        }
    }


    public MainWindow()
    {
        InitializeComponent();

        // Initialize logging
        LogService.Instance.Info("SteamRoll starting up", "App");
        LogService.Instance.CleanupOldLogs();
        
        // Load settings
        _settingsService = new SettingsService();
        _settingsService.Load();
        
        // Initialize services
        _steamLocator = new SteamLocator();
        _libraryScanner = new LibraryScanner(_steamLocator);
        _goldbergService = new GoldbergService();
        _dlcService = new DlcService();
        _packageScanner = new PackageScanner(_settingsService);
        _cacheService = new CacheService();
        _packageBuilder = new PackageBuilder(_goldbergService, _settingsService, _dlcService);
        
        // Initialize LibraryManager for library operations
        _libraryManager = new LibraryManager(
            _steamLocator, _libraryScanner, _packageScanner, 
            _cacheService, _dlcService, _settingsService);
        _libraryManager.ProgressChanged += status => Dispatcher.Invoke(() => StatusText.Text = status);
        
        // Set output path from settings
        _outputPath = _settingsService.Settings.OutputPath;
        
        // Initialize LAN services
        _lanDiscoveryService = new LanDiscoveryService();
        _transferService = new TransferService(_outputPath);
        
        // Initialize SaveGameService
        _saveGameService = new SaveGameService(_settingsService);

        // Initialize IntegrityService
        _integrityService = new IntegrityService();

        // Initialize update service
        _updateService = new UpdateService(_goldbergService.GoldbergPath);
        _updateService.UpdateAvailable += OnUpdateAvailable;
        
        // Subscribe to progress updates
        _packageBuilder.ProgressChanged += OnPackageProgress;
        _transferService.ProgressChanged += OnTransferProgress;
        _transferService.TransferComplete += OnTransferComplete;
        _transferService.TransferApprovalRequested += OnTransferApprovalRequested;
        _transferService.LocalLibraryRequested += OnLocalLibraryRequested;
        _transferService.PullPackageRequested += OnPullPackageRequested;
        _lanDiscoveryService.PeerDiscovered += OnPeerDiscovered;
        _lanDiscoveryService.PeerLost += OnPeerLost;
        _lanDiscoveryService.TransferRequested += OnTransferRequested;
        
        // Handle transfer errors
        _transferService.Error += (s, msg) => 
        {
            LogService.Instance.Error($"Transfer error: {msg}", category: "Transfer");
            Dispatcher.Invoke(() => ToastService.Instance.Show(msg, "Error", ToastType.Error));
        };
        
        // Initialize Goldberg directory (creates setup instructions)
        _goldbergService.InitializeGoldbergDirectory();
        
        // Initialize Toast Service
        ToastService.Instance.Initialize(ToastContainer);
        
        // Set search placeholder
        HeaderControl.SearchText = "🔍 Search games...";
        
        // Apply saved window dimensions and state
        if (_settingsService.Settings.WindowWidth > 0 && _settingsService.Settings.WindowHeight > 0)
        {
            Width = _settingsService.Settings.WindowWidth;
            Height = _settingsService.Settings.WindowHeight;
        }

        if (!double.IsNaN(_settingsService.Settings.WindowTop)) Top = _settingsService.Settings.WindowTop;
        if (!double.IsNaN(_settingsService.Settings.WindowLeft)) Left = _settingsService.Settings.WindowLeft;

        if (Enum.TryParse<WindowState>(_settingsService.Settings.WindowState, out var savedState))
        {
            WindowState = savedState;
        }

        // Load games on startup
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        KeyDown += MainWindow_KeyDown;

        
        // Subscribe to GameDetailsView events
        GameDetailsView.BackRequested += OnDetailsBackRequested;
        GameDetailsView.PackageRequested += OnDetailsPackageRequested;
    }
    

    
    private async Task CheckForIncompletePackages()
    {
        var outputPath = _settingsService.Settings.OutputPath; // Changed from OutputDirectory to OutputPath based on existing code
        if (string.IsNullOrEmpty(outputPath)) return;
        
        var incompletePackages = await Task.Run(() => PackageBuilder.FindIncompletePackages(outputPath));
        
        if (incompletePackages.Count > 0)
        {
            var msg = incompletePackages.Count == 1 
                ? $"Found an incomplete package for {incompletePackages[0].GameName}. Would you like to resume it?" 
                : $"Found {incompletePackages.Count} incomplete packages. Would you like to resume them?";
                
            var result = MessageBox.Show(msg, "Resume Packaging", MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                foreach (var pkg in incompletePackages)
                {
                    await ResumePackage(pkg);
                }
            }
        }
    }
    
    private async Task ResumePackage(PackageState state)
    {
        var game = FindGameByAppId(state.AppId);
        
        // If game not found in library (e.g. library path changed), try to construct minimalist object
        if (game == null)
        {
            if (Directory.Exists(state.SourcePath))
            {
                game = new InstalledGame 
                {
                    AppId = state.AppId,
                    Name = state.GameName,
                    FullPath = state.SourcePath,
                    InstallDir = System.IO.Path.GetFileName(state.SourcePath)
                };
            }
            else
            {
                ToastService.Instance.ShowError("Resume Failed", $"Source path not found for {state.GameName}");
                return;
            }
        }
        
        var mode = PackageMode.Goldberg;
        if (Enum.TryParse<PackageMode>(state.Mode, out var parsedMode))
        {
            mode = parsedMode;
        } else if (state.Mode == "CreamApi") {
            mode = PackageMode.CreamApi;
        }

        await CreatePackageAsync(game, mode, state);
    }
    
    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        // Cancel current operation with Escape key
        if (e.Key == Key.Escape && _currentOperationCts != null)
        {
            _currentOperationCts.Cancel();
            StatusText.Text = "⏳ Cancelling operation...";
            e.Handled = true;
        }
    }

    // ============================================
    // Window Chrome Handlers
    // ============================================
    
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Double-click to maximize/restore
            MaximizeButton_Click(sender, e);
        }
        else if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
    
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }
    
    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeButton.Content = "☐";
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeButton.Content = "❐";
        }
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }


    

    private void OnPackageProgress(string status, int percentage)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"[{percentage}%] {status}";
            LoadingOverlay.UpdateProgress(status, percentage);
        });
    }
    
    private void OnGoldbergDownloadProgress(string status, int percentage)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"🔧 [{percentage}%] {status}";
        });
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Subscribe to Goldberg download progress
        _goldbergService.DownloadProgressChanged += OnGoldbergDownloadProgress;
        
        // Check if we need Defender exclusions (before downloading)
        if (!_goldbergService.IsGoldbergAvailable())
        {
            await EnsureDefenderExclusionsAsync();
            
            StatusText.Text = "🔧 Goldberg Emulator not found - Downloading automatically...";
            
            var success = await _goldbergService.DownloadGoldbergAsync();
            
            if (success)
            {
                StatusText.Text = "✓ Goldberg Emulator installed successfully!";
            }
            else
            {
                StatusText.Text = "⚠ Could not download Goldberg - packages will need manual setup";
            }
        }
        else
        {
            StatusText.Text = "✓ Goldberg Emulator ready";
            
            // Check for Goldberg updates in the background
            SafeFireAndForget(_updateService.CheckGoldbergUpdateAsync(), "Update Check");
        }
        
        // Scan Steam libraries
        _scanCts = new CancellationTokenSource();
        await ScanLibraryAsync(_scanCts.Token);
        
        // Start LAN services for peer discovery
        _lanDiscoveryService.Start();
        _transferService.StartListening();
        UpdateNetworkStatus();
        
        // Check for incomplete packages after loading
        await CheckForIncompletePackages();
    }
    
    private void OnUpdateAvailable(object? sender, UpdateAvailableEventArgs e)
    {
        Dispatcher.Invoke(async () =>
        {
            var result = MessageBox.Show(
                $"{e.Update.Name} Update Available!\n\n" +
                $"Current version: {e.Update.CurrentVersion}\n" +
                $"New version: {e.Update.LatestVersion}\n\n" +
                "Would you like to update now?",
                "Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            
            if (result == MessageBoxResult.Yes)
            {
                await PerformGoldbergUpdateAsync();
            }
            else
            {
                ToastService.Instance.ShowInfo(
                    "Update Skipped",
                    "You can update later via Settings or by restarting the app."
                );
            }
        });
    }
    
    private async Task PerformGoldbergUpdateAsync()
    {
        LoadingOverlay.Show("Updating Goldberg Emulator...");
        StatusText.Text = "⏳ Downloading Goldberg update...";
        
        // Subscribe to progress updates
        void OnProgress(string status, int percentage)
        {
            Dispatcher.Invoke(() =>
            {
                LoadingOverlay.UpdateProgress(status, percentage);
                StatusText.Text = $"⏳ {status}";
            });
        }
        
        _goldbergService.DownloadProgressChanged += OnProgress;
        
        try
        {
            var success = await _goldbergService.DownloadGoldbergAsync();
            LoadingOverlay.Hide();
            
            if (success)
            {
                StatusText.Text = "✓ Goldberg Emulator updated successfully!";
                ToastService.Instance.ShowSuccess(
                    "Update Complete",
                    "Goldberg Emulator has been updated to the latest version."
                );
            }
            else
            {
                StatusText.Text = "⚠ Goldberg update failed";
                ToastService.Instance.ShowError(
                    "Update Failed",
                    "Failed to download Goldberg update. Check logs for details."
                );
            }
        }
        catch (Exception ex)
        {
            LoadingOverlay.Hide();
            StatusText.Text = $"⚠ Update error: {ex.Message}";
            ToastService.Instance.ShowError("Update Failed", ex.Message);
            LogService.Instance.Error("Goldberg update failed", ex, "Update");
        }
        finally
        {
            _goldbergService.DownloadProgressChanged -= OnProgress;
        }
    }


    
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        LogService.Instance.Info("SteamRoll shutting down", "App");
        
        // Save window size and state to settings
        _settingsService.Update(s =>
        {
            if (WindowState == WindowState.Normal)
            {
                s.WindowWidth = Width;
                s.WindowHeight = Height;
                s.WindowTop = Top;
                s.WindowLeft = Left;
            }
            s.WindowState = WindowState.ToString();
        });
        
        // Unsubscribe from events to prevent memory leaks
        _updateService.UpdateAvailable -= OnUpdateAvailable;
        _packageBuilder.ProgressChanged -= OnPackageProgress;
        _transferService.ProgressChanged -= OnTransferProgress;
        _transferService.TransferComplete -= OnTransferComplete;
        _transferService.TransferApprovalRequested -= OnTransferApprovalRequested;
        _transferService.LocalLibraryRequested -= OnLocalLibraryRequested;
        _transferService.PullPackageRequested -= OnPullPackageRequested;
        _lanDiscoveryService.PeerDiscovered -= OnPeerDiscovered;
        _lanDiscoveryService.PeerLost -= OnPeerLost;
        _lanDiscoveryService.TransferRequested -= OnTransferRequested;
        
        // Clean up LAN services
        _lanDiscoveryService.Stop();
        _transferService.StopListening();
        _lanDiscoveryService.Dispose();
        _transferService.Dispose();
        
        // Dispose services with IDisposable
        _goldbergService.Dispose();
        _dlcService.Dispose();
        
        // Flush logs before exit
        LogService.Instance.Dispose();
    }


    /// <summary>
    /// Ensures Windows Defender exclusions are in place to prevent false positive detections.
    /// Now uses improved dialog with user consent and "never ask again" option.
    /// </summary>
    private async Task EnsureDefenderExclusionsAsync()
    {
        // Check if user has disabled this prompt
        if (_settingsService.Settings.DefenderExclusionsAdded)
            return;
            
        // Check if exclusions are actually needed
        if (!DefenderExclusionHelper.NeedsExclusions())
        {
            _settingsService.Update(s => s.DefenderExclusionsAdded = true);
            return;
        }

        StatusText.Text = "🛡️ Checking Windows Defender exclusions...";

        if (DefenderExclusionHelper.IsRunningAsAdmin())
        {
            // We have admin rights - ask user if they want to add exclusions
            var (shouldProceed, neverAskAgain) = DefenderExclusionHelper.GetUserConfirmation(this);
            
            if (neverAskAgain)
            {
                _settingsService.Update(s => s.DefenderExclusionsAdded = true); // Mark as "handled" (user opted out)
                return;
            }
            
            if (shouldProceed)
            {
                StatusText.Text = "🛡️ Adding Windows Defender exclusions...";
                var exclusionPaths = DefenderExclusionHelper.GetSteamRollExclusionPaths();
                DefenderExclusionHelper.AddExclusions(exclusionPaths);
                _settingsService.Update(s => s.DefenderExclusionsAdded = true);
                await Task.Delay(500);
                ToastService.Instance.ShowSuccess("Defender Exclusions", "Windows Defender exclusions added successfully.");
            }
        }
        else
        {
            // Not admin - ask if user wants to elevate
            var (shouldProceed, neverAskAgain) = DefenderExclusionHelper.GetUserConfirmation(this);
            
            if (neverAskAgain)
            {
                _settingsService.Update(s => s.DefenderExclusionsAdded = true); // Mark as "handled" (user opted out)
                return;
            }
            
            if (shouldProceed)
            {
                if (DefenderExclusionHelper.RequestElevation())
                {
                    Application.Current.Shutdown();
                    return;
                }
            }
        }
    }

    private async Task ScanLibraryAsync(CancellationToken ct = default)
    {
        // Use Skeleton Loading instead of full Overlay
        GameLibraryViewControl.SetLoading(true);

        var steamPath = _libraryManager.GetSteamPath();
        if (steamPath == null)
        {
            GameLibraryViewControl.SetLoading(false);
            StatusText.Text = "⚠ Steam installation not found. Please ensure Steam is installed.";
            ToastService.Instance.ShowError("Steam Not Found", "Please ensure Steam is installed.");
            return;
        }

        try
        {
            // Use LibraryManager to scan libraries
            var scanResult = await _libraryManager.ScanLibrariesAsync(ct);
            ct.ThrowIfCancellationRequested();
            
            // Update local reference for UI (sync with manager)
            lock (_gamesLock)
            {
                _allGames = scanResult.AllGames;
            }
            
            // Only analyze games not in cache
            if (scanResult.GamesToAnalyze.Count > 0)
            {
                await _libraryManager.AnalyzeGamesForDrmAsync(scanResult.GamesToAnalyze, ct);
            }
            ct.ThrowIfCancellationRequested();
            
            // Check for existing packages
            _libraryManager.CheckExistingPackages(scanResult.AllGames);
            
            // Show games immediately
            ApplyFilters();
            UpdateStats(scanResult.AllGames);
            
            // Save updated cache
            _libraryManager.SaveCache(scanResult.AllGames);
            
            // Fetch DLC info for games without it (in background)
            var gamesNeedingDlc = _libraryManager.GetGamesNeedingDlc();
            if (gamesNeedingDlc.Count > 0)
            {
                StatusText.Text = $"Fetching DLC for {gamesNeedingDlc.Count} games...";
                SafeFireAndForget(_libraryManager.FetchDlcForGamesAsync(gamesNeedingDlc, ct), "DLC Fetch");
            }

            // Start Store Data Fetch (Reviews/Metacritic)
            SafeFireAndForget(_libraryManager.EnrichWithStoreDataAsync(_libraryManager.GetGamesSnapshot(), ct), "Store Enrich");
            
            // Resolve game images in background (tries multiple CDN sources for failed images)
            SafeFireAndForget(_libraryManager.ResolveGameImagesAsync(_libraryManager.GetGamesSnapshot(), ct), "Image Resolution");
            
            var packageableCount = scanResult.AllGames.Count(g => g.IsPackageable);
            var packagedCount = scanResult.AllGames.Count(g => g.IsPackaged);
            StatusText.Text = $"✓ {scanResult.AllGames.Count} games • {packageableCount} ready • {packagedCount} packaged";
            GameLibraryViewControl.SetLoading(false);
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation
        }
        catch (Exception ex)
        {
            GameLibraryViewControl.SetLoading(false);
            StatusText.Text = $"⚠ Error scanning library: {ex.Message}";
            ToastService.Instance.ShowError("Scan Failed", ex.Message);
        }
    }

    private async Task EnrichGamesWithStoreDataAsync(List<InstalledGame> games, CancellationToken ct, bool forceRefresh = false)
    {
        var enrichedCount = 0;
        
        // Process in batches
        foreach (var game in games)
        {
            if (ct.IsCancellationRequested) break;
            if (game.AppId <= 0) continue; 

            try
            {
                // Skip games that already have cached review data (unless forcing refresh)
                if (!forceRefresh && (game.HasReviewScore || game.HasMetacriticScore)) continue;

                var details = await SteamStoreService.Instance.GetGameDetailsAsync(game.AppId, ct);
                if (details != null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        game.ReviewPositivePercent = details.ReviewPositivePercent;
                        game.ReviewDescription = details.ReviewDescription;
                        game.MetacriticScore = details.MetacriticScore;
                    });
                    enrichedCount++;
                    
                    // Update cache for this game
                    _cacheService.UpdateCache(game);
                }
                
                await Task.Delay(50, ct); 
            }
            catch (Exception ex)
            {
                 LogService.Instance.Debug($"Failed to enrich {game.Name}: {ex.Message}", "StoreEnricher");
            }
        }
        
        // Save cache if we enriched any games
        if (enrichedCount > 0)
        {
            _cacheService.SaveCache();
            LogService.Instance.Info($"Enriched and cached review data for {enrichedCount} games", "StoreEnricher");
        }
    }

    private async Task ScanPackagesAsync(CancellationToken ct = default)
    {
        LoadingOverlay.Show("Scanning packaged games...");
        StatusText.Text = "Scanning packaged games...";

        try
        {
            var scannedPackages = await Task.Run(() => _packageScanner.ScanPackages(ct), ct);
            ct.ThrowIfCancellationRequested();

            lock (_gamesLock)
            {
                _allGames = scannedPackages;
            }

            // Apply cache if possible to get better metadata (images, etc)
            // Skip path validation since package path differs from original install path
            foreach (var game in scannedPackages)
            {
                _cacheService.ApplyCachedData(game, skipPathValidation: true);
            }

            UpdateGamesList(scannedPackages);

            // Simple stats for packages view
            StatsBarControl.UpdateStats(
                scannedPackages.Count,
                0,
                FormatUtils.FormatBytes(scannedPackages.Sum(g => g.SizeOnDisk))
            );

            StatusText.Text = $"✓ Found {scannedPackages.Count} packaged games";
            LoadingOverlay.Hide();
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation
        }
        catch (Exception ex)
        {
            LoadingOverlay.Hide();
            StatusText.Text = $"⚠ Error scanning packages: {ex.Message}";
            ToastService.Instance.ShowError("Scan Failed", ex.Message);
        }
    }

    private static string SanitizeFileName(string name) => FormatUtils.SanitizeFileName(name);

    private void UpdateGamesList(List<InstalledGame> games)
    {
        GameLibraryViewControl.SetGames(games);

        // Update view mode state inside the control (it handles hiding scroll/list)
        GameLibraryViewControl.SetViewMode(HeaderControl.IsViewModeList);

        // Update empty state message based on context
        if (games.Count == 0 && _allGames.Count > 0)
        {
            GameLibraryViewControl.SetEmptyStateMessage("No Matching Games", "No games match your current filters. Try adjusting your filters or search.");
        }
        else if (games.Count == 0)
        {
            GameLibraryViewControl.SetEmptyStateMessage("No Games Found", "No Steam games were detected. Try adding Steam library paths in Settings or refreshing the library.");
        }
    }

    private void UpdateStats(List<InstalledGame> games)
    {
        var (total, installed, size) = _libraryScanner.GetLibraryStats(games);
        var packageable = games.Count(g => g.IsPackageable);
        
        StatsBarControl.UpdateStats(total, packageable, FormatUtils.FormatBytes(size));
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        // Cancel existing
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        // Refresh based on current view
        if (_isLibraryViewActive)
        {
            SafeFireAndForget(ScanLibraryAsync(_scanCts.Token), "Library Scan");
        }
        else
        {
            SafeFireAndForget(ScanPackagesAsync(_scanCts.Token), "Package Scan");
        }
    }


    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_outputPath))
        {
            Directory.CreateDirectory(_outputPath);
        }
        Process.Start("explorer.exe", _outputPath);
    }
    
    private async void ImportPackageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import SteamRoll Package",
            Filter = "Zip Files (*.zip)|*.zip",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                LoadingOverlay.Show("Importing package...");

                var importedPath = await _packageBuilder.ImportPackageAsync(dialog.FileName, _outputPath);

                LoadingOverlay.Hide();
                ToastService.Instance.ShowSuccess("Import Successful", $"Imported to {Path.GetFileName(importedPath)}");

                // Refresh to show new package
                _scanCts?.Cancel();
                _scanCts = new CancellationTokenSource();
                await ScanPackagesAsync(_scanCts.Token);
            }
            catch (Exception ex)
            {
                LoadingOverlay.Hide();
                MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_settingsService, _cacheService)
        {
            Owner = this
        };
        
        if (settingsWindow.ShowDialog() == true && settingsWindow.ChangesSaved)
        {
            // Apply any settings that need immediate effect
            _outputPath = _settingsService.Settings.OutputPath;
            _transferService.UpdateReceivePath(_outputPath);
            
            ToastService.Instance.ShowSuccess("Settings Saved", "Your changes have been applied.");
        }
    }

    private void TransfersButton_Click(object sender, RoutedEventArgs e)
    {
        // Show the Transfers section view
        ShowTransfersView();
    }

    private void ShowTransfersView()
    {
        // Hide other views
        GameLibraryViewControl.Visibility = Visibility.Collapsed;
        GameDetailsView.Visibility = Visibility.Collapsed;
        StatsBarControl.Visibility = Visibility.Collapsed;
        
        // Show transfers view
        TransfersViewControl.Visibility = Visibility.Visible;
    }

    private void TransfersView_BackClicked(object sender, RoutedEventArgs e)
    {
        // Hide transfers view
        TransfersViewControl.Visibility = Visibility.Collapsed;
        
        // Restore library view
        StatsBarControl.Visibility = Visibility.Visible;
        GameLibraryViewControl.Visibility = Visibility.Visible;
    }





    private async void PackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is InstalledGame game)
        {
            // If already packaged, open the folder instead
            if (game.IsPackaged && !string.IsNullOrEmpty(game.PackagePath))
            {
                try
                {
                    Process.Start("explorer.exe", game.PackagePath);
                }
                catch (Exception ex)
                {
                    LogService.Instance.Error($"Failed to open package folder: {ex.Message}", ex, "MainWindow");
                }
                return;
            }
            
            await PackageGameAsync(game, button);
        }
    }

    private async Task PackageGameAsync(InstalledGame game, Button? button = null)
    {
        if (button != null)
        {
            button.IsEnabled = false;
        }
        
        // Create cancellation token for this operation
        _currentOperationCts?.Cancel();
        _currentOperationCts = new CancellationTokenSource();
        var ct = _currentOperationCts.Token;
        
        var isUpdate = game.IsPackaged && game.UpdateAvailable;
        var actionText = isUpdate ? "Updating package for" : "Packaging";

        LoadingOverlay.Show($"{actionText} {game.Name}... (Press ESC to cancel)");
        StatusText.Text = $"⏳ {actionText} {game.Name}...";

        try
        {
            // Look up any stored Goldberg config for this game
            _gameGoldbergConfigs.TryGetValue(game.AppId, out var goldbergConfig);

            var options = new PackageOptions
            {
                IsUpdate = isUpdate,
                GoldbergConfig = goldbergConfig,
                Mode = _settingsService.Settings.DefaultPackageMode
            };

            // Use the new PackageBuilder with Goldberg integration
            var packagePath = await _packageBuilder.CreatePackageAsync(game, _outputPath, options, ct);
            
            // Update game's package status
            game.IsPackaged = true;
            game.PackagePath = packagePath;
            game.LastPackaged = DateTime.Now;
            _cacheService.UpdateCache(game);
            _cacheService.SaveCache();
            
            var goldbergStatus = _goldbergService.IsGoldbergAvailable() 
                ? "Goldberg Emulator applied!" 
                : "Manual Goldberg setup required.";
            
            StatusText.Text = $"✓ Packaged {game.Name} - {goldbergStatus}";
            GameLibraryViewControl.RefreshList();
            LoadingOverlay.Hide();
            
            ToastService.Instance.ShowSuccess("Package Complete", $"{game.Name} packaged successfully!", 5000);
        }
        catch (OperationCanceledException)
        {
            LoadingOverlay.Hide();
            StatusText.Text = $"⚠ Packaging cancelled for {game.Name}";
            ToastService.Instance.ShowWarning("Packaging Cancelled", $"{game.Name} packaging was cancelled.");
        }
        catch (Exception ex)
        {
            LoadingOverlay.Hide();
            StatusText.Text = $"⚠ Failed to package {game.Name}: {ex.Message}";
            ToastService.Instance.ShowError("Package Failed", ex.Message);
        }
        finally
        {
            _currentOperationCts = null;
            if (button != null)
            {
                button.IsEnabled = true;
            }
        }
    }


    private void PackageGameAsync(InstalledGame game)
    {
        // Wrapper for callback from GameDetailsWindow
        _ = PackageGameAsync(game, null);
    }
    
    private async Task CreatePackageAsync(InstalledGame game, PackageMode mode, PackageState? resumeState = null)
    {
        // Create cancellation token for this operation
        _currentOperationCts?.Cancel();
        _currentOperationCts = new CancellationTokenSource();
        var ct = _currentOperationCts.Token;
        
        var isUpdate = game.IsPackaged && game.UpdateAvailable && resumeState == null;
        var actionText = isUpdate ? "Updating package for" : "Packaging";

        LoadingOverlay.Show($"{actionText} {game.Name}... (Press ESC to cancel)");
        StatusText.Text = $"⏳ {actionText} {game.Name}...";
        
        try
        {
            // Look up any stored Goldberg config for this game
            _gameGoldbergConfigs.TryGetValue(game.AppId, out var goldbergConfig);
            
            // Create package options with specified mode
            var options = new PackageOptions
            {
                Mode = mode,
                IncludeDlc = true,
                GoldbergConfig = goldbergConfig,
                IsUpdate = isUpdate
            };
            
            // Start packaging process with optional resume state
            var packagePath = await _packageBuilder.CreatePackageAsync(game, _outputPath, options, ct, resumeState);
            
            // Update game status
            game.IsPackaged = true;
            game.PackagePath = packagePath;
            
            // We just created it, so build ID matches
            game.PackageBuildId = game.BuildId;
            game.LastPackaged = DateTime.Now;
            
            _cacheService.UpdateCache(game);
            _cacheService.SaveCache();
            
            GameLibraryViewControl.RefreshList();
            LoadingOverlay.Hide();
            
            ToastService.Instance.ShowSuccess("Packaging Complete", $"Successfully packaged {game.Name}!");
            StatusText.Text = $"✓ Packaged {game.Name}";
        }
        catch (OperationCanceledException)
        {
            LoadingOverlay.Hide();
            StatusText.Text = $"⚠ Packaging cancelled for {game.Name}";
            ToastService.Instance.ShowWarning("Packaging Cancelled", $"{game.Name} packaging was cancelled.");
        }
        catch (Exception ex)
        {
            LoadingOverlay.Hide();
            StatusText.Text = $"⚠ Failed to package {game.Name}: {ex.Message}";
            ToastService.Instance.ShowError("Package Failed", ex.Message);
        }
        finally
        {
            _currentOperationCts = null;
        }
    }

    
    private async void SendToPeerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is InstalledGame game)
        {
            if (!game.IsPackaged || string.IsNullOrEmpty(game.PackagePath))
            {
                MessageBox.Show("This game has not been packaged yet.", "Not Packaged", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            var peers = _lanDiscoveryService.GetPeers();
            
            if (peers.Count == 0)
            {
                MessageBox.Show(
                    "No peers found on the network.\n\n" +
                    "Make sure another SteamRoll instance is running on your LAN.",
                    "No Peers",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
                return;
            }
            
            // If only one peer, send directly; otherwise show selection
            PeerInfo selectedPeer;
            if (peers.Count == 1)
            {
                selectedPeer = peers[0];
            }
            else
            {
                // Show peer selection dialog
                var dialog = new Controls.PeerSelectionDialog(peers)
                {
                    Owner = this
                };
                
                if (dialog.ShowDialog() != true || dialog.SelectedPeer == null)
                {
                    return;
                }
                
                selectedPeer = dialog.SelectedPeer;
            }
            
            // Confirm transfer
            var confirm = MessageBox.Show(
                $"Send \"{game.Name}\" to {selectedPeer.HostName}?\n\n" +
                $"Size: {game.FormattedSize}",
                "Confirm Transfer",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );
            
            if (confirm != MessageBoxResult.Yes) return;
            
            // Start transfer
            StatusText.Text = $"📤 Sending {game.Name} to {selectedPeer.HostName}...";
            button.IsEnabled = false;
            
            try
            {
                var success = await _transferService.SendPackageAsync(
                    selectedPeer.IpAddress,
                    selectedPeer.TransferPort,
                    game.PackagePath
                );
                
                if (success)
                {
                    StatusText.Text = $"✓ Successfully sent {game.Name} to {selectedPeer.HostName}";
                }
                else
                {
                    StatusText.Text = $"⚠ Failed to send {game.Name}";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"⚠ Transfer error: {ex.Message}";
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }
    
    // ============================================
    // Utility Methods
    // ============================================
    
    /// <summary>
    /// Safely executes an async task in fire-and-forget mode with error handling.
    /// Any exceptions are logged and shown as error toasts.
    /// </summary>
    /// <param name="task">The task to execute.</param>
    /// <param name="operationName">Name of the operation for error messages.</param>
    private static async void SafeFireAndForget(Task task, string operationName)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected, don't log as error
            LogService.Instance.Info($"{operationName} was cancelled.", "MainWindow");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"{operationName} failed: {ex.Message}", ex, "MainWindow");
            try
            {
                ToastService.Instance.ShowError($"{operationName} Failed", ex.Message);
            }
            catch
            {
                // Ignore if toast service isn't available
            }
        }
    }
    
    private void FavoriteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is InstalledGame game)
        {
            _cacheService.UpdateCache(game);
            _cacheService.SaveCache();
            // Don't ApplyFilters immediately to avoid items jumping around while user is interacting
        }
    }

    private async void BrowsePeerButton_Click(object sender, RoutedEventArgs e)
    {
        var peers = _lanDiscoveryService.GetPeers();
        if (peers.Count == 0)
        {
            ToastService.Instance.ShowInfo("No Peers", "No peers found to browse.");
            return;
        }

        PeerInfo selectedPeer;
        if (peers.Count == 1)
        {
            selectedPeer = peers[0];
        }
        else
        {
            var dialog = new PeerSelectionDialog(peers) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedPeer == null) return;
            selectedPeer = dialog.SelectedPeer;
        }

        var remoteLibWindow = new RemoteLibraryWindow(_transferService, selectedPeer) { Owner = this };
        if (remoteLibWindow.ShowDialog() == true && remoteLibWindow.SelectedGame != null)
        {
            var game = remoteLibWindow.SelectedGame;
            StatusText.Text = $"⏳ Requesting {game.Name} from {selectedPeer.HostName}...";

            try
            {
                await _transferService.RequestPullPackageAsync(selectedPeer.IpAddress, selectedPeer.TransferPort, game.Name);
                ToastService.Instance.ShowSuccess("Request Sent", $"Asked {selectedPeer.HostName} to send {game.Name}");
            }
            catch (Exception ex)
            {
                ToastService.Instance.ShowError("Request Failed", ex.Message);
            }
        }
    }

    // ============================================
    // Keyboard Shortcuts
    // ============================================
    
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        
        // Don't handle if user is typing in a text box
        if (e.OriginalSource is TextBox) return;
        
        // Ctrl+R: Refresh library
        if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
        {
            RefreshButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        // Ctrl+F: Focus search box
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            HeaderControl.FocusSearch();
            e.Handled = true;
        }
        // Ctrl+S: Open settings
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SettingsButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        // Escape: Cancel current operation (existing behavior)
        else if (e.Key == Key.Escape)
        {
            _currentOperationCts?.Cancel();
            e.Handled = true;
        }
    }
}
