using System.Collections.Concurrent;
using System.IO;

namespace SteamRoll.Services;

/// <summary>
/// Service that pre-computes file hashes for packages during idle time.
/// Reduces transfer initialization time by caching XxHash64 values.
/// </summary>
public class BackgroundIndexingService : IDisposable
{
    private readonly CacheService _cacheService;
    private readonly SettingsService _settingsService;
    private CancellationTokenSource? _cts;
    private Task? _indexingTask;
    private bool _isRunning;
    private int _processedPackages;
    private int _totalPackages;

    /// <summary>
    /// Raised when indexing starts for a package.
    /// </summary>
    public event EventHandler<IndexingProgressEventArgs>? IndexingProgress;

    /// <summary>
    /// Raised when all indexing is complete.
    /// </summary>
    public event EventHandler? IndexingCompleted;

    public BackgroundIndexingService(CacheService cacheService, SettingsService settingsService)
    {
        _cacheService = cacheService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Returns true if background indexing is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Current progress (0-100).
    /// </summary>
    public int ProgressPercent => _totalPackages > 0 ? (_processedPackages * 100 / _totalPackages) : 0;

    /// <summary>
    /// Starts background indexing for all packages in the output directory.
    /// Runs at low priority to avoid impacting user operations.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        // Check if background indexing is enabled
        if (!(_settingsService.Settings.EnableBackgroundIndexing ?? true))
        {
            LogService.Instance.Debug("Background indexing is disabled in settings", "BackgroundIndexingService");
            return;
        }

        _cts = new CancellationTokenSource();
        _isRunning = true;
        _processedPackages = 0;
        _totalPackages = 0;

        _indexingTask = Task.Run(async () => await IndexPackagesAsync(_cts.Token), _cts.Token);
        
        LogService.Instance.Info("Background indexing started", "BackgroundIndexingService");
    }

    /// <summary>
    /// Stops background indexing.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;

        _cts?.Cancel();
        
        try
        {
            _indexingTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException) { }
        catch (OperationCanceledException) { }

        _isRunning = false;
        LogService.Instance.Info("Background indexing stopped", "BackgroundIndexingService");
    }

    private async Task IndexPackagesAsync(CancellationToken ct)
    {
        try
        {
            var outputPath = _settingsService.Settings.OutputPath;
            if (string.IsNullOrEmpty(outputPath) || !Directory.Exists(outputPath))
            {
                LogService.Instance.Debug("Output path not set or doesn't exist", "BackgroundIndexingService");
                return;
            }

            // Find all packages (directories with steamroll.json)
            var packageDirs = Directory.EnumerateDirectories(outputPath)
                .Where(d => File.Exists(Path.Combine(d, "steamroll.json")))
                .ToList();

            _totalPackages = packageDirs.Count;
            if (_totalPackages == 0)
            {
                LogService.Instance.Debug("No packages to index", "BackgroundIndexingService");
                return;
            }

            LogService.Instance.Info($"Found {_totalPackages} packages to index", "BackgroundIndexingService");

            foreach (var packageDir in packageDirs)
            {
                if (ct.IsCancellationRequested) break;

                await IndexPackageAsync(packageDir, ct);
                
                _processedPackages++;
                
                IndexingProgress?.Invoke(this, new IndexingProgressEventArgs
                {
                    PackageName = Path.GetFileName(packageDir),
                    ProcessedCount = _processedPackages,
                    TotalCount = _totalPackages
                });

                // Yield to other operations - run at low priority
                await Task.Delay(100, ct);
            }

            IndexingCompleted?.Invoke(this, EventArgs.Empty);
            LogService.Instance.Info($"Background indexing completed: {_processedPackages}/{_totalPackages} packages", "BackgroundIndexingService");
        }
        catch (OperationCanceledException)
        {
            LogService.Instance.Debug("Background indexing cancelled", "BackgroundIndexingService");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Background indexing error: {ex.Message}", ex, "BackgroundIndexingService");
        }
        finally
        {
            _isRunning = false;
        }
    }

    private async Task IndexPackageAsync(string packageDir, CancellationToken ct)
    {
        var packageName = Path.GetFileName(packageDir);
        var cacheKey = $"hashes:{packageName}";

        // Check if we already have cached hashes and they're still valid
        var cachedHashes = _cacheService.GetFileHashes(packageDir);
        if (cachedHashes != null)
        {
            LogService.Instance.Debug($"Package {packageName} already indexed, skipping", "BackgroundIndexingService");
            return;
        }

        LogService.Instance.Debug($"Indexing package: {packageName}", "BackgroundIndexingService");

        try
        {
            // Compute XxHash64 for all important files
            var hashes = new ConcurrentDictionary<string, string>();
            var files = Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".json") && !f.EndsWith(".txt") && !f.EndsWith(".bat") && !f.EndsWith(".sh"))
                .ToList();

            // Use parallel processing but limit concurrency to avoid disk thrashing
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = 2, // Low parallelism for background operation
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(files, options, async (file, token) =>
            {
                if (token.IsCancellationRequested) return;

                try
                {
                    var relativePath = Path.GetRelativePath(packageDir, file).Replace('\\', '/');
                    var hash = await Utils.MemoryMappedHasher.ComputeXxHash64Async(file, token);
                    hashes[relativePath] = hash;
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning($"Error hashing {file}: {ex.Message}", "BackgroundIndexingService");
                }
            });

            // Store in cache
            _cacheService.SetFileHashes(packageDir, new Dictionary<string, string>(hashes));
            
            LogService.Instance.Debug($"Indexed {hashes.Count} files in {packageName}", "BackgroundIndexingService");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Error indexing package {packageName}: {ex.Message}", "BackgroundIndexingService");
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}

/// <summary>
/// Event args for indexing progress updates.
/// </summary>
public class IndexingProgressEventArgs : EventArgs
{
    public required string PackageName { get; init; }
    public int ProcessedCount { get; init; }
    public int TotalCount { get; init; }
    public int ProgressPercent => TotalCount > 0 ? (ProcessedCount * 100 / TotalCount) : 0;
}
