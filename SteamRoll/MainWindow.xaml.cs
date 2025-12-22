using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SteamRoll.Controls;
using SteamRoll.Models;
using SteamRoll.Services;

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


    
    // ============================================
    // View Switching Methods
    // ============================================

    private void ViewModeToggle_Click(object sender, RoutedEventArgs e)
    {
        // Toggle UI logic delegates to the control
        GameLibraryViewControl.SetViewMode(HeaderControl.IsViewModeList);
        HeaderControl.SetViewModeIcon(HeaderControl.IsViewModeList);
    }
    
    private void ShowDetailsView(InstalledGame game)
    {
        GameLibraryViewControl.Visibility = Visibility.Collapsed;
        GameDetailsView.Visibility = Visibility.Visible;
        SafeFireAndForget(GameDetailsView.LoadGameAsync(game), "Load Game Details");

    }
    
    private void ShowLibraryView()
    {
        _isLibraryViewActive = true;
        GameDetailsView.Visibility = Visibility.Collapsed;
        GameLibraryViewControl.Visibility = Visibility.Visible;
        
        // Update tab button styles via control method
        HeaderControl.SetLibraryTabActive(true);

        // Cancel any running scan
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        SafeFireAndForget(ScanLibraryAsync(_scanCts.Token), "Scan Library");
    }
    
    private void ShowPackagesView()
    {
        _isLibraryViewActive = false;
        GameDetailsView.Visibility = Visibility.Collapsed;
        GameLibraryViewControl.Visibility = Visibility.Visible; // Reuse library view layout

        // Update tab button styles via control method
        HeaderControl.SetLibraryTabActive(false);

        // Cancel any running scan
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        SafeFireAndForget(ScanPackagesAsync(_scanCts.Token), "Scan Packages");
    }

    private void LibraryTab_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryView();
    }

    private void PackagesTab_Click(object sender, RoutedEventArgs e)
    {
        ShowPackagesView();
    }
    
    private void OnDetailsBackRequested(object? sender, EventArgs e)
    {
        ShowLibraryView();
    }
    
    private async void OnDetailsPackageRequested(object? sender, (InstalledGame Game, PackageMode Mode) args)
    {
        try
        {
            await CreatePackageAsync(args.Game, args.Mode);
            // Refresh and stay in details view so user can see updated package status
            SafeFireAndForget(GameDetailsView.LoadGameAsync(args.Game), "Refresh Game Details");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("Failed to create package from details view", ex, "MainWindow");
            ToastService.Instance.ShowError("Package Failed", ex.Message);
        }
    }
    
    private void GameCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is InstalledGame game)
        {
            ShowDetailsView(game);
        }
        else
        {
            LogService.Instance.Warning($"GameCard_Click received unexpected sender type or Tag: {sender?.GetType().Name}", "MainWindow");
        }
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

    
    private void UpdateNetworkStatus()
    {
        var peerCount = _lanDiscoveryService.GetPeers().Count;
        Dispatcher.Invoke(() =>
        {
            StatsBarControl.UpdateNetworkStatus(peerCount);
            HeaderControl.HasPeers = peerCount > 0;
        });
    }
    
    private void OnPeerDiscovered(object? sender, PeerInfo peer)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateNetworkStatus();
            StatusText.Text = $"🔗 Found peer: {peer.HostName}";
            ToastService.Instance.ShowInfo("Peer Found", $"Connected to {peer.HostName}");
        });
    }
    
    private void OnPeerLost(object? sender, PeerInfo peer)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateNetworkStatus();
        });
    }
    
    private void OnTransferProgress(object? sender, TransferProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            var direction = progress.IsSending ? "📤 Sending" : "📥 Receiving";
            StatusText.Text = $"{direction} {progress.GameName}: {progress.FormattedProgress}";
        });
    }
    
    private void OnTransferComplete(object? sender, TransferResult result)
    {
        Dispatcher.Invoke(async () =>
        {
            if (result.Success)
            {
                var action = result.WasReceived ? "received" : "sent";
                
                if (result.WasReceived)
                {
                    if (result.IsSaveSync)
                    {
                        // Handle Save Sync Restoration
                        try
                        {
                            // We assume GameName is just the name of the game for now.
                            // To actually restore, we need the AppID.
                            // Ideally, the game name string should allow us to find the InstalledGame.
                            var game = _allGames.FirstOrDefault(g => g.Name.Equals(result.GameName, StringComparison.OrdinalIgnoreCase));

                            if (game != null)
                            {
                                // Prompt user before overwriting
                                var confirm = MessageBox.Show(
                                    $"Received updated saves for {game.Name}. Overwrite local saves?",
                                    "Save Sync Received",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Question);

                                if (confirm == MessageBoxResult.Yes)
                                {
                                    await _saveGameService.RestoreSavesAsync(result.Path, game.AppId, game.PackagePath);
                                    StatusText.Text = $"✓ Synced saves for {result.GameName}";
                                    ToastService.Instance.ShowSuccess("Save Sync", "Local saves updated successfully.");
                                }
                                else
                                {
                                    StatusText.Text = $"⚠ Save sync skipped for {result.GameName}";
                                }
                            }
                            else
                            {
                                ToastService.Instance.ShowWarning("Save Sync", $"Received saves for unknown game: {result.GameName}");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.Instance.Error($"Failed to restore saves: {ex.Message}", ex, "SaveSync");
                            ToastService.Instance.ShowError("Save Restore Failed", ex.Message);
                        }
                        finally
                        {
                            // Cleanup temp zip
                            try { File.Delete(result.Path); } catch (Exception ex) { LogService.Instance.Debug($"Could not delete temp file: {ex.Message}", "MainWindow"); }
                        }
                        return;
                    }

                    // Show verification status for received packages
                    if (result.VerificationPassed)
                    {
                        StatusText.Text = $"✓ Successfully {action}: {result.GameName} (Verified ✓)";
                        ToastService.Instance.ShowSuccess("Transfer Complete", 
                            $"Successfully {action} {result.GameName}\n✓ Package integrity verified");
                    }
                    else
                    {
                        var errorSummary = result.VerificationErrors.Count > 0 
                            ? string.Join(", ", result.VerificationErrors.Take(3)) 
                            : "Unknown verification error";
                        StatusText.Text = $"⚠ {action}: {result.GameName} (Verification failed)";
                        ToastService.Instance.ShowWarning("Transfer Complete - Verification Failed", 
                            $"{result.GameName} was received but verification failed:\n{errorSummary}");
                    }
                    
                    // Refresh the game list to show received packages if we are in Packages view or just generally update
                    // Since we received a package, it makes sense to ensure it's visible.
                    // However, we shouldn't arbitrarily switch views or interrupt user.
                    // Let's just refresh current view if appropriate.
                    // For now, minimal intrusion:
                    _scanCts?.Cancel();
                    _scanCts = new CancellationTokenSource();
                    await ScanPackagesAsync(_scanCts.Token);
                }
                else
                {
                    StatusText.Text = $"✓ Successfully {action}: {result.GameName}";
                    ToastService.Instance.ShowSuccess("Transfer Complete", $"Successfully {action} {result.GameName}");
                }
            }
            else
            {
                StatusText.Text = $"⚠ Transfer failed: {result.GameName}";
                ToastService.Instance.ShowError("Transfer Failed", $"Failed to transfer {result.GameName}");
            }
        });
    }
    
    /// <summary>
    /// Handles incoming transfer approval requests - this is the new proper mechanism.
    /// User's choice actually affects whether the transfer proceeds.
    /// </summary>
    private void OnTransferApprovalRequested(object? sender, TransferApprovalEventArgs e)
    {
        // Must invoke on UI thread synchronously to get user response
        Dispatcher.Invoke(() =>
        {
            // Standard transfer message - we analyze for updates AFTER approval to prevent UI hang
            var message = $"📥 Incoming game transfer request:\n\n" +
                          $"Game: {e.GameName}\n" +
                          $"Size: {e.FormattedSize}\n" +
                          $"Files: {e.FileCount}\n\n" +
                          "Do you want to accept this transfer?";

            var result = MessageBox.Show(
                message,
                "Incoming Transfer Request",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No // Default to No for safety
            );
            
            var approved = (result == MessageBoxResult.Yes);
            e.SetApproval(approved);
            
            if (approved)
            {
                StatusText.Text = $"✓ Accepted transfer of {e.GameName}";
                ToastService.Instance.ShowInfo("Transfer Accepted", $"Receiving {e.GameName}...");
            }
            else
            {
                StatusText.Text = $"✗ Rejected transfer of {e.GameName}";
                ToastService.Instance.ShowWarning("Transfer Rejected", $"Declined transfer of {e.GameName}");
            }
        });
    }

    
    /// <summary>
    /// Handles transfer request notifications from LAN discovery (informational only).
    /// The actual accept/reject happens in OnTransferApprovalRequested.
    /// </summary>
    private void OnTransferRequested(object? sender, TransferRequest request)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"📥 Transfer request from {request.FromHostName}: {request.GameName}";
        });
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
            foreach (var game in scannedPackages)
            {
                _cacheService.ApplyCachedData(game);
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

    /// <summary>
    /// Analyzes all games for DRM protection in parallel with progress updates.
    /// </summary>
    private async Task AnalyzeGamesForDrmAsync(List<InstalledGame> games)
    {
        var total = games.Count;
        var completed = 0;

        // Analyze in parallel but limit concurrency to avoid I/O saturation
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        
        await Task.Run(() =>
        {
            Parallel.ForEach(games, options, game =>
            {
                game.Analyze();
                
                var current = Interlocked.Increment(ref completed);
                
                // Update status periodically (every 10 games or at milestones)
                if (current % 10 == 0 || current == total)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"Analyzing DRM: {current}/{total} games...";
                    });
                }
            });
        });
    }

    /// <summary>
    /// Fetches DLC information for all games in the background.
    /// Updates the UI as data comes in.
    /// </summary>
    private async Task FetchDlcForGamesAsync(List<InstalledGame> games)
    {
        var total = games.Count;
        var completed = 0;
        var gamesWithDlc = 0;

        foreach (var game in games)
        {
            try
            {
                var dlcList = await _dlcService.GetDlcListAsync(game.AppId);
                game.AvailableDlc = dlcList;
                game.DlcFetched = true;

                // Check which DLC are actually installed (verified from Steam manifests)
                if (dlcList.Count > 0 && !string.IsNullOrEmpty(game.LibraryPath))
                {
                    _dlcService.CheckInstalledDlc(game.FullPath, game.LibraryPath, dlcList);
                    gamesWithDlc++;
                }

                completed++;

                // Update UI periodically
                if (completed % 5 == 0 || completed == total)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"Fetching DLC: {completed}/{total} games ({gamesWithDlc} with DLC)...";
                        // Refresh the list to show updated DLC info
                        GameLibraryViewControl.RefreshList();
                    });
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Error fetching DLC for {game.Name}", ex);
                game.DlcFetched = true; // Mark as fetched even on error
                completed++;
            }
        }

        // Final UI update and cache save
        Dispatcher.Invoke(() =>
        {
            var packageableCount = _allGames.Count(g => g.IsPackageable);
            var totalDlc = _allGames.Sum(g => g.TotalDlcCount);
            StatusText.Text = $"✓ {_allGames.Count} games • {packageableCount} packageable • {totalDlc} DLC available";
            GameLibraryViewControl.RefreshList();
            
            // Save updated DLC info to cache
            foreach (var game in games)
            {
                _cacheService.UpdateCache(game);
            }
            _cacheService.SaveCache();
        });
    }

    /// <summary>
    /// Checks which games have already been packaged by scanning the output directory.
    /// Clears package status if the package no longer exists.
    /// </summary>
    private void CheckExistingPackages(List<InstalledGame> games)
    {
        string[] packageDirs = Array.Empty<string>();
        
        if (Directory.Exists(_outputPath))
        {
            try
            {
                packageDirs = Directory.GetDirectories(_outputPath);
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Error scanning output directory: {ex.Message}", ex, "MainWindow");
            }
        }

        // Build a map of AppID -> Package Path for robust detection
        var packageMap = ScanForPackages(packageDirs);

        foreach (var game in games)
        {
            // First, reset package status
            var wasPackaged = game.IsPackaged;
            game.IsPackaged = false;
            game.PackagePath = null;
            game.LastPackaged = null;

            if (packageDirs.Length == 0) continue;

            // Strategy 1: Match by AppID (Robust)
            string? matchingDir = null;
            if (packageMap.TryGetValue(game.AppId, out var appIdMatch))
            {
                matchingDir = appIdMatch;
            }
            
            // Strategy 2: Fallback to folder name matching (Legacy support)
            if (matchingDir == null)
            {
                var sanitizedName = SanitizeFileName(game.Name);
                if (!string.IsNullOrEmpty(sanitizedName))
                {
                    matchingDir = packageDirs.FirstOrDefault(d => 
                        Path.GetFileName(d).Equals(sanitizedName, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (matchingDir != null)
            {
                // Check if it has Goldberg applied (steam_settings folder or steam_api.dll backup)
                var hasGoldberg = Directory.Exists(Path.Combine(matchingDir, "steam_settings")) ||
                                 File.Exists(Path.Combine(matchingDir, "steam_api_o.dll")) ||
                                 File.Exists(Path.Combine(matchingDir, "steam_api64_o.dll"));

                if (hasGoldberg)
                {
                    game.IsPackaged = true;
                    game.PackagePath = matchingDir;
                    game.LastPackaged = Directory.GetLastWriteTime(matchingDir);
                    
                    // Check if this was received from another SteamRoll client
                    game.IsReceivedPackage = File.Exists(Path.Combine(matchingDir, ".steamroll_received"));
                    
                    // Read package metadata for update detection
                    var metadataPath = Path.Combine(matchingDir, "steamroll.json");
                    if (File.Exists(metadataPath))
                    {
                        try
                        {
                            var json = File.ReadAllText(metadataPath);
                            var metadata = System.Text.Json.JsonSerializer.Deserialize<SteamRoll.Models.PackageMetadata>(json);
                            if (metadata != null)
                            {
                                game.PackageBuildId = metadata.BuildId;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.Instance.Warning($"Failed to read metadata for {game.Name}: {ex.Message}", "MainWindow");
                        }
                    }
                }
            }

            // Update cache if status changed
            if (wasPackaged != game.IsPackaged)
            {
                _cacheService.UpdateCache(game);
            }
        }
        
        // Save cache if any changes were made
        _cacheService.SaveCache();
    }

    /// <summary>
    /// Scans package directories to map AppIDs to their package paths.
    /// This is more robust than matching by folder name.
    /// </summary>
    private Dictionary<int, string> ScanForPackages(string[] packageDirs)
    {
        var map = new Dictionary<int, string>();
        if (packageDirs == null) return map;

        foreach (var dir in packageDirs)
        {
            try
            {
                int appId = -1;
                
                // Strategy 1: Check steam_appid.txt in root (standard Goldberg/Steam)
                var rootAppIdPath = Path.Combine(dir, "steam_appid.txt");
                if (File.Exists(rootAppIdPath))
                {
                    if (int.TryParse(File.ReadLines(rootAppIdPath).FirstOrDefault() ?? "", out var id)) appId = id;
                }
                
                if (appId == -1)
                {
                    var goldbergIdPath = Path.Combine(dir, "steam_settings", "steam_appid.txt");
                    if (File.Exists(goldbergIdPath))
                    {
                        if (int.TryParse(File.ReadLines(goldbergIdPath).FirstOrDefault() ?? "", out var id)) appId = id;
                    }
                }
                
                // Strategy 3: Check steamroll.json (New metadata file)
                if (appId == -1)
                {
                    var metadataPath = Path.Combine(dir, "steamroll.json");
                    if (File.Exists(metadataPath))
                    {
                        try 
                        {
                            var json = File.ReadAllText(metadataPath);
                            // Simple parsing to avoid heavy dependency if possible, or use JsonSerializer
                            var metadata = System.Text.Json.JsonSerializer.Deserialize<SteamRoll.Models.PackageMetadata>(json);
                            if (metadata != null && metadata.AppId > 0)
                            {
                                appId = metadata.AppId;
                            }
                        }
                        catch 
                        { 
                            // Ignore malformed json 
                        }
                    }
                }

                if (appId > 0 && !map.ContainsKey(appId))
                {
                    map.Add(appId, dir);
                }

            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Error scanning package {dir}", ex);
            }
        }
        return map;
    }
    
    /// <summary>
    /// Scans output folder for received packages that aren't in Steam library.
    /// Creates InstalledGame entries for these "received-only" packages.
    /// </summary>
    private List<InstalledGame> ScanReceivedPackages(List<InstalledGame> existingGames)
    {
        var receivedGames = new List<InstalledGame>();
        
        if (!Directory.Exists(_outputPath))
            return receivedGames;
        
        try
        {
            var existingNames = new HashSet<string>(
                existingGames.Select(g => SanitizeFileName(g.Name)),
                StringComparer.OrdinalIgnoreCase);
            
            foreach (var dir in Directory.GetDirectories(_outputPath))
            {
                var dirName = Path.GetFileName(dir);
                
                // Skip if this matches an existing Steam game
                if (existingNames.Contains(dirName))
                    continue;
                
                // Check if this is a received package
                var markerPath = Path.Combine(dir, ".steamroll_received");
                if (!File.Exists(markerPath))
                    continue;
                
                // Check if it has Goldberg markers (steam_settings folder or backup DLLs)
                var hasGoldberg = Directory.Exists(Path.Combine(dir, "steam_settings")) ||
                                 File.Exists(Path.Combine(dir, "steam_api_o.dll")) ||
                                 File.Exists(Path.Combine(dir, "steam_api64_o.dll"));
                
                if (!hasGoldberg)
                    continue;
                
                // Extract AppID from steam_settings if possible
                var appId = 0;
                var steamAppIdPath = Path.Combine(dir, "steam_settings", "steam_appid.txt");
                if (File.Exists(steamAppIdPath))
                {
                    var appIdContent = File.ReadAllText(steamAppIdPath).Trim();
                    int.TryParse(appIdContent, out appId);
                }
                
                // Calculate size
                long sizeOnDisk = 0;
                try
                {
                    sizeOnDisk = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);
                }
                catch (Exception sizeEx) 
                { 
                    LogService.Instance.Debug($"Could not calculate size for {dir}: {sizeEx.Message}", "MainWindow"); 
                }
                
                // Create a special InstalledGame for this received package
                var receivedGame = new InstalledGame
                {
                    AppId = appId,
                    Name = dirName,
                    InstallDir = dirName,
                    FullPath = dir,
                    LibraryPath = _outputPath,
                    SizeOnDisk = sizeOnDisk,
                    StateFlags = 4, // Mark as fully installed
                    IsPackaged = true,
                    PackagePath = dir,
                    IsReceivedPackage = true,
                    LastPackaged = Directory.GetLastWriteTime(dir),
                    // DrmAnalysis left null - received packages don't need analysis
                    LastAnalyzed = DateTime.Now
                };
                
                receivedGames.Add(receivedGame);
                LogService.Instance.Info($"Found received package: {dirName} (AppID: {appId})", "MainWindow");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error scanning received packages: {ex.Message}", ex, "MainWindow");
        }
        
        return receivedGames;
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


    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Skip if not yet initialized (fires during XAML loading)
        if (!IsLoaded) return;
        
        ApplyFilters();
    }
    
    // ============================================
    // Filters & Sorting
    // ============================================
    
    private void FilterReady_Click(object sender, RoutedEventArgs e) => ApplyFilters();
    private void FilterPackaged_Click(object sender, RoutedEventArgs e) => ApplyFilters();
    private void FilterDlc_Click(object sender, RoutedEventArgs e) => ApplyFilters();
    private void FilterUpdate_Click(object sender, RoutedEventArgs e) => ApplyFilters();
    private void FilterFavorites_Click(object sender, RoutedEventArgs e) => ApplyFilters();

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }
    
    private void ApplyFilters()
    {
        var searchText = HeaderControl.SearchText;
        var isSearchActive = !string.IsNullOrWhiteSpace(searchText) && searchText != "🔍 Search games...";
        
        var filterReady = StatsBarControl.IsReadyChecked;
        var filterPackaged = StatsBarControl.IsPackagedChecked;
        var filterDlc = StatsBarControl.IsDlcChecked;
        var filterUpdate = StatsBarControl.IsUpdateChecked;
        var filterFavorites = StatsBarControl.IsFavoritesChecked;
        
        var filtered = GetGamesSnapshot().AsEnumerable();
        
        // Apply search filter
        if (isSearchActive)
        {
            filtered = filtered.Where(g => 
                g.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                g.AppId.ToString().Contains(searchText));
        }
        
        // Apply toggle filters
        if (filterReady)
            filtered = filtered.Where(g => g.IsPackageable);
        
        if (filterPackaged)
            filtered = filtered.Where(g => g.IsPackaged);
            
        if (filterDlc)
            filtered = filtered.Where(g => g.HasDlc);
        
        if (filterUpdate)
            filtered = filtered.Where(g => g.UpdateAvailable);

        if (filterFavorites)
            filtered = filtered.Where(g => g.IsFavorite);

        // Apply Sorting - combine favorites pinning with primary sort in single pass
        // If we are NOT filtering BY favorites, pin favorites to top first
        var pinFavorites = !filterFavorites;
        
        if (StatsBarControl.SelectedSortItem is ComboBoxItem item && item.Tag is string sortType)
        {
            // Use a combined sort: favorites first (if pinning), then by selected sort type
            if (pinFavorites)
            {
                filtered = sortType switch
                {
                    "Size" => filtered.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.SizeOnDisk),
                    "LastPlayed" => filtered.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.LastPlayed),
                    "ReviewScore" => filtered.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.ReviewPositivePercent ?? 0),
                    "ReleaseDate" => filtered.OrderByDescending(g => g.IsFavorite).ThenByDescending(g => g.BuildId),
                    _ => filtered.OrderByDescending(g => g.IsFavorite).ThenBy(g => g.Name)
                };
            }
            else
            {
                filtered = sortType switch
                {
                    "Size" => filtered.OrderByDescending(g => g.SizeOnDisk),
                    "LastPlayed" => filtered.OrderByDescending(g => g.LastPlayed),
                    "ReviewScore" => filtered.OrderByDescending(g => g.ReviewPositivePercent ?? 0),
                    "ReleaseDate" => filtered.OrderByDescending(g => g.BuildId),
                    _ => filtered.OrderBy(g => g.Name)
                };
            }
        }
        else
        {
            // Default sort by name, with favorites pinned if not filtering by favorites
            filtered = pinFavorites 
                ? filtered.OrderByDescending(g => g.IsFavorite).ThenBy(g => g.Name)
                : filtered.OrderBy(g => g.Name);
        }
        
        UpdateGamesList(filtered.ToList());
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
    
    // ============================================
    // Batch Operations
    // ============================================
    
    private void GameSelection_Click(object sender, RoutedEventArgs e)
    {
        // Update batch action bar visibility based on selection
        UpdateBatchActionBar();
    }
    
    private void UpdateBatchActionBar()
    {
        var selectedGames = _allGames.Where(g => g.IsSelected).ToList();
        var selectedCount = selectedGames.Count;
        var sendableCount = selectedGames.Count(g => g.IsPackaged);
        
        GameLibraryViewControl.UpdateBatchBar(selectedCount, sendableCount > 0);
    }
    
    private void BatchClear_Click(object sender, RoutedEventArgs e) => ClearSelection_Click(sender, e);
    
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var game in _allGames)
        {
            game.IsSelected = false;
        }
        
        // Refresh the list to update checkboxes
        UpdateGamesList(_allGames);
        UpdateBatchActionBar();
    }
    
    private void SelectModeToggle_Click(object sender, RoutedEventArgs e)
    {
        // Toggle handled via ToggleButton binding - just update display
        UpdateBatchActionBar();
    }
    
    private async void BatchPackage_Click(object sender, RoutedEventArgs e)
    {
        var selectedGames = _allGames.Where(g => g.IsSelected && g.IsPackageable).ToList();
        
        if (selectedGames.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Games Selected", "Please select packageable games first.");
            return;
        }
        
        var result = MessageBox.Show(
            $"Package {selectedGames.Count} game{(selectedGames.Count > 1 ? "s" : "")}?\n\nThis may take a while depending on game sizes.",
            "Batch Package",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        
        if (result != MessageBoxResult.Yes) return;
        
        // Disable batch buttons during operation
        GameLibraryViewControl.SetBatchButtonsEnabled(false);
        
        var successCount = 0;
        var failCount = 0;
        
        try
        {
            for (int i = 0; i < selectedGames.Count; i++)
            {
                var game = selectedGames[i];
                StatusText.Text = $"📦 Packaging {i + 1}/{selectedGames.Count}: {game.Name}";
                
                try
                {
                    var mode = _settingsService.Settings.DefaultPackageMode;
                    await CreatePackageAsync(game, mode);
                    successCount++;
                    game.IsSelected = false; // Deselect after success
                }
                catch (Exception ex)
                {
                    LogService.Instance.Error($"Batch package failed for {game.Name}", ex, "Batch");
                    failCount++;
                }
            }
            
            // Show summary
            if (failCount == 0)
            {
                ToastService.Instance.ShowSuccess(
                    "Batch Complete",
                    $"Successfully packaged {successCount} game{(successCount > 1 ? "s" : "")}."
                );
            }
            else
            {
                ToastService.Instance.ShowWarning(
                    "Batch Complete",
                    $"Packaged {successCount}, failed {failCount}. Check logs for details."
                );
            }
            
            StatusText.Text = $"✓ Batch packaging complete: {successCount} succeeded, {failCount} failed";
        }
        finally
        {
            GameLibraryViewControl.SetBatchButtonsEnabled(true);
            UpdateGamesList(_allGames);
            UpdateBatchActionBar();
        }
    }
    
    private async void BatchSendToPeer_Click(object sender, RoutedEventArgs e)
    {
        var selectedGames = _allGames.Where(g => g.IsSelected && g.IsPackaged && !string.IsNullOrEmpty(g.PackagePath)).ToList();
        
        if (selectedGames.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Packages Selected", "Please select packaged games to send.");
            return;
        }
        
        // Get available peers
        var peers = _lanDiscoveryService.GetPeers();
        if (peers.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Peers Found", "No other SteamRoll instances found on your network.");
            return;
        }
        
        // Show peer selection dialog
        PeerInfo? selectedPeer;
        if (peers.Count == 1)
        {
            selectedPeer = peers.First();
        }
        else
        {
            var dialog = new PeerSelectionDialog(peers);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true || dialog.SelectedPeer == null)
            {
                return;
            }
            selectedPeer = dialog.SelectedPeer;
        }
        
        // Confirm transfer
        var totalSize = selectedGames.Sum(g => g.SizeOnDisk);
        var confirm = MessageBox.Show(
            $"Send {selectedGames.Count} package{(selectedGames.Count > 1 ? "s" : "")} to {selectedPeer.HostName}?\n\n" +
            $"Total size: ~{totalSize / (1024 * 1024 * 1024.0):F1} GB",
            "Confirm Batch Transfer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );
        
        if (confirm != MessageBoxResult.Yes) return;
        
        // Disable buttons during transfer
        GameLibraryViewControl.SetBatchButtonsEnabled(false);
        
        var successCount = 0;
        var failCount = 0;
        
        try
        {
            for (int i = 0; i < selectedGames.Count; i++)
            {
                var game = selectedGames[i];
                StatusText.Text = $"📤 Sending {i + 1}/{selectedGames.Count}: {game.Name} to {selectedPeer.HostName}...";
                
                try
                {
                    var success = await _transferService.SendPackageAsync(
                        selectedPeer.IpAddress,
                        selectedPeer.TransferPort,
                        game.PackagePath!
                    );
                    
                    if (success)
                    {
                        successCount++;
                        game.IsSelected = false;
                    }
                    else
                    {
                        failCount++;
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Error($"Batch transfer failed for {game.Name}", ex, "BatchTransfer");
                    failCount++;
                }
            }
            
            // Show summary
            if (failCount == 0)
            {
                ToastService.Instance.ShowSuccess(
                    "Batch Transfer Complete",
                    $"Successfully sent {successCount} package{(successCount > 1 ? "s" : "")} to {selectedPeer.HostName}."
                );
            }
            else
            {
                ToastService.Instance.ShowWarning(
                    "Batch Transfer Complete",
                    $"Sent {successCount}, failed {failCount}. Check logs for details."
                );
            }
            
            StatusText.Text = $"✓ Batch transfer complete: {successCount} sent, {failCount} failed";
        }
        finally
        {
            GameLibraryViewControl.SetBatchButtonsEnabled(true);
            UpdateGamesList(_allGames);
            UpdateBatchActionBar();
        }
    }
    
    // ============================================
    // Context Menu Handlers
    // ============================================
    
    private InstalledGame? GetGameFromContextMenu(object sender)
    {
        if (sender is MenuItem menuItem && 
            menuItem.Parent is ContextMenu contextMenu && 
            contextMenu.PlacementTarget is FrameworkElement element &&
            element.DataContext is InstalledGame game)
        {
            return game;
        }
        return null;
    }
    
    private void ContextMenu_Package_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null) return;
        
        if (game.IsPackaged && !string.IsNullOrEmpty(game.PackagePath))
        {
            try { Process.Start("explorer.exe", game.PackagePath); } catch (Exception ex) { LogService.Instance.Debug($"Could not open package folder: {ex.Message}", "MainWindow"); }
        }
        else
        {
            // Show toast indicating they need to click the Package button
            ToastService.Instance.ShowInfo("Package Game", $"Use the Package button on the game card to create a package for {game.Name}");
        }
    }
    

    
    private void ContextMenu_SendToPeer_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game?.IsPackaged == true)
        {
            ToastService.Instance.ShowInfo("Send to Peer", $"Use the 'Send to Peer' button on the game card to transfer {game.Name}");
        }
    }

    private void ContextMenu_Favorite_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game != null)
        {
            game.IsFavorite = !game.IsFavorite;
            _cacheService.UpdateCache(game);
            _cacheService.SaveCache();
            ApplyFilters(); // Refresh sort order
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

    private async void ContextMenu_BackupSave_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null) return;

        try
        {
            var saveDir = _saveGameService.FindSaveDirectory(game.AppId, game.PackagePath);
            if (string.IsNullOrEmpty(saveDir))
            {
                ToastService.Instance.ShowWarning("No Saves Found", $"Could not find local saves for {game.Name}.");
                return;
            }

            var backupDir = Path.Combine(_settingsService.Settings.OutputPath, "Backups");
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupPath = Path.Combine(backupDir, $"{FormatUtils.SanitizeFileName(game.Name)}_Save_{timestamp}.zip");

            await _saveGameService.BackupSavesAsync(game.AppId, backupPath, game.PackagePath);

            ToastService.Instance.ShowSuccess("Backup Complete", $"Saved to Backups/{Path.GetFileName(backupPath)}");

            // Open folder?
            // Process.Start("explorer.exe", "/select," + backupPath);
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Backup failed: {ex.Message}", ex, "Backup");
            ToastService.Instance.ShowError("Backup Failed", ex.Message);
        }
    }

    private async void ContextMenu_SyncSaves_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null) return;

        // Get available peers
        var peers = _lanDiscoveryService.GetPeers();
        if (peers.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Peers Found", "No other SteamRoll instances found on your network.");
            return;
        }

        // Peer selection
        PeerInfo? selectedPeer;
        if (peers.Count == 1)
        {
            selectedPeer = peers.First();
        }
        else
        {
            var dialog = new PeerSelectionDialog(peers) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedPeer == null) return;
            selectedPeer = dialog.SelectedPeer;
        }

        // Confirm sync (Push)
        var result = MessageBox.Show(
            $"Send saves for {game.Name} to {selectedPeer.HostName}?\n\nThis will overwrite their local saves.",
            "Send Saves",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            StatusText.Text = $"⏳ Sending saves for {game.Name} to {selectedPeer.HostName}...";
            LoadingOverlay.Show("Sending saves...");

            // Check if we have saves to send
            var saveDir = _saveGameService.FindSaveDirectory(game.AppId, game.PackagePath);
            if (string.IsNullOrEmpty(saveDir) || Directory.GetFiles(saveDir).Length == 0)
            {
                ToastService.Instance.ShowWarning("No Saves Found", $"Could not find local saves for {game.Name} to send.");
                LoadingOverlay.Hide();
                return;
            }

            // Create zip in temp and send
            var tempZip = System.IO.Path.GetTempFileName();
            File.Delete(tempZip);
            await _saveGameService.BackupSavesAsync(game.AppId, tempZip, game.PackagePath);

            // Use SendSaveSyncAsync (we need to add this method to TransferService)
            var success = await _transferService.SendSaveSyncAsync(
                selectedPeer.IpAddress,
                selectedPeer.TransferPort,
                tempZip,
                game.Name
            );

            File.Delete(tempZip); // Cleanup

            LoadingOverlay.Hide();

            if (success)
            {
                StatusText.Text = $"✓ Sent saves for {game.Name} to {selectedPeer.HostName}";
                ToastService.Instance.ShowSuccess("Save Sync", "Saves sent successfully!");
            }
            else
            {
                StatusText.Text = "⚠ Failed to send saves";
                ToastService.Instance.ShowError("Save Sync Failed", "Peer rejected or error occurred.");
            }
        }
        catch (Exception ex)
        {
            LoadingOverlay.Hide();
            LogService.Instance.Error($"Save sync failed: {ex.Message}", ex, "SaveSync");
            ToastService.Instance.ShowError("Save Sync Error", ex.Message);
        }
    }

    private async void ContextMenu_DeletePackage_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null || !game.IsPackaged || string.IsNullOrEmpty(game.PackagePath)) return;

        var result = MessageBox.Show(
            $"Are you sure you want to delete the package for {game.Name}?\n\nThis will permanently remove the folder:\n{game.PackagePath}",
            "Delete Package",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                await PackageBuilder.DeletePackageAsync(game.PackagePath);

                // Update game state
                game.IsPackaged = false;
                game.PackagePath = null;
                game.LastPackaged = null;

                // If the game is purely a package (not an installed Steam game), remove it from the list
                // This applies to received packages AND packages found via PackageScanner
                if (game.LibraryPath == _outputPath)
                {
                    lock (_gamesLock)
                    {
                        _allGames.Remove(game);
                    }
                }

                _cacheService.UpdateCache(game);
                _cacheService.SaveCache();

                UpdateGamesList(_allGames);

                ToastService.Instance.ShowSuccess("Package Deleted", $"Deleted package for {game.Name}");
            }
            catch (Exception ex)
            {
                ToastService.Instance.ShowError("Delete Failed", ex.Message);
            }
        }
    }
    
    private void ContextMenu_OpenInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game != null)
        {
            try { Process.Start("explorer.exe", game.InstallDir); } catch (Exception ex) { LogService.Instance.Debug($"Could not open install folder: {ex.Message}", "MainWindow"); }
        }
    }
    
    private void ContextMenu_ViewDetails_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game != null)
        {
            // Show game info in a toast for now - full details view can be added later
            var info = $"AppID: {game.AppId}\nSize: {game.FormattedSize}\nInstalled: {game.InstallDir}";
            ToastService.Instance.ShowInfo(game.Name, info);
        }
    }

    private async void ContextMenu_RepairFromPeer_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null) return;

        var peers = _lanDiscoveryService.GetPeers();
        if (peers.Count == 0)
        {
            ToastService.Instance.ShowWarning("No Peers Found", "No peers available for repair.");
            return;
        }

        PeerInfo? selectedPeer;
        if (peers.Count == 1)
        {
            selectedPeer = peers.First();
        }
        else
        {
            var dialog = new PeerSelectionDialog(peers) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedPeer == null) return;
            selectedPeer = dialog.SelectedPeer;
        }

        var result = MessageBox.Show(
            $"Attempt to repair \"{game.Name}\" from {selectedPeer.HostName}?\n\n" +
            "This will verify your local files against the peer's copy and download only what is missing or corrupt.",
            "Repair Package",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            StatusText.Text = $"⏳ Requesting repair for {game.Name}...";
            // Uses the same "Pull Request" flow - TransferService handles the differential sync
            await _transferService.RequestPullPackageAsync(selectedPeer.IpAddress, selectedPeer.TransferPort, game.Name);
            ToastService.Instance.ShowSuccess("Repair Requested", $"Asked {selectedPeer.HostName} to send clean files.");
        }
        catch (Exception ex)
        {
            ToastService.Instance.ShowError("Repair Failed", ex.Message);
        }
    }

    private async void ContextMenu_VerifyIntegrity_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null || !game.IsPackaged || string.IsNullOrEmpty(game.PackagePath)) return;

        LoadingOverlay.Show($"Verifying {game.Name} integrity...");
        StatusText.Text = $"🛡️ Verifying {game.Name}...";

        try
        {
            var progress = new Progress<int>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    LoadingOverlay.UpdateProgress("Verifying files...", p);
                    StatusText.Text = $"🛡️ Verifying {game.Name}: {p}%";
                });
            });

            var result = await _integrityService.VerifyPackageAsync(game.PackagePath, progress);

            LoadingOverlay.Hide();

            if (result.IsValid)
            {
                StatusText.Text = $"✓ Verification passed: {game.Name}";
                ToastService.Instance.ShowSuccess("Verification Passed", $"{game.Name} is valid and intact.");
            }
            else
            {
                StatusText.Text = $"⚠ Verification failed: {game.Name}";

                var message = $"{result.MissingFiles.Count} missing files, {result.MismatchedFiles.Count} modified files.\n\n" +
                              "Would you like to see the detailed report?";

                if (MessageBox.Show(message, "Verification Failed", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    var report = $"Verification Report for {game.Name}\n\n";
                    if (result.MissingFiles.Count > 0)
                    {
                        report += "MISSING FILES:\n" + string.Join("\n", result.MissingFiles.Take(10));
                        if (result.MissingFiles.Count > 10) report += $"\n...and {result.MissingFiles.Count - 10} more";
                        report += "\n\n";
                    }
                    if (result.MismatchedFiles.Count > 0)
                    {
                        report += "MODIFIED/CORRUPT FILES:\n" + string.Join("\n", result.MismatchedFiles.Take(10));
                        if (result.MismatchedFiles.Count > 10) report += $"\n...and {result.MismatchedFiles.Count - 10} more";
                    }

                    // Show in a simple dialog or just a large message box
                    MessageBox.Show(report, "Detailed Report", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            LoadingOverlay.Hide();
            StatusText.Text = $"⚠ Verification error: {ex.Message}";
            ToastService.Instance.ShowError("Verification Error", ex.Message);
        }
    }
    
    private void ContextMenu_OpenSteamStore_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game != null)
        {
            var url = $"https://store.steampowered.com/app/{game.AppId}";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { LogService.Instance.Debug($"Could not open Steam store: {ex.Message}", "MainWindow"); }
        }
    }
    
    // Per-game Goldberg configuration storage
    private readonly Dictionary<int, GoldbergConfig> _gameGoldbergConfigs = new();
    
    private void ContextMenu_AdvancedConfig_Click(object sender, RoutedEventArgs e)
    {
        var game = GetGameFromContextMenu(sender);
        if (game == null) return;
        
        // Get existing config or create new one with defaults
        _gameGoldbergConfigs.TryGetValue(game.AppId, out var existingConfig);
        
        var dialog = new GoldbergConfigDialog(existingConfig)
        {
            Owner = this
        };
        
        if (dialog.ShowDialog() == true && dialog.Config != null)
        {
            _gameGoldbergConfigs[game.AppId] = dialog.Config;
            ToastService.Instance.ShowSuccess("Config Saved", $"Goldberg settings saved for {game.Name}");
        }
    }
    

    
    private List<RemoteGame> OnLocalLibraryRequested()
    {
        // Return only packaged games (thread-safe snapshot)
        return GetGamesSnapshot().Where(g => g.IsPackaged && !string.IsNullOrEmpty(g.PackagePath))
            .Select(g => new RemoteGame { Name = g.Name, SizeBytes = g.SizeOnDisk })
            .ToList();
    }

    private async Task OnPullPackageRequested(string gameName, string targetIp, int targetPort)
    {
        // Find the requested game (thread-safe)
        var game = FindGame(g => g.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase) && g.IsPackaged);

        if (game != null && !string.IsNullOrEmpty(game.PackagePath))
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"📤 Sending requested package {game.Name}...";
                ToastService.Instance.ShowInfo("Transfer Started", $"Sending {game.Name} (Requested by peer)");
            });

            await _transferService.SendPackageAsync(targetIp, targetPort, game.PackagePath);
        }
        else
        {
            LogService.Instance.Warning($"Peer requested unknown/unpackaged game: {gameName}", "MainWindow");
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

