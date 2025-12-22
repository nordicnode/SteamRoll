using System.IO;
using System.IO.Compression;

namespace SteamRoll.Services.Packaging;

/// <summary>
/// Handles file operations for packaging, including copying, syncing, and zip import.
/// </summary>
public class PackageFileHandler
{
    private DateTime _lastReportTime = DateTime.MinValue;
    private readonly object _progressLock = new();

    public event Action<string, int>? ProgressChanged;

    /// <summary>
    /// Synchronizes the package directory with the source, removing files that no longer exist.
    /// Preserves SteamRoll-specific files.
    /// </summary>
    public async Task SyncDirectoryAsync(string sourceDir, string packageDir, List<string>? excludedPaths, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            var packageFiles = Directory.GetFiles(packageDir, "*", SearchOption.AllDirectories);

            // List of files to always preserve (SteamRoll metadata, configs, etc.)
            var preservedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "steamroll.json",
                "LAUNCH.bat",
                "launch.sh",
                "README.txt",
                "install_deps.bat",
                ".steamroll_received",
                "steam_interfaces.txt"
            };

            foreach (var file in packageFiles)
            {
                ct.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(packageDir, file);

                // Skip preserved files
                if (preservedFiles.Contains(Path.GetFileName(file)) ||
                    relativePath.StartsWith("steam_settings", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sourcePath = Path.Combine(sourceDir, relativePath);

                // If file doesn't exist in source, delete it
                if (!File.Exists(sourcePath))
                {
                    try
                    {
                        File.Delete(file);
                        LogService.Instance.Debug($"Deleted orphaned file: {relativePath}", "PackageFileHandler");
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.Warning($"Failed to delete orphaned file {relativePath}: {ex.Message}", "PackageFileHandler");
                    }
                }
            }

            // Cleanup empty directories
            foreach (var dir in Directory.GetDirectories(packageDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    try
                    {
                        Directory.Delete(dir);
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.Debug($"Could not delete empty directory {dir}: {ex.Message}", "PackageFileHandler");
                    }
                }
            }
        }, ct);
    }

    /// <summary>
    /// Copies a directory recursively with progress reporting using limited concurrency. Skips files that already exist with matching size/timestamp.
    /// </summary>
    public async Task CopyDirectoryAsync(string sourceDir, string destDir, List<string>? excludedPaths = null, CancellationToken ct = default)
    {
        await Task.Run(async () =>
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

            var allFiles = dir.GetFiles("*", SearchOption.AllDirectories);
            var totalFiles = allFiles.Length;
            int copiedFiles = 0;

            // Prepare exclude patterns
            var excludePatterns = excludedPaths?.Select(p =>
            {
                // Normalize path separators
                return p.Replace('/', '\\').Trim('\\');
            }).ToList() ?? new List<string>();

            // Use Parallel.ForEachAsync for better scalability and cleaner code
            await Parallel.ForEachAsync(allFiles, new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = ct
            }, async (file, token) =>
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
                    // Handle IO exceptions with retries
                    int retries = 0;
                    const int maxRetries = 3;
                    while (true)
                    {
                        try
                        {
                            // Use async stream copy for better responsiveness and I/O handling
                            // Use FileShare.ReadWrite to prevent locking issues
                            using var sourceStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
                            using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
                            await sourceStream.CopyToAsync(destStream, token);

                            // Preserve timestamp
                            File.SetLastWriteTimeUtc(destPath, file.LastWriteTimeUtc);
                            break;
                        }
                        catch (IOException ioEx)
                        {
                            retries++;
                            if (retries > maxRetries)
                            {
                                LogService.Instance.Error($"Failed to copy {file.Name} after {maxRetries} retries: {ioEx.Message}", ioEx, "PackageFileHandler");
                                throw;
                            }

                            LogService.Instance.Warning($"Retry copy {retries}/{maxRetries} for {file.Name}: {ioEx.Message}", "PackageFileHandler");
                            await Task.Delay(100 * (int)Math.Pow(2, retries - 1), token);
                        }
                    }
                }

                // Increment atomically
                var currentCount = Interlocked.Increment(ref copiedFiles);

                // Report progress
                var progress = 10 + (int)(((double)currentCount / totalFiles) * 60); // 10-70%
                ThrottleProgress($"{(skip ? "Skipping" : "Copying")}: {relativePath}", progress);
            });
        }, ct);
    }

    private void ThrottleProgress(string status, int percentage)
    {
        var now = DateTime.UtcNow;
        lock (_progressLock)
        {
            // Report max 10 times per second (100ms) or if finished
            if ((now - _lastReportTime).TotalMilliseconds >= 100 || percentage >= 100)
            {
                _lastReportTime = now;
                ProgressChanged?.Invoke(status, percentage);
            }
        }
    }

    /// <summary>
    /// Imports a SteamRoll package from a ZIP file.
    /// </summary>
    public async Task<string> ImportPackageAsync(string zipPath, string destinationRoot, CancellationToken ct = default)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Package file not found", zipPath);

        return await Task.Run(async () =>
        {
            using var archive = ZipFile.OpenRead(zipPath);

            var rootEntry = archive.Entries.OrderBy(e => e.FullName.Length).FirstOrDefault();
            if (rootEntry == null) throw new InvalidDataException("Empty archive");

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
                targetDirName = commonRoot;
            }
            else
            {
                targetDirName = Path.GetFileNameWithoutExtension(zipPath);
            }

            var destPath = Path.Combine(destinationRoot, targetDirName);
            int counter = 1;
            while (Directory.Exists(destPath))
            {
                destPath = Path.Combine(destinationRoot, $"{targetDirName} ({counter++})");
            }

            Directory.CreateDirectory(destPath);
            LogService.Instance.Info($"Importing package to {destPath}", "PackageFileHandler");

            if (commonRoot != null)
            {
                var rootPrefix = commonRoot + "/";

                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/")) continue;

                    if (entry.FullName.StartsWith(rootPrefix))
                    {
                        var relativePath = entry.FullName.Substring(rootPrefix.Length);
                        if (string.IsNullOrEmpty(relativePath)) continue;

                        var fullOutputPath = Path.Combine(destPath, relativePath);

                        var fullDestPath = Path.GetFullPath(destPath);
                        if (!fullDestPath.EndsWith(Path.DirectorySeparatorChar.ToString()) && !fullDestPath.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                        {
                            fullDestPath += Path.DirectorySeparatorChar;
                        }

                        var fullTarget = Path.GetFullPath(fullOutputPath);
                        if (!fullTarget.StartsWith(fullDestPath, StringComparison.OrdinalIgnoreCase))
                        {
                            LogService.Instance.Warning($"Skipped potentially malicious zip entry: {entry.FullName}", "PackageFileHandler");
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
                        entry.ExtractToFile(fullOutputPath, true);
                    }
                }
            }
            else
            {
                archive.ExtractToDirectory(destPath);
            }

            if (!File.Exists(Path.Combine(destPath, "LAUNCH.bat")) &&
                !File.Exists(Path.Combine(destPath, "steamroll.json")))
            {
                LogService.Instance.Warning("Imported package might not be valid (missing LAUNCH.bat or steamroll.json)", "PackageFileHandler");
            }

            return destPath;
        }, ct);
    }
}
