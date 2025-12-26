using System.IO;
using System.IO.Compression;
using SteamRoll.Models;
using System.Text.Json;
using SteamRoll.Services.Packaging;

namespace SteamRoll.Services;

/// <summary>
/// Orchestrates the game packaging workflow - copies files, applies Goldberg or CreamAPI,
/// and creates a ready-to-run game package.
/// </summary>
public class PackageBuilder
{
    private readonly GoldbergService _goldbergService;
    private readonly DlcService _dlcService;
    private readonly CreamApiService _creamApiService;
    private readonly SteamStoreService _steamStoreService;
    private readonly PathLockService _pathLockService;
    
    private readonly PackageFileHandler _fileHandler;
    private readonly PackageMetadataGenerator _metadataGenerator;
    private readonly LauncherGenerator _launcherGenerator;
    private readonly PackageDependencyManager _dependencyManager;

    /// <summary>
    /// Event fired when packaging progress updates.
    /// </summary>
    public event Action<string, int>? ProgressChanged;

    public PackageBuilder(GoldbergService goldbergService, SettingsService settingsService, PathLockService? pathLockService = null, DlcService? dlcService = null, CreamApiService? creamApiService = null, SteamStoreService? steamStoreService = null)
    {
        _goldbergService = goldbergService;
        _dlcService = dlcService ?? new DlcService();
        _creamApiService = creamApiService ?? new CreamApiService(settingsService);
        _steamStoreService = steamStoreService ?? throw new ArgumentNullException(nameof(steamStoreService), "SteamStoreService is required");
        // Fallback to new instance if not provided, though DI is preferred
        _pathLockService = pathLockService ?? new PathLockService();

        _fileHandler = new PackageFileHandler();
        _fileHandler.ProgressChanged += (status, percentage) => ReportProgress(status, percentage);

        _metadataGenerator = new PackageMetadataGenerator();
        _launcherGenerator = new LauncherGenerator();
        _dependencyManager = new PackageDependencyManager();
    }

    /// <summary>
    /// Creates a complete game package with Goldberg Emulator applied.
    /// </summary>
    public async Task<string> CreatePackageAsync(InstalledGame game, string outputPath, PackageOptions? options = null, CancellationToken ct = default, PackageState? resumeState = null)
    {
        options ??= new PackageOptions();
        LogService.Instance.Info($"Starting package creation for {game.Name} (Resume: {resumeState != null})", "PackageBuilder");
        
        var packageName = SanitizeFileName(game.Name);
        var packageDir = System.IO.Path.Combine(outputPath, packageName);
        bool packageStarted = false;

        // Ensure output directory is writable
        try
        {
             if (!Directory.Exists(outputPath)) Directory.CreateDirectory(outputPath);
             var testFile = Path.Combine(outputPath, Path.GetRandomFileName());
             using (File.Create(testFile)) { }
             File.Delete(testFile);
        }
        catch (Exception ex)
        {
             throw new UnauthorizedAccessException($"Output path is not writable: {outputPath}. {ex.Message}");
        }

        // Check available disk space using utility helper
        var requiredSpace = game.SizeOnDisk + (200 * 1024 * 1024); // 200MB safety buffer
        var (hasSpace, spaceError) = Utils.DiskUtils.HasSufficientSpace(outputPath, requiredSpace);
        if (!hasSpace && spaceError != null)
        {
            // Only fail on confirmed insufficient space, not on check failures (UNC paths, etc.)
            if (spaceError.Contains("Insufficient"))
                throw new IOException(spaceError);
            else
                LogService.Instance.Warning($"Could not check disk space: {spaceError}", "PackageBuilder");
        }

        
        // Initialize or use existing state
        var state = resumeState ?? new PackageState
        {
            AppId = game.AppId,
            GameName = game.Name,
            SourcePath = game.FullPath,
            PackagePath = packageDir,
            CurrentStep = PackagingStep.NotStarted,
            Mode = options.Mode.ToString()
        };

        // Acquire lock for the package directory to prevent concurrent modification/transfer
        // We use a long timeout (30s) as we want to wait a bit if a transfer is just finishing up
        using var lockHandle = await _pathLockService.AcquireLockAsync(packageDir, 30000, ct);

        if (lockHandle == null)
        {
            throw new IOException($"Cannot modify package for {game.Name} because it is currently locked by another operation (e.g. file transfer).");
        }

        try
        {
            // Initial Setup: Create directory if not resuming
            if (resumeState == null)
            {
                if (options.IsUpdate && Directory.Exists(packageDir))
                {
                    ReportProgress("Synchronizing package files...", 5);
                    await _fileHandler.SyncDirectoryAsync(game.FullPath, packageDir, options.ExcludedPaths, ct);
                    packageStarted = true;
                }
                else
                {
                    // Clean existing package if not resuming
                    if (Directory.Exists(packageDir))
                    {
                        ReportProgress("Cleaning existing package...", 0);
                        await Task.Run(() => Directory.Delete(packageDir, true), ct);
                    }

                    ct.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(packageDir);
                    packageStarted = true;
                }
            }
            else
            {
                packageStarted = true;
                ReportProgress($"Resuming package creation from step: {state.CurrentStep}...", 0);
            }

            // Step 1: Copy game files
            if (state.CurrentStep < PackagingStep.CopyingFiles)
            {
                state.CurrentStep = PackagingStep.CopyingFiles;
                SavePackageState(state);
                
                ReportProgress("Copying game files...", 10);
                await _fileHandler.CopyDirectoryAsync(game.FullPath, packageDir, options.ExcludedPaths, ct);
                
                // Checkpoint
                SavePackageState(state);
            }

            // Note: Interface detection is now handled internally by GoldbergPatcher
            // which scans original DLLs before replacement for stability

            bool emulatorApplied = false;
            
            // Step 3: Apply emulator
            if (state.CurrentStep <= PackagingStep.ApplyingEmulator)
            {
                state.CurrentStep = PackagingStep.ApplyingEmulator;
                SavePackageState(state);
                
                if (options.Mode == PackageMode.CreamApi)
                {
                    // CreamAPI mode
                    ReportProgress("Applying CreamAPI...", 80);
                    var dlcIds = game.AvailableDlc?.Select(d => d.AppId).ToList() ?? new List<int>();
                    emulatorApplied = await _creamApiService.ApplyCreamApiAsync(packageDir, game.AppId, dlcIds, game.Name, ct);
                    
                    if (!emulatorApplied)
                    {
                        ReportProgress("CreamAPI failed, falling back to Goldberg...", 82);
                        await _goldbergService.EnsureGoldbergAvailableAsync();
                        emulatorApplied = await Task.Run(() => 
                            _goldbergService.ApplyGoldberg(packageDir, game.AppId, options.GoldbergConfig));
                    }
                }
                else
                {
                    // Goldberg mode
                    if (!_goldbergService.IsGoldbergAvailable())
                    {
                        ReportProgress("Downloading Goldberg Emulator...", 75);
                        await _goldbergService.EnsureGoldbergAvailableAsync();
                    }
                    ReportProgress("Applying Goldberg Emulator...", 80);
                    emulatorApplied = await Task.Run(() => 
                        _goldbergService.ApplyGoldberg(packageDir, game.AppId, options.GoldbergConfig));
                    // Interface detection/writing is now handled internally by GoldbergPatcher
                }
                
                // Checkpoint
                SavePackageState(state);
            }
            else
            {
                // Assume applied if skipping
                emulatorApplied = true; 
            }

            // Step 4: Configure DLC (Goldberg only)
            if (state.CurrentStep <= PackagingStep.ConfiguringDlc)
            {
                state.CurrentStep = PackagingStep.ConfiguringDlc;
                SavePackageState(state);
                
                if (options.Mode == PackageMode.Goldberg)
                {
                    ReportProgress("Configuring DLC...", 85);
                    await ConfigureDlcAsync(game, packageDir);
                }
                
                // Checkpoint
                SavePackageState(state);
            }


            // Step 5: Metadata
            if (state.CurrentStep <= PackagingStep.CreatingMetadata)
            {
                state.CurrentStep = PackagingStep.CreatingMetadata;
                SavePackageState(state);
                
                ReportProgress("Creating package metadata...", 90);
                SteamGameDetails? storeDetails = null;
                try 
                { 
                    storeDetails = await _steamStoreService.GetGameDetailsAsync(game.AppId); 
                } 
                catch (Exception storeEx) 
                { 
                    LogService.Instance.Debug($"Could not fetch store details: {storeEx.Message}", "PackageBuilder"); 
                }
                
                string? emulatorVersion = options.Mode == PackageMode.CreamApi 
                    ? _creamApiService.GetInstalledVersion() 
                    : _goldbergService.GetInstalledVersion();
                
                _metadataGenerator.CreatePackageMetadata(packageDir, game, emulatorApplied, options.Mode, emulatorVersion, storeDetails, options.HashMode);
                
                // Checkpoint
                SavePackageState(state);
            }

            // Step 6: Create Launcher & Dependency Scripts
            if (state.CurrentStep <= PackagingStep.CreatingLauncher)
            {
                state.CurrentStep = PackagingStep.CreatingLauncher;
                SavePackageState(state);
                
                _launcherGenerator.CreateLauncher(packageDir, game, options.LauncherArguments);
                _dependencyManager.DetectAndScriptDependencies(packageDir);
                
                // Checkpoint
                SavePackageState(state);
            }

            state.CurrentStep = PackagingStep.Complete;
            ClearPackageState(packageDir); // Success - cleanup state
            
            ReportProgress("Package complete!", 100);
            LogService.Instance.Info($"Package created: {packageDir}", "PackageBuilder");
            return packageDir;
        }
        catch (OperationCanceledException)
        {
            LogService.Instance.Warning($"Package creation cancelled for {game.Name}", "PackageBuilder");
            // Save state on cancellation for resumption
            SavePackageState(state);
            throw;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Package creation failed for {game.Name}", ex, "PackageBuilder");
            // Clean up partial package if not resumable
            if (resumeState == null)
            {
                await CleanupFailedPackageAsync(packageDir, packageStarted);
            }
            else
            {
                // Save state for resumption
                SavePackageState(state);
            }
            throw;
        }
    }
    
    /// <summary>
    /// Cleans up a partial/failed package directory.
    /// </summary>
    private async Task CleanupFailedPackageAsync(string packageDir, bool packageStarted)
    {
        if (!packageStarted)
            return;
            
        try
        {
            if (string.IsNullOrEmpty(packageDir))
                return;

            // Security check: Prevent deleting root or system directories
            var fullPath = Path.GetFullPath(packageDir);
            var root = Path.GetPathRoot(fullPath);

            // Check if path is root (e.g., C:\ or /)
            // Or if path is effectively empty or just a drive letter
            if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                              root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                              StringComparison.OrdinalIgnoreCase))
            {
                LogService.Instance.Error($"Safety check prevented deletion of root path: {packageDir}", category: "PackageBuilder");
                return;
            }

            // Also prevent deleting very short paths that might be system roots (e.g. C:)
            // But allow C:\A (length 3 + 1 = 4)
            if (fullPath.Length <= 3 && (root != null && fullPath.StartsWith(root)))
            {
                 // This covers "C:\" (3 chars) but allows "C:\A" (4 chars)
                 // Just strictly block anything length <= 3 if it looks like a root
                 LogService.Instance.Error($"Safety check prevented deletion of short path: {packageDir}", category: "PackageBuilder");
                 return;
            }

            ReportProgress("Cleaning up failed package...", 0);
            
            // Delete folder if it exists
            if (Directory.Exists(packageDir))
            {
                await Task.Run(() => Directory.Delete(packageDir, true));
                LogService.Instance.Info($"Cleaned up failed package at {packageDir}", "PackageBuilder");
            }
            
            // Delete ZIP if it exists (it might have been created before failure)
            var folderName = System.IO.Path.GetFileName(packageDir);
            var parentDir = System.IO.Path.GetDirectoryName(packageDir) ?? packageDir;
            var zipPath = System.IO.Path.Combine(parentDir, $"{folderName} - SteamRoll.zip");
            
            if (File.Exists(zipPath))
            {
                await Task.Run(() => File.Delete(zipPath));
                LogService.Instance.Info($"Cleaned up failed package ZIP at {zipPath}", "PackageBuilder");
            }
        }
        catch (Exception cleanupEx)
        {
            // Don't fail if cleanup fails - just log it but include stack trace for debugging
            LogService.Instance.Error($"Failed to clean up package directory: {cleanupEx.Message}", cleanupEx, "PackageBuilder");
        }
    }
    
    /// <summary>
    /// Detects Steam interfaces from original DLLs before replacement.
    /// </summary>
    private List<string> DetectGameInterfaces(string gameDir)
    {
        var interfaces = new System.Collections.Concurrent.ConcurrentBag<string>();
        
        // Look for original steam_api DLLs
        var steamApiPaths = new[]
        {
            Directory.GetFiles(gameDir, "steam_api.dll", SearchOption.AllDirectories),
            Directory.GetFiles(gameDir, "steam_api64.dll", SearchOption.AllDirectories)
        };

        var allPaths = steamApiPaths.SelectMany(p => p).ToList();

        Parallel.ForEach(allPaths, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (path) =>
        {
            var detected = _goldbergService.DetectInterfaces(path);
            foreach (var iface in detected)
            {
                interfaces.Add(iface);
            }
        });

        return interfaces.Distinct().ToList();
    }
    
    /// <summary>
    /// Verifies the integrity of a package by comparing current file hashes against stored hashes.
    /// </summary>
    /// <param name="packageDir">Path to the package directory.</param>
    /// <returns>A tuple with (isValid, mismatches) where mismatches is a list of files that failed verification.</returns>
    public static Task<(bool IsValid, List<string> Mismatches)> VerifyIntegrityAsync(string packageDir)
    {
        return PackageVerifier.VerifyIntegrityAsync(packageDir);
    }

    /// <summary>
    /// Verifies the integrity of a package by comparing current file hashes against stored hashes.
    /// </summary>
    /// <param name="packageDir">Path to the package directory.</param>
    /// <returns>A tuple with (isValid, mismatches) where mismatches is a list of files that failed verification.</returns>
    [Obsolete("Use VerifyIntegrityAsync instead")]
    public static (bool IsValid, List<string> Mismatches) VerifyIntegrity(string packageDir)
    {
        return PackageVerifier.VerifyIntegrity(packageDir);
    }
    
    private void ReportProgress(string status, int percentage)
    {
        ProgressChanged?.Invoke(status, percentage);
    }

    /// <summary>
    /// Saves the current packaging state to a progress file for resumption.
    /// </summary>
    private void SavePackageState(PackageState state)
    {
        try
        {
            var progressPath = System.IO.Path.Combine(state.PackagePath, PackageState.ProgressFileName);
            state.LastUpdatedAt = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(progressPath, json);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to save package state: {ex.Message}", "PackageBuilder");
        }
    }
    
    /// <summary>
    /// Loads package state from a progress file if it exists.
    /// </summary>
    public static PackageState? LoadPackageState(string packageDir)
    {
        try
        {
            var progressPath = System.IO.Path.Combine(packageDir, PackageState.ProgressFileName);
            if (!File.Exists(progressPath)) return null;
            
            var json = File.ReadAllText(progressPath);
            return JsonSerializer.Deserialize<PackageState>(json);
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Removes the progress file after successful package completion.
    /// </summary>
    private static void ClearPackageState(string packageDir)
    {
        try
        {
            var progressPath = System.IO.Path.Combine(packageDir, PackageState.ProgressFileName);
            if (File.Exists(progressPath))
            {
                File.Delete(progressPath);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Could not clear package state: {ex.Message}", "PackageBuilder");
        }
    }
    
    /// <summary>
    /// Finds all incomplete packages in the output directory.
    /// </summary>
    /// <param name="outputPath">The output directory to scan.</param>
    /// <returns>List of incomplete package states.</returns>
    public static List<PackageState> FindIncompletePackages(string outputPath)
    {
        var incompletePackages = new List<PackageState>();
        
        if (!Directory.Exists(outputPath)) return incompletePackages;
        
        foreach (var dir in Directory.GetDirectories(outputPath))
        {
            var state = LoadPackageState(dir);
            if (state != null && state.CurrentStep != PackagingStep.Complete && !state.IsExpired)
            {
                incompletePackages.Add(state);
            }
        }
        
        return incompletePackages;
    }

    /// <summary>
    /// Deletes a package directory completely.
    /// </summary>
    /// <param name="packagePath">The path to the package folder.</param>
    public static async Task DeletePackageAsync(string packagePath)
    {
        if (string.IsNullOrEmpty(packagePath) || !Directory.Exists(packagePath))
            return;

        await Task.Run(() =>
        {
            try
            {
                Directory.Delete(packagePath, true);
                LogService.Instance.Info($"Deleted package at {packagePath}", "PackageBuilder");
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Failed to delete package at {packagePath}", ex, "PackageBuilder");
                throw;
            }
        });
    }

    private static string SanitizeFileName(string name) => FormatUtils.SanitizeFileName(name);

    /// <summary>
    /// Updates an existing package with changed files from the Steam installation.
    /// Uses incremental sync to only copy modified files.
    /// </summary>
    /// <param name="game">The game from Steam library (has current BuildId).</param>
    /// <param name="packageDir">Path to the existing package.</param>
    /// <param name="options">Optional package options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Path to the updated package.</returns>
    public async Task<string> UpdatePackageAsync(InstalledGame game, string outputPath, PackageOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PackageOptions();
        options.IsUpdate = true;
        
        LogService.Instance.Info($"Starting incremental update for {game.Name} (PackageBuildId: {game.PackageBuildId} -> SteamBuildId: {game.BuildId})", "PackageBuilder");
        
        return await CreatePackageAsync(game, outputPath, options, ct);
    }

    /// <summary>
    /// Imports a SteamRoll package from a ZIP file.
    /// </summary>
    public Task<string> ImportPackageAsync(string zipPath, string destinationRoot, CancellationToken ct = default)
    {
        return _fileHandler.ImportPackageAsync(zipPath, destinationRoot, ct);
    }

    /// <summary>
    /// Fetches DLC information and generates unlock configuration.
    /// </summary>
    private async Task ConfigureDlcAsync(InstalledGame game, string packageDir)
    {
        try
        {
            // Fetch DLC list from Steam if not already done
            if (!game.DlcFetched || game.AvailableDlc.Count == 0)
            {
                ReportProgress("Fetching DLC list from Steam...", 86);
                game.AvailableDlc = await _dlcService.GetDlcListAsync(game.AppId);
                game.DlcFetched = true;
            }

            if (game.AvailableDlc.Count > 0)
            {
                ReportProgress($"Unlocking {game.AvailableDlc.Count} DLC...", 88);
                
                // Write DLC.txt to unlock ALL available DLC
                await _dlcService.WriteDlcConfigAsync(packageDir, game.AvailableDlc);
                
                LogService.Instance.Info($"DLC config written for {game.Name}: {game.AvailableDlc.Count} DLC", "PackageBuilder");
            }
            else
            {
                LogService.Instance.Info($"No DLC found for {game.Name}", "PackageBuilder");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"DLC config failed for {game.Name}: {ex.Message}", "PackageBuilder");
            // Don't fail the package, just skip DLC
        }
    }
}
