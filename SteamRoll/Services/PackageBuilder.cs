using System.IO;
using System.IO.Compression;
using SteamRoll.Models;
using System.Text.Json;

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
    
    /// <summary>
    /// Event fired when packaging progress updates.
    /// </summary>
    public event Action<string, int>? ProgressChanged;

    public PackageBuilder(GoldbergService goldbergService, SettingsService settingsService, DlcService? dlcService = null, CreamApiService? creamApiService = null)
    {
        _goldbergService = goldbergService;
        _dlcService = dlcService ?? new DlcService();
        _creamApiService = creamApiService ?? new CreamApiService(settingsService);
        // Use shared singleton instance to avoid multiple HttpClient instances
        _steamStoreService = SteamStoreService.Instance;
    }

    /// <summary>
    /// Creates a complete game package with Goldberg Emulator applied.
    /// </summary>
    /// <param name="game">The game to package.</param>
    /// <param name="outputPath">Base output directory.</param>
    /// <param name="options">Packaging options.</param>
    /// <param name="ct">Cancellation token for aborting the operation.</param>
    /// <returns>Path to the created package.</returns>
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

        // Check available disk space
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outputPath)) ?? outputPath);
            // Add 200MB safety buffer
            var requiredSpace = game.SizeOnDisk + (200 * 1024 * 1024);

            if (driveInfo.AvailableFreeSpace < requiredSpace)
            {
                throw new IOException($"Insufficient disk space. Required: {FormatUtils.FormatBytes(requiredSpace)}, Available: {FormatUtils.FormatBytes(driveInfo.AvailableFreeSpace)}");
            }
        }
        catch (ArgumentException)
        {
            // Path might be invalid or network path that DriveInfo doesn't like, proceed with caution
            LogService.Instance.Warning($"Could not check disk space for {outputPath}", "PackageBuilder");
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

        try
        {
            // Initial Setup: Create directory if not resuming
            if (resumeState == null)
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
                await CopyDirectoryAsync(game.FullPath, packageDir, options.ExcludedPaths, ct);
                
                // Checkpoint
                SavePackageState(state);
            }

            // Step 2: Detect interfaces
            List<string> interfaces = new();
            if (state.CurrentStep <= PackagingStep.DetectingInterfaces)
            {
                state.CurrentStep = PackagingStep.DetectingInterfaces;
                SavePackageState(state);
                
                ReportProgress("Detecting Steam interfaces...", 70);
                interfaces = DetectGameInterfaces(packageDir);
                
                // Checkpoint
                SavePackageState(state);
            }
            else
            {
                // Re-detect if skipping (it's fast)
                interfaces = DetectGameInterfaces(packageDir); 
            }

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
                        emulatorApplied = _goldbergService.ApplyGoldberg(packageDir, game.AppId, options.GoldbergConfig);
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
                    emulatorApplied = _goldbergService.ApplyGoldberg(packageDir, game.AppId, options.GoldbergConfig);
                    if (interfaces.Count > 0) _goldbergService.CreateInterfacesFile(packageDir, interfaces);
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
                
                CreatePackageMetadata(packageDir, game, emulatorApplied, options.Mode, emulatorVersion, storeDetails);
                
                // Note: GenerateFileHashes is called inside CreatePackageMetadata, no need to call again
                
                // Checkpoint
                SavePackageState(state);
            }

            // Step 6: Create Launcher
            if (state.CurrentStep <= PackagingStep.CreatingLauncher)
            {
                state.CurrentStep = PackagingStep.CreatingLauncher;
                SavePackageState(state);
                
                CreateLauncher(packageDir, game, options.LauncherArguments);
                
                // Checkpoint
                SavePackageState(state);
            }

            // Step 7: Integrity Verification (Post-creation check)
            // Added check to ensure hashes are generated if metadata step was skipped but hashes are missing
            // But logic above includes GenerateFileHashes in Metadata step, so skipping implies done.

            // Step 8: Zip Archive
            if (state.CurrentStep <= PackagingStep.CreatingZip)
            {
                state.CurrentStep = PackagingStep.CreatingZip;
                SavePackageState(state);
                
                ReportProgress("Creating ZIP archive...", 95);
                var zipPath = await CreateZipArchiveAsync(packageDir, options.ZipCompressionLevel, ct);
                
                // Checkpoint
                SavePackageState(state);
            }

            state.CurrentStep = PackagingStep.Complete;
            ClearPackageState(packageDir); // Success - cleanup state
            
            ReportProgress("Package complete!", 100);
            LogService.Instance.Info($"Package and ZIP created: {packageDir}", "PackageBuilder");
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
    /// Creates a ZIP archive of the package directory for easy sharing with progress tracking.
    /// Optionally deletes the source folder after archiving.
    /// </summary>
    private async Task<string> CreateZipArchiveAsync(string packageDir, CompressionLevel compressionLevel = CompressionLevel.Optimal, CancellationToken ct = default, bool deleteSourceFolder = true)
    {
        var folderName = System.IO.Path.GetFileName(packageDir);
        var parentDir = System.IO.Path.GetDirectoryName(packageDir) ?? packageDir;
        var zipPath = System.IO.Path.Combine(parentDir, $"{folderName} - SteamRoll.zip");
        
        // Delete existing ZIP if present
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }
        
        await Task.Run(async () =>
        {
            // Get all files and directories to compress
            var files = Directory.GetFiles(packageDir, "*", SearchOption.AllDirectories);
            var directories = Directory.GetDirectories(packageDir, "*", SearchOption.AllDirectories);
            var totalBytes = files.Sum(f => new FileInfo(f).Length);
            long processedBytes = 0;

            using var zipStream = new FileStream(zipPath, FileMode.Create);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

            // Add empty directories first
            foreach (var dir in directories)
            {
                 // Only add empty directories (files handle their own paths)
                 if (Directory.GetFileSystemEntries(dir).Length == 0)
                 {
                     var relativePath = Path.GetRelativePath(packageDir, dir);
                     var entryName = Path.Combine(folderName, relativePath).Replace("\\", "/");

                     // Directory entries must end with a slash
                     if (!entryName.EndsWith("/")) entryName += "/";

                     archive.CreateEntry(entryName);
                 }
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(packageDir, file);
                // Include the base folder name in the zip structure
                // ZIP spec requires forward slashes
                var entryName = Path.Combine(folderName, relativePath).Replace("\\", "/");

                var entry = archive.CreateEntry(entryName, compressionLevel);

                // Set the LastWriteTime to preserve timestamp
                entry.LastWriteTime = File.GetLastWriteTime(file);

                using var sourceStream = File.OpenRead(file);
                using var entryStream = entry.Open();

                // Copy with progress
                var buffer = new byte[81920]; // 80KB buffer
                int bytesRead;
                while ((bytesRead = await sourceStream.ReadAsync(buffer, ct)) > 0)
                {
                    await entryStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    processedBytes += bytesRead;

                    // Throttle updates
                    var percentage = totalBytes > 0 ? 90 + (int)((double)processedBytes / totalBytes * 10) : 100;
                    ThrottleProgress("Creating ZIP archive...", percentage); // 90-100%
                }
            }
        }, ct);
        
        var zipSize = new FileInfo(zipPath).Length;
        LogService.Instance.Info($"Created ZIP archive: {zipPath} ({FormatUtils.FormatBytes(zipSize)})", "PackageBuilder");
        
        // Delete the source folder - only keep the ZIP
        if (deleteSourceFolder && Directory.Exists(packageDir))
        {
            await Task.Run(() => Directory.Delete(packageDir, true), ct);
            LogService.Instance.Info($"Removed source folder, keeping only ZIP", "PackageBuilder");
        }
        
        return zipPath;
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
    /// Creates package metadata and README.
    /// </summary>
    private void CreatePackageMetadata(string packageDir, InstalledGame game, bool emulatorApplied, 
        PackageMode mode = PackageMode.Goldberg, string? emulatorVersion = null, SteamGameDetails? storeDetails = null)
    {
        var modeName = mode == PackageMode.CreamApi ? "CreamAPI" : "Goldberg";
        var versionDisplay = !string.IsNullOrEmpty(emulatorVersion) ? emulatorVersion : "unknown";
        
        var emulatorStatus = emulatorApplied 
            ? $"✓ Ready to play ({modeName} {versionDisplay})"
            : $"⚠ Manual setup required ({modeName} not applied)";

        // Build the README with richer game info
        var sb = new System.Text.StringBuilder();
        
        // Header
        sb.AppendLine("╔══════════════════════════════════════════════════════════════════════════════╗");
        sb.AppendLine($"║  {game.Name,-76} ║");
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════╣");
        sb.AppendLine($"║  Packaged by SteamRoll • {DateTime.Now:MMMM d, yyyy,-50} ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        
        // Game description from Steam
        if (storeDetails != null && !string.IsNullOrEmpty(storeDetails.Description))
        {
            sb.AppendLine("ABOUT THIS GAME");
            sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");
            // Word wrap the description to ~78 chars
            var desc = System.Net.WebUtility.HtmlDecode(storeDetails.Description);
            desc = System.Text.RegularExpressions.Regex.Replace(desc, "<[^>]+>", ""); // Strip HTML
            sb.AppendLine(WordWrap(desc, 78));
            sb.AppendLine();
        }
        
        // Game details section
        sb.AppendLine("GAME DETAILS");
        sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");
        sb.AppendLine($"  Steam AppID:    {game.AppId}");
        sb.AppendLine($"  Build:          {game.Version}");
        sb.AppendLine($"  Size:           {game.FormattedSize}");
        
        if (storeDetails != null)
        {
            if (!string.IsNullOrEmpty(storeDetails.ReleaseDate))
                sb.AppendLine($"  Released:       {storeDetails.ReleaseDate}");
            
            if (storeDetails.Developers.Count > 0)
                sb.AppendLine($"  Developer:      {storeDetails.DevelopersDisplay}");
            
            if (storeDetails.Publishers.Count > 0 && storeDetails.Publishers[0] != storeDetails.Developers.FirstOrDefault())
                sb.AppendLine($"  Publisher:      {storeDetails.PublishersDisplay}");
            
            if (storeDetails.Genres.Count > 0)
                sb.AppendLine($"  Genres:         {storeDetails.GenresDisplay}");
        }
        sb.AppendLine();
        
        // Reviews & Ratings section
        if (storeDetails != null && (storeDetails.ReviewPositivePercent.HasValue || storeDetails.MetacriticScore.HasValue))
        {
            sb.AppendLine("RATINGS");
            sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");
            
            if (storeDetails.ReviewPositivePercent.HasValue)
            {
                sb.AppendLine($"  Steam Reviews:  {storeDetails.ReviewDescription} ({storeDetails.ReviewPositivePercent}% positive)");
                if (storeDetails.ReviewTotalCount.HasValue)
                    sb.AppendLine($"                  Based on {storeDetails.ReviewTotalCount:N0} reviews");
            }
            
            if (storeDetails.MetacriticScore.HasValue)
                sb.AppendLine($"  Metacritic:     {storeDetails.MetacriticScore}/100");
            
            sb.AppendLine();
        }
        
        // Package status
        sb.AppendLine("PACKAGE STATUS");
        sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");
        sb.AppendLine($"  {emulatorStatus}");
        if (game.HasDlc && game.AvailableDlc.Count > 0)
            sb.AppendLine($"  {game.AvailableDlc.Count} DLC unlocked");
        sb.AppendLine();
        
        // How to play section
        if (emulatorApplied)
        {
            sb.AppendLine("HOW TO PLAY");
            sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine("  1. Copy this entire folder to the target PC");
            sb.AppendLine("  2. Run LAUNCH.bat (or the game executable directly)");
            sb.AppendLine("  3. No Steam required!");
            sb.AppendLine();
            sb.AppendLine("  For LAN multiplayer:");
            sb.AppendLine("  • Both PCs must be on the same network");
            sb.AppendLine("  • Run the game on both PCs");
            sb.AppendLine("  • Use in-game LAN/Local multiplayer options");
            sb.AppendLine();
            sb.AppendLine("  Customization:");
            sb.AppendLine("  • Edit steam_settings/force_account_name.txt to change your player name");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("MANUAL SETUP REQUIRED");
            sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine("  Emulator DLLs were not applied automatically. To complete setup:");
            sb.AppendLine();
            sb.AppendLine("  1. Download Goldberg Emulator from:");
            sb.AppendLine("     https://gitlab.com/Mr_Goldberg/goldberg_emulator/-/releases");
            sb.AppendLine();
            sb.AppendLine("  2. Find steam_api.dll / steam_api64.dll in this folder");
            sb.AppendLine("  3. Replace them with the Goldberg versions");
            sb.AppendLine("  4. Run the game executable directly");
            sb.AppendLine();
        }
        
        // Footer
        sb.AppendLine("═══════════════════════════════════════════════════════════════════════════════");
        sb.AppendLine("  This package is for personal household LAN use only.");
        sb.AppendLine("  Created with SteamRoll • https://github.com/steamroll");
        sb.AppendLine("═══════════════════════════════════════════════════════════════════════════════");

        File.WriteAllText(Path.Combine(packageDir, "README.txt"), sb.ToString());

        // Create machine-readable metadata
        try
        {
            var metadata = new PackageMetadata
            {
                AppId = game.AppId,
                Name = game.Name,
                BuildId = game.BuildId,
                CreatedDate = DateTime.Now,
                CreatorVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
                EmulatorMode = mode.ToString(),
                EmulatorVersion = versionDisplay,
                OriginalSize = game.SizeOnDisk,
                FileHashes = GenerateFileHashes(packageDir)
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(metadata, options);
            File.WriteAllText(Path.Combine(packageDir, "steamroll.json"), json);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to create steamroll.json metadata: {ex.Message}", "PackageBuilder");
        }
    }
    
    /// <summary>
    /// Generates SHA256 hashes for key files in a package directory.
    /// </summary>
    private Dictionary<string, string> GenerateFileHashes(string packageDir)
    {
        var hashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var extensions = new[] { ".exe", ".dll" };
        
        try
        {
            var files = Directory.EnumerateFiles(packageDir, "*.*", SearchOption.AllDirectories);

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (file) =>
            {
                var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (extensions.Contains(ext))
                {
                    var relativePath = System.IO.Path.GetRelativePath(packageDir, file);
                    try
                    {
                        using var sha256 = System.Security.Cryptography.SHA256.Create();
                        using var stream = File.OpenRead(file);
                        var hashBytes = sha256.ComputeHash(stream);
                        hashes[relativePath] = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    }
                    catch (Exception ex)
                    {
                         LogService.Instance.Warning($"Error generating hash for {file}: {ex.Message}", "PackageBuilder");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Error generating file hashes: {ex.Message}", "PackageBuilder");
        }
        
        return new Dictionary<string, string>(hashes);
    }
    
    /// <summary>
    /// Verifies the integrity of a package by comparing current file hashes against stored hashes.
    /// </summary>
    /// <param name="packageDir">Path to the package directory.</param>
    /// <returns>A tuple with (isValid, mismatches) where mismatches is a list of files that failed verification.</returns>
    public static (bool IsValid, List<string> Mismatches) VerifyIntegrity(string packageDir)
    {
        var mismatches = new List<string>();
        var metadataPath = System.IO.Path.Combine(packageDir, "steamroll.json");
        
        if (!File.Exists(metadataPath))
        {
            return (false, new List<string> { "steamroll.json not found" });
        }
        
        try
        {
            var json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<PackageMetadata>(json);
            
            if (metadata?.FileHashes == null || metadata.FileHashes.Count == 0)
            {
                return (true, mismatches); // No hashes stored, assume valid
            }
            
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            
            foreach (var (relativePath, expectedHash) in metadata.FileHashes)
            {
                var filePath = System.IO.Path.Combine(packageDir, relativePath);
                
                if (!File.Exists(filePath))
                {
                    mismatches.Add($"Missing: {relativePath}");
                    continue;
                }
                
                using var stream = File.OpenRead(filePath);
                var hashBytes = sha256.ComputeHash(stream);
                var actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                
                if (actualHash != expectedHash)
                {
                    mismatches.Add($"Modified: {relativePath}");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error verifying integrity: {ex.Message}", ex, "PackageBuilder");
            return (false, new List<string> { $"Verification error: {ex.Message}" });
        }
        
        return (mismatches.Count == 0, mismatches);
    }
    
    /// <summary>
    /// Word wraps text to a specified width.
    /// </summary>
    private static string WordWrap(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) return "";
        
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentLine = "";
        
        foreach (var word in words)
        {
            if (currentLine.Length + word.Length + 1 <= width)
            {
                currentLine += (currentLine.Length > 0 ? " " : "") + word;
            }
            else
            {
                if (currentLine.Length > 0)
                    lines.Add(currentLine);
                currentLine = word;
            }
        }
        if (currentLine.Length > 0)
            lines.Add(currentLine);
        
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Creates a launcher batch file.
    /// </summary>
    private void CreateLauncher(string packageDir, InstalledGame game, string? customArgs = null)
    {
        // Find likely game executables
        var exeFiles = Directory.GetFiles(packageDir, "*.exe", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Contains("redist", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Contains("setup", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Try to find the main game executable
        var mainExe = exeFiles.FirstOrDefault(f => 
            System.IO.Path.GetFileNameWithoutExtension(f).Equals(game.InstallDir, StringComparison.OrdinalIgnoreCase)) ??
            exeFiles.FirstOrDefault(f => 
            System.IO.Path.GetFileNameWithoutExtension(f).Contains(game.Name.Split(' ')[0], StringComparison.OrdinalIgnoreCase)) ??
            exeFiles.FirstOrDefault();

        if (mainExe != null)
        {
            var exeDir = System.IO.Path.GetDirectoryName(mainExe)!;
            var exeName = System.IO.Path.GetFileName(mainExe);
            var relativeExePath = System.IO.Path.GetRelativePath(packageDir, mainExe);
            
            // Check if this is a Source engine game
            var sourceGameInfo = DetectSourceEngineGame(packageDir, exeDir);
            
            if (sourceGameInfo != null)
            {
                // Create Source engine specific launcher
                // Prefer hl2.exe if it exists in the package root
                var hl2Path = System.IO.Path.Combine(packageDir, "hl2.exe");
                var sourceExePath = relativeExePath;
                
                if (File.Exists(hl2Path))
                {
                    sourceExePath = "hl2.exe";
                }
                
                CreateSourceEngineLauncher(packageDir, game.Name, sourceExePath, sourceGameInfo.Value, customArgs);
            }
            else
            {
                // Standard launcher
                var relativeDir = System.IO.Path.GetRelativePath(packageDir, exeDir);
                var cdPath = relativeDir == "." ? "" : relativeDir;

                var args = !string.IsNullOrWhiteSpace(customArgs) ? $" {customArgs}" : "";

                // Windows Batch
                var launcherContent = $"""
                    @echo off
                    title {game.Name}
                    cd /d "%~dp0{cdPath}"
                    start "" "{exeName}"{args}
                    """;
                
                File.WriteAllText(Path.Combine(packageDir, "LAUNCH.bat"), launcherContent);

                // Linux Shell Script
                // Assuming standard Wine usage or just launching if native (but we're packaging exe, so likely wine)
                // We convert backslashes to forward slashes for Linux paths
                var linuxCdPath = cdPath.Replace("\\", "/");

                var shellScriptContent = $$"""
                    #!/bin/bash
                    # SteamRoll Launcher for {{game.Name}}

                    # Set working directory to script location + relative path
                    DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
                    cd "$DIR/{{linuxCdPath}}"

                    # Add current directory to library path (for Goldberg steam_api.so)
                    export LD_LIBRARY_PATH=".:$LD_LIBRARY_PATH"

                    echo "Launching {{game.Name}}..."

                    # Try to launch with wine if it's an exe, or directly if it's executable
                    if [[ "{{exeName}}" == *.exe ]]; then
                        if command -v wine &> /dev/null; then
                            wine "{{exeName}}"{{args}}
                        else
                            echo "Wine not found. Attempting to run directly..."
                            ./"{{exeName}}"{{args}}
                        fi
                    else
                         ./"{{exeName}}"{{args}}
                    fi
                    """;

                // Use LF line endings for Linux script
                shellScriptContent = shellScriptContent.Replace("\r\n", "\n");

                var shPath = Path.Combine(packageDir, "launch.sh");
                File.WriteAllText(shPath, shellScriptContent);
            }
        }
    }
    
    /// <summary>
    /// Detects if this is a Source engine game and returns the game content folder info.
    /// </summary>
    private (string ContentFolder, string ContentFolderRelativeToRoot)? DetectSourceEngineGame(string packageDir, string exeDir)
    {
        var options = new EnumerationOptions 
        { 
            MatchCasing = MatchCasing.CaseInsensitive, 
            RecurseSubdirectories = true 
        };
        
        var gameInfoFiles = Directory.GetFiles(packageDir, "gameinfo.txt", options);
        
        if (gameInfoFiles.Length == 0)
            return null;
            
        // Filter out common engine/system folders
        // Filter out common engine/system folders and sort by path depth (length)
        // We want the shallowest gameinfo.txt (closest to root) to avoid finding backups or deeply nested files
        var validGameInfo = gameInfoFiles
            .Select(path => new { 
                Path = path, 
                Dir = System.IO.Path.GetDirectoryName(path)!,
                Folder = new DirectoryInfo(Path.GetDirectoryName(path)!).Name 
            })
            .Where(x => !x.Folder.Equals("platform", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.Folder.Equals("bin", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.Folder.Equals("hl2", StringComparison.OrdinalIgnoreCase)) // Skip base HL2 content
            // Count separators for depth to avoid array allocation overhead of Split()
            .OrderBy(x => x.Path.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
            .FirstOrDefault();
            
        if (validGameInfo == null)
            return null;
            
        // Get the folder name and its path relative to package root
        var relativeToRoot = System.IO.Path.GetRelativePath(packageDir, validGameInfo.Dir);
        
        // Fix for "Setup file 'GameInfo.txt' doesn't exist in subdirectory"
        // Ensure the game parameter just uses the folder name if it's a direct child, 
        // or the relative path if it's nested (though -game typically expects just a name if in root).
        // For standard source games, the structure is usually:
        // Root/
        //   hl2.exe
        //   [GameName]/
        //     gameinfo.txt
        // So we usually just want [GameName].
        
        var gameParam = validGameInfo.Folder;
        
        LogService.Instance.Info($"Source engine detected: content folder '{validGameInfo.Folder}' at '{relativeToRoot}'", "PackageBuilder");
        
        return (gameParam, relativeToRoot);
    }
    
    /// <summary>
    /// Creates a launcher specifically for Source engine games.
    /// Source engine requires the working directory to be set correctly and -game to point to the content folder.
    /// </summary>
    private void CreateSourceEngineLauncher(string packageDir, string gameName, string relativeExePath, (string ContentFolder, string ContentFolderRelativeToRoot) sourceInfo, string? customArgs = null)
    {
        // For Source engine:
        // 1. We need to run from the package root (where both bin/ and the content folder are)
        // 2. The -game parameter should be the content folder name (e.g., "stanley")
        // 3. The executable path is relative to that working directory
        
        // The key insight is that Source engine looks for -game folder relative to the current working directory,
        // NOT relative to the executable location.
        
        var args = !string.IsNullOrWhiteSpace(customArgs) ? $" {customArgs}" : "";

        // Windows Batch
        var launcherContent = $"""
            @echo off
            title {gameName}
            rem Source Engine Game Launcher
            rem Setting working directory to package root where game content folder exists
            cd /d "%~dp0"
            
            rem Launch game with -game pointing to content folder: {sourceInfo.ContentFolder}
            start "" "{relativeExePath}" -game "{sourceInfo.ContentFolderRelativeToRoot}"{args}
            """;
        
        File.WriteAllText(Path.Combine(packageDir, "LAUNCH.bat"), launcherContent);
        
        // Linux Shell Script
        var linuxRelativeExePath = relativeExePath.Replace("\\", "/");
        var linuxGamePath = sourceInfo.ContentFolderRelativeToRoot.Replace("\\", "/");

        var shellScriptContent = $$"""
            #!/bin/bash
            # SteamRoll Source Engine Launcher for {{gameName}}

            # Set working directory to package root
            DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
            cd "$DIR"

            # Add current directory to library path
            export LD_LIBRARY_PATH=".:$LD_LIBRARY_PATH"

            echo "Launching {{gameName}} (Source Engine)..."

            if command -v wine &> /dev/null; then
                wine "{{linuxRelativeExePath}}" -game "{{linuxGamePath}}"{{args}}
            else
                echo "Wine not found. Attempting to run directly..."
                ./"{{linuxRelativeExePath}}" -game "{{linuxGamePath}}"{{args}}
            fi
            """;

        // Use LF line endings
        shellScriptContent = shellScriptContent.Replace("\r\n", "\n");

        var shPath = Path.Combine(packageDir, "launch.sh");
        File.WriteAllText(shPath, shellScriptContent);

        LogService.Instance.Info($"Created Source engine launcher: {relativeExePath} -game \"{sourceInfo.ContentFolderRelativeToRoot}\"", "PackageBuilder");
    }

    /// <summary>
    /// Copies a directory recursively with progress reporting using parallelism. Skips files that already exist with matching size/timestamp.
    /// </summary>
    private async Task CopyDirectoryAsync(string sourceDir, string destDir, List<string>? excludedPaths = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

            var allFiles = dir.GetFiles("*", SearchOption.AllDirectories);
            var totalFiles = allFiles.Length;
            int copiedFiles = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct
            };

            // Prepare exclude patterns
            var excludePatterns = excludedPaths?.Select(p =>
            {
                // Normalize path separators
                return p.Replace('/', '\\').Trim('\\');
            }).ToList() ?? new List<string>();

            Parallel.ForEach(allFiles, parallelOptions, (file) =>
            {
                var relativePath = System.IO.Path.GetRelativePath(sourceDir, file.FullName);

                // Check exclusions
                if (excludePatterns.Any(pattern =>
                {
                    // Basic wildcard support: * at end matches any subpath
                    if (pattern.EndsWith("*"))
                    {
                        var basePattern = pattern.TrimEnd('*');
                        return relativePath.StartsWith(basePattern, StringComparison.OrdinalIgnoreCase);
                    }
                    return relativePath.Equals(pattern, StringComparison.OrdinalIgnoreCase);
                }))
                {
                    Interlocked.Increment(ref copiedFiles);
                    return;
                }

                var destPath = System.IO.Path.Combine(destDir, relativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                // Check if file exists and matches
                bool skip = false;
                if (File.Exists(destPath))
                {
                    var destInfo = new FileInfo(destPath);
                    if (destInfo.Length == file.Length &&
                        Math.Abs((destInfo.LastWriteTimeUtc - file.LastWriteTimeUtc).TotalSeconds) < 2)
                    {
                        skip = true;
                    }
                }

                if (!skip)
                {
                    // CopyTo is not thread safe if two threads try to write to same file, but we are writing to unique paths
                    // However, we need to handle potential IO exceptions for file access
                    int retries = 0;
                    const int maxRetries = 3;
                    while (true)
                    {
                        try
                        {
                            file.CopyTo(destPath, overwrite: true);
                            break;
                        }
                        catch (IOException ioEx)
                        {
                            retries++;
                            if (retries > maxRetries)
                            {
                                LogService.Instance.Error($"Failed to copy {file.Name} after {maxRetries} retries: {ioEx.Message}", ioEx, "PackageBuilder");
                                throw;
                            }

                            LogService.Instance.Warning($"Retry copy {retries}/{maxRetries} for {file.Name}: {ioEx.Message}", "PackageBuilder");
                            // Exponential backoff: 100ms, 200ms, 400ms...
                            Thread.Sleep(100 * (int)Math.Pow(2, retries - 1));
                        }
                    }
                }

                // Increment atomically
                var currentCount = Interlocked.Increment(ref copiedFiles);

                // Report progress with throttling to avoid swamping the UI thread
                var progress = 10 + (int)(((double)currentCount / totalFiles) * 60); // 10-70%
                ThrottleProgress($"{(skip ? "Skipping" : "Copying")}: {relativePath}", progress);
            });
        }, ct);
    }

    private DateTime _lastReportTime = DateTime.MinValue;
    private readonly object _progressLock = new();

    private void ReportProgress(string status, int percentage)
    {
        ProgressChanged?.Invoke(status, percentage);
    }

    /// <summary>
    /// Reports progress but limits update frequency to avoid UI saturation.
    /// </summary>
    private void ThrottleProgress(string status, int percentage)
    {
        var now = DateTime.UtcNow;
        lock (_progressLock)
        {
            // Report max 10 times per second (100ms) or if finished
            if ((now - _lastReportTime).TotalMilliseconds >= 100 || percentage >= 100)
            {
                _lastReportTime = now;
                // Invoke directly on the current thread (ThreadPool thread)
                // The UI event handler is responsible for marshaling to UI thread if needed (which it does via Dispatcher.Invoke)
                // Using Task.Run causes race conditions in event ordering
                ProgressChanged?.Invoke(status, percentage);
            }
        }
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
        catch { }
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
    /// Imports a SteamRoll package from a ZIP file.
    /// </summary>
    /// <param name="zipPath">Path to the zip file.</param>
    /// <param name="destinationRoot">The root directory to import to (e.g. Settings.OutputPath).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path to the imported package directory.</returns>
    public async Task<string> ImportPackageAsync(string zipPath, string destinationRoot, CancellationToken ct = default)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Package file not found", zipPath);

        return await Task.Run(async () =>
        {
            using var archive = ZipFile.OpenRead(zipPath);

            // Validate package
            // Look for steamroll.json or LAUNCH.bat to confirm it's a game package
            // Also need to determine the root folder name inside the zip to avoid double nesting

            var rootEntry = archive.Entries.OrderBy(e => e.FullName.Length).FirstOrDefault();
            if (rootEntry == null) throw new InvalidDataException("Empty archive");

            // Heuristic: Check if the zip has a single top-level directory
            // or if it's a flat list of files.
            // SteamRoll packages created by us usually have "GameName/..." structure.

            // Detect if all entries are within a single root folder
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.FullName)).ToList();
            string? commonRoot = null;

            if (entries.Count > 0)
            {
                var firstPath = entries[0].FullName;
                var parts = firstPath.Split('/');
                if (parts.Length > 1)
                {
                    var potentialRoot = parts[0] + "/";
                    // Check if all entries start with this root
                    if (entries.All(e => e.FullName.StartsWith(potentialRoot, StringComparison.Ordinal)))
                    {
                        commonRoot = parts[0];
                    }
                }
            }

            string targetDirName;

            if (commonRoot != null)
            {
                // Single root folder in zip - use that name
                targetDirName = commonRoot;
            }
            else
            {
                // Multiple roots or flat files - use zip name
                targetDirName = Path.GetFileNameWithoutExtension(zipPath);
            }

            // Ensure unique destination path
            var destPath = Path.Combine(destinationRoot, targetDirName);
            int counter = 1;
            while (Directory.Exists(destPath))
            {
                destPath = Path.Combine(destinationRoot, $"{targetDirName} ({counter++})");
            }

            Directory.CreateDirectory(destPath);
            LogService.Instance.Info($"Importing package to {destPath}", "PackageBuilder");

            if (commonRoot != null)
            {
                // Zip has "GameName/file.txt" structure
                // Extract contents OF the root folder INTO destPath, stripping the root folder name

                var rootPrefix = commonRoot + "/";

                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/")) continue; // Skip directories

                    // Strip the root prefix
                    if (entry.FullName.StartsWith(rootPrefix))
                    {
                        var relativePath = entry.FullName.Substring(rootPrefix.Length);
                        if (string.IsNullOrEmpty(relativePath)) continue;

                        var fullOutputPath = Path.Combine(destPath, relativePath);

                        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
                        entry.ExtractToFile(fullOutputPath, true);
                    }
                }
            }
            else
            {
                // Flat structure or mixed roots - extract everything into destPath as is
                archive.ExtractToDirectory(destPath);
            }

            // Post-import validation
            if (!File.Exists(Path.Combine(destPath, "LAUNCH.bat")) &&
                !File.Exists(Path.Combine(destPath, "steamroll.json")))
            {
                LogService.Instance.Warning("Imported package might not be valid (missing LAUNCH.bat or steamroll.json)", "PackageBuilder");
            }

            return destPath;
        }, ct);
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

/// <summary>
/// Options for package creation.
/// </summary>
public class PackageOptions
{
    /// <summary>
    /// Whether to include DLC content.
    /// </summary>
    public bool IncludeDlc { get; set; } = true;

    /// <summary>
    /// Whether to compress the package.
    /// </summary>
    public bool Compress { get; set; } = false;
    
    /// <summary>
    /// Compression level for the zip archive.
    /// </summary>
    public CompressionLevel ZipCompressionLevel { get; set; } = CompressionLevel.Optimal;

    /// <summary>
    /// The emulation mode to use for the package.
    /// </summary>
    public PackageMode Mode { get; set; } = PackageMode.Goldberg;

    /// <summary>
    /// User-provided notes about this package.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Custom arguments to pass to the game executable in the launcher.
    /// </summary>
    public string? LauncherArguments { get; set; }

    /// <summary>
    /// List of file paths (relative to game root) to exclude from the package.
    /// Supports basic wildcards (*).
    /// </summary>
    public List<string> ExcludedPaths { get; set; } = new();

    /// <summary>
    /// Tags for organizing packages.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Advanced Goldberg configuration (null = use defaults).
    /// </summary>
    public GoldbergConfig? GoldbergConfig { get; set; }
}

/// <summary>
/// Advanced configuration options for Goldberg Emulator.
/// </summary>
public class GoldbergConfig
{
    /// <summary>
    /// The account name shown in-game.
    /// </summary>
    public string AccountName { get; set; } = "Player";

    /// <summary>
    /// Whether to disable all network functionality.
    /// </summary>
    public bool DisableNetworking { get; set; } = true;

    /// <summary>
    /// Whether to disable the Steam overlay.
    /// </summary>
    public bool DisableOverlay { get; set; } = true;

    /// <summary>
    /// Whether to enable LAN multiplayer functionality.
    /// </summary>
    public bool EnableLan { get; set; } = false;
}

/// <summary>
/// The Steam emulation mode to apply to the package.
/// </summary>
public enum PackageMode
{
    /// <summary>
    /// Goldberg Emulator - Full Steam replacement, works offline.
    /// Best for most games.
    /// </summary>
    Goldberg,
    
    /// <summary>
    /// CreamAPI - Steam proxy that unlocks DLC while maintaining some Steam features.
    /// Use when Goldberg doesn't work or you need Steam integration.
    /// </summary>
    CreamApi
}
