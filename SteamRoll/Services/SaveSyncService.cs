using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SteamRoll.Services;

/// <summary>
/// Service that monitors game saves and synchronizes them with peers on the network.
/// Supports both manual "Sync Now" and optional automatic background sync.
/// </summary>
public class SaveSyncService : IDisposable
{
    private readonly SaveGameService _saveGameService;
    private readonly LanDiscoveryService _lanDiscoveryService;
    private readonly TransferService _transferService;
    private readonly SettingsService _settingsService;
    
    private readonly ConcurrentDictionary<int, GameSyncState> _syncStates = new();
    private readonly ConcurrentDictionary<int, FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<int, DateTime> _pendingChanges = new();
    private readonly ConcurrentDictionary<int, List<SyncedSave>> _saveVersions = new();
    
    private const int MAX_VERSIONS_TO_KEEP = 5;
    private const int DEBOUNCE_MS = 5000; // Wait 5 seconds after changes before syncing
    private readonly string _versionsPath;
    private bool _autoSyncEnabled;
    private CancellationTokenSource? _debounceCtx;

    /// <summary>
    /// Raised when a save conflict is detected.
    /// </summary>
    public event EventHandler<SaveConflict>? ConflictDetected;

    /// <summary>
    /// Raised when a sync operation completes.
    /// </summary>
    public event EventHandler<(int AppId, bool Success, string Message)>? SyncCompleted;

    /// <summary>
    /// Raised when a remote save is available for download.
    /// </summary>
    public event EventHandler<SaveSyncOffer>? RemoteSaveAvailable;

    public SaveSyncService(
        SaveGameService saveGameService,
        LanDiscoveryService lanDiscoveryService,
        TransferService transferService,
        SettingsService settingsService)
    {
        _saveGameService = saveGameService;
        _lanDiscoveryService = lanDiscoveryService;
        _transferService = transferService;
        _settingsService = settingsService;
        
        _versionsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamRoll", "SaveVersions");
        
        Directory.CreateDirectory(_versionsPath);
        
        _autoSyncEnabled = settingsService.Settings.AutoSaveSync;
    }

    /// <summary>
    /// Gets or sets whether automatic background sync is enabled.
    /// </summary>
    public bool AutoSyncEnabled
    {
        get => _autoSyncEnabled;
        set
        {
            _autoSyncEnabled = value;
            if (value)
            {
                StartAutoSync();
            }
            else
            {
                StopAutoSync();
            }
        }
    }

    /// <summary>
    /// Starts monitoring saves for a game.
    /// </summary>
    public void StartMonitoring(int appId, string gameName, string? packagePath = null)
    {
        var saveDir = _saveGameService.FindSaveDirectory(appId, packagePath);
        if (string.IsNullOrEmpty(saveDir) || !Directory.Exists(saveDir))
        {
            LogService.Instance.Debug($"No save directory found for {gameName} (AppId: {appId})", "SaveSync");
            return;
        }

        var state = new GameSyncState
        {
            AppId = appId,
            GameName = gameName,
            SavePath = saveDir,
            LastKnownHash = ComputeDirectoryHash(saveDir),
            LastSyncTime = DateTime.Now
        };

        _syncStates[appId] = state;

        // Load existing versions
        LoadSaveVersions(appId);

        if (_autoSyncEnabled)
        {
            StartWatching(appId, saveDir);
        }

        LogService.Instance.Info($"Started monitoring saves for {gameName}", "SaveSync");
    }

    /// <summary>
    /// Stops monitoring saves for a game.
    /// </summary>
    public void StopMonitoring(int appId)
    {
        if (_watchers.TryRemove(appId, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _syncStates.TryRemove(appId, out _);
    }

    /// <summary>
    /// Manually syncs saves for a game to all known peers.
    /// </summary>
    public async Task<bool> SyncNowAsync(int appId, CancellationToken ct = default)
    {
        if (!_syncStates.TryGetValue(appId, out var state))
        {
            LogService.Instance.Warning($"Cannot sync: game {appId} is not being monitored", "SaveSync");
            return false;
        }

        try
        {
            // Create a backup version before syncing
            await CreateVersionBackupAsync(appId, state.SavePath);

            // Compute current hash
            var currentHash = ComputeDirectoryHash(state.SavePath);
            
            // Increment vector clock for this device
            var deviceId = _settingsService.Settings.DeviceId;
            state.VectorClock.Increment(deviceId);
            
            // Create save info for announcement with vector clock
            var saveInfo = new SaveSyncOffer
            {
                AppId = appId,
                GameName = state.GameName,
                Hash = currentHash,
                Timestamp = DateTime.Now,
                SizeBytes = GetDirectorySize(state.SavePath),
                Version = state.LocalVersion + 1,
                VectorClock = new VectorClock(state.VectorClock)
            };

            // Broadcast save availability to peers
            var peers = _lanDiscoveryService.GetPeers();
            if (peers.Count == 0)
            {
                LogService.Instance.Info($"No peers available to sync {state.GameName}", "SaveSync");
                SyncCompleted?.Invoke(this, (appId, true, "No peers available"));
                return true;
            }

            // Update state
            state.LastKnownHash = currentHash;
            state.LastSyncTime = DateTime.Now;
            state.LocalVersion++;

            LogService.Instance.Info($"Syncing saves for {state.GameName} to {peers.Count} peers (vclock: {state.VectorClock})", "SaveSync");
            SyncCompleted?.Invoke(this, (appId, true, $"Synced to {peers.Count} peers"));
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to sync saves for AppId {appId}: {ex.Message}", ex, "SaveSync");
            SyncCompleted?.Invoke(this, (appId, false, ex.Message));
            return false;
        }
    }

    /// <summary>
    /// Handles receiving a save sync offer from a peer.
    /// Uses vector clocks for causality detection instead of timestamps.
    /// </summary>
    public void HandleSaveSyncOffer(SaveSyncOffer offer, string peerIp)
    {
        if (!_syncStates.TryGetValue(offer.AppId, out var state))
        {
            // We're not monitoring this game
            return;
        }

        // Check if remote save is different
        if (offer.Hash != state.LastKnownHash)
        {
            // Use vector clock to determine causality
            var comparison = state.VectorClock.CompareTo(offer.VectorClock);
            
            if (comparison < 0)
            {
                // Remote happened-after local (remote is strictly newer)
                LogService.Instance.Info($"Remote save for {offer.GameName} is newer (vclock comparison)", "SaveSync");
                RemoteSaveAvailable?.Invoke(this, offer);
            }
            else if (comparison > 0)
            {
                // Local happened-after remote (local is strictly newer)
                LogService.Instance.Debug($"Local save for {offer.GameName} is newer (vclock comparison), ignoring remote offer", "SaveSync");
            }
            else
            {
                // Clocks are concurrent - this is a true conflict!
                // Vector clocks detect this reliably without depending on synchronized system clocks
                LogService.Instance.Warning($"Concurrent modification detected for {offer.GameName} - conflict!", "SaveSync");
                
                var conflict = new SaveConflict
                {
                    LocalSave = new SyncedSave
                    {
                        AppId = offer.AppId,
                        GameName = state.GameName,
                        Hash = state.LastKnownHash,
                        Timestamp = state.LastSyncTime,
                        Version = state.LocalVersion,
                        VectorClock = new VectorClock(state.VectorClock)
                    },
                    RemoteSave = new SyncedSave
                    {
                        AppId = offer.AppId,
                        GameName = offer.GameName,
                        Hash = offer.Hash,
                        Timestamp = offer.Timestamp,
                        SizeBytes = offer.SizeBytes,
                        Version = offer.Version,
                        PeerSource = peerIp,
                        VectorClock = new VectorClock(offer.VectorClock)
                    },
                    ConflictTime = DateTime.Now
                };

                ConflictDetected?.Invoke(this, conflict);
            }
        }
    }

    /// <summary>
    /// Resolves a save conflict with the specified resolution.
    /// Uses vector clocks for proper distributed causality when applicable.
    /// </summary>
    public async Task ResolveConflictAsync(SaveConflict conflict, SaveConflictResolution resolution)
    {
        if (!_syncStates.TryGetValue(conflict.AppId, out var state))
            return;

        switch (resolution)
        {
            case SaveConflictResolution.KeepLocal:
                // Keep local version, increment our clock to show we "won" the conflict
                var deviceId = _settingsService.Settings.DeviceId;
                state.VectorClock.Increment(deviceId);
                state.LastSyncTime = DateTime.Now;
                LogService.Instance.Info($"Kept local saves for {conflict.GameName}", "SaveSync");
                break;

            case SaveConflictResolution.UseRemote:
                // Create timestamped .bak backup of local before overwriting
                await CreateTimestampedBackupAsync(conflict.AppId, state.SavePath, "before_remote");
                
                // Merge remote vector clock into ours to maintain causality chain
                state.VectorClock.Merge(conflict.RemoteSave.VectorClock);
                state.VectorClock.Increment(_settingsService.Settings.DeviceId);
                
                // Download and apply remote (handled by caller)
                LogService.Instance.Info($"Will use remote saves for {conflict.GameName} (local backed up as .bak)", "SaveSync");
                break;

            case SaveConflictResolution.KeepBoth:
                // Create timestamped backup of local
                await CreateVersionBackupAsync(conflict.AppId, state.SavePath, "_local");
                // Remote will be downloaded with timestamp suffix (handled by caller)
                LogService.Instance.Info($"Keeping both local and remote saves for {conflict.GameName}", "SaveSync");
                break;

            case SaveConflictResolution.LastModifiedWins:
                // Use vector clock total logical time to determine "winner"
                // This is more reliable than timestamps which depend on clock sync
                var localLogicalTime = conflict.LocalSave.VectorClock.TotalLogicalTime;
                var remoteLogicalTime = conflict.RemoteSave.VectorClock.TotalLogicalTime;
                
                LogService.Instance.Debug(
                    $"LastModifiedWins: local vclock={localLogicalTime}, remote vclock={remoteLogicalTime}", "SaveSync");
                
                // If logical times are equal, fall back to timestamp (with warning)
                bool remoteWins;
                if (localLogicalTime != remoteLogicalTime)
                {
                    remoteWins = remoteLogicalTime > localLogicalTime;
                }
                else
                {
                    // Fallback to timestamp when vector clocks are equal
                    LogService.Instance.Warning(
                        $"Vector clocks equal for {conflict.GameName}, falling back to timestamps (may be unreliable)", "SaveSync");
                    remoteWins = conflict.RemoteSave.Timestamp > conflict.LocalSave.Timestamp;
                }
                
                if (remoteWins)
                {
                    // Remote wins - backup local first
                    await CreateTimestampedBackupAsync(conflict.AppId, state.SavePath, "local_overwritten");
                    state.VectorClock.Merge(conflict.RemoteSave.VectorClock);
                    state.VectorClock.Increment(_settingsService.Settings.DeviceId);
                    LogService.Instance.Info($"Remote save wins for {conflict.GameName}, local backed up as .bak", "SaveSync");
                }
                else
                {
                    // Local wins - increment clock to show we resolved the conflict
                    state.VectorClock.Increment(_settingsService.Settings.DeviceId);
                    state.LastSyncTime = DateTime.Now;
                    LogService.Instance.Info($"Local save wins for {conflict.GameName}, keeping local", "SaveSync");
                }
                break;
        }
    }

    /// <summary>
    /// Gets the versioned backups for a game.
    /// </summary>
    public List<SyncedSave> GetSaveVersions(int appId)
    {
        return _saveVersions.TryGetValue(appId, out var versions) 
            ? versions.OrderByDescending(v => v.Timestamp).ToList() 
            : new List<SyncedSave>();
    }

    /// <summary>
    /// Restores a specific version of saves.
    /// </summary>
    public async Task RestoreVersionAsync(int appId, SyncedSave version)
    {
        if (!_syncStates.TryGetValue(appId, out var state))
            return;

        var versionPath = Path.Combine(_versionsPath, appId.ToString(), $"v{version.Version}.zip");
        if (!File.Exists(versionPath))
        {
            throw new FileNotFoundException($"Version backup not found: v{version.Version}");
        }

        // Backup current before restoring
        await CreateVersionBackupAsync(appId, state.SavePath, "_before_restore");

        // Extract the version
        await Task.Run(() =>
        {
            // Clear current saves
            if (Directory.Exists(state.SavePath))
            {
                foreach (var file in Directory.GetFiles(state.SavePath, "*", SearchOption.AllDirectories))
                {
                    File.Delete(file);
                }
            }

            // Extract backup
            ZipFile.ExtractToDirectory(versionPath, state.SavePath, true);
        });

        state.LastKnownHash = ComputeDirectoryHash(state.SavePath);
        state.LastSyncTime = DateTime.Now;

        LogService.Instance.Info($"Restored version {version.Version} for AppId {appId}", "SaveSync");
    }

    private void StartAutoSync()
    {
        foreach (var state in _syncStates.Values)
        {
            StartWatching(state.AppId, state.SavePath);
        }
    }

    private void StopAutoSync()
    {
        foreach (var appId in _watchers.Keys.ToList())
        {
            if (_watchers.TryRemove(appId, out var watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
        }
    }

    private void StartWatching(int appId, string savePath)
    {
        if (_watchers.ContainsKey(appId)) return;

        try
        {
            var watcher = new FileSystemWatcher(savePath)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            watcher.Changed += (s, e) => OnSaveChanged(appId);
            watcher.Created += (s, e) => OnSaveChanged(appId);
            watcher.Deleted += (s, e) => OnSaveChanged(appId);
            watcher.Renamed += (s, e) => OnSaveChanged(appId);

            _watchers[appId] = watcher;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to start file watcher for AppId {appId}: {ex.Message}", ex, "SaveSync");
        }
    }

    private void OnSaveChanged(int appId)
    {
        _pendingChanges[appId] = DateTime.Now;
        
        // Debounce - wait before syncing
        _debounceCtx?.Cancel();
        _debounceCtx = new CancellationTokenSource();
        
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DEBOUNCE_MS, _debounceCtx.Token);
                
                if (_pendingChanges.TryRemove(appId, out _))
                {
                    await SyncNowAsync(appId);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when debounce is cancelled
            }
        });
    }

    private async Task CreateVersionBackupAsync(int appId, string savePath, string suffix = "")
    {
        var gameVersionsPath = Path.Combine(_versionsPath, appId.ToString());
        Directory.CreateDirectory(gameVersionsPath);

        if (!_syncStates.TryGetValue(appId, out var state))
            return;

        var version = state.LocalVersion + 1;
        var backupPath = Path.Combine(gameVersionsPath, $"v{version}{suffix}.zip");

        await Task.Run(() =>
        {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            
            ZipFile.CreateFromDirectory(savePath, backupPath);
        });

        // Track this version
        var saveVersion = new SyncedSave
        {
            AppId = appId,
            GameName = state.GameName,
            Hash = ComputeDirectoryHash(savePath),
            Timestamp = DateTime.Now,
            SizeBytes = new FileInfo(backupPath).Length,
            Version = version
        };

        if (!_saveVersions.TryGetValue(appId, out var versions))
        {
            versions = new List<SyncedSave>();
            _saveVersions[appId] = versions;
        }

        versions.Add(saveVersion);

        // Prune old versions
        while (versions.Count > MAX_VERSIONS_TO_KEEP)
        {
            var oldest = versions.OrderBy(v => v.Timestamp).First();
            var oldPath = Path.Combine(gameVersionsPath, $"v{oldest.Version}.zip");
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }
            versions.Remove(oldest);
        }

        SaveVersionsMetadata(appId);
    }

    /// <summary>
    /// Creates a timestamped .bak backup file of the current saves.
    /// These backups are kept alongside the save directory for easy rollback.
    /// Uses format: saves_YYYYMMDD_HHmmss_{reason}.bak.zip
    /// </summary>
    private async Task CreateTimestampedBackupAsync(int appId, string savePath, string reason = "backup")
    {
        if (!Directory.Exists(savePath))
        {
            LogService.Instance.Warning($"Cannot backup - save path doesn't exist: {savePath}", "SaveSync");
            return;
        }

        try
        {
            // Create .bak file in the same parent directory as the saves
            var parentDir = Path.GetDirectoryName(savePath) ?? savePath;
            var saveDirName = Path.GetFileName(savePath);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var bakFileName = $"{saveDirName}_{timestamp}_{reason}.bak.zip";
            var bakPath = Path.Combine(parentDir, bakFileName);

            await Task.Run(() =>
            {
                // Create backup zip
                if (File.Exists(bakPath))
                    File.Delete(bakPath);
                    
                ZipFile.CreateFromDirectory(savePath, bakPath);
            });

            LogService.Instance.Info($"Created timestamped backup: {bakFileName}", "SaveSync");

            // Also add to versioned backups for the UI
            if (_syncStates.TryGetValue(appId, out var state))
            {
                await CreateVersionBackupAsync(appId, savePath, $"_{reason}");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to create timestamped backup: {ex.Message}", ex, "SaveSync");
        }
    }

    private void LoadSaveVersions(int appId)
    {
        var metadataPath = Path.Combine(_versionsPath, appId.ToString(), "versions.json");
        if (File.Exists(metadataPath))
        {
            try
            {
                var json = File.ReadAllText(metadataPath);
                var versions = JsonSerializer.Deserialize<List<SyncedSave>>(json) ?? new List<SyncedSave>();
                _saveVersions[appId] = versions;
            }
            catch (Exception ex)
            {
                LogService.Instance.Debug($"Failed to load version metadata for AppId {appId}: {ex.Message}", "SaveSync");
                _saveVersions[appId] = new List<SyncedSave>();
            }
        }
        else
        {
            _saveVersions[appId] = new List<SyncedSave>();
        }
    }

    private void SaveVersionsMetadata(int appId)
    {
        var gameVersionsPath = Path.Combine(_versionsPath, appId.ToString());
        Directory.CreateDirectory(gameVersionsPath);
        
        var metadataPath = Path.Combine(gameVersionsPath, "versions.json");
        
        if (_saveVersions.TryGetValue(appId, out var versions))
        {
            var json = JsonSerializer.Serialize(versions, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metadataPath, json);
        }
    }

    private static string ComputeDirectoryHash(string path)
    {
        using var md5 = MD5.Create();
        var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();

        using var ms = new MemoryStream();
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(path, file);
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(relativePath);
            ms.Write(pathBytes, 0, pathBytes.Length);

            var fileBytes = File.ReadAllBytes(file);
            ms.Write(fileBytes, 0, fileBytes.Length);
        }

        ms.Position = 0;
        var hash = md5.ComputeHash(ms);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static long GetDirectorySize(string path)
    {
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }

    public void Dispose()
    {
        _debounceCtx?.Cancel();
        _debounceCtx?.Dispose();
        
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
