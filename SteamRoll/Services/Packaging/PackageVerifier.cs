using System.IO;
using System.Text.Json;
using SteamRoll.Models;
using SteamRoll.Utils;

namespace SteamRoll.Services.Packaging;

/// <summary>
/// Verifies the integrity of game packages.
/// </summary>
public static class PackageVerifier
{
    /// <summary>
    /// Verifies the integrity of a package by comparing current file hashes against stored hashes.
    /// Uses parallel processing and memory-mapped files for large files.
    /// </summary>
    /// <param name="packageDir">Path to the package directory.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A tuple with (isValid, mismatches) where mismatches is a list of files that failed verification.</returns>
    public static async Task<(bool IsValid, List<string> Mismatches)> VerifyIntegrityAsync(
        string packageDir, CancellationToken ct = default)
    {
        var mismatches = new System.Collections.Concurrent.ConcurrentBag<string>();
        var metadataPath = System.IO.Path.Combine(packageDir, FileNames.STEAMROLL_JSON);

        if (!File.Exists(metadataPath))
        {
            return (false, new List<string> { $"{FileNames.STEAMROLL_JSON} not found" });
        }

        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, ct);
            var metadata = JsonSerializer.Deserialize<PackageMetadata>(json);

            if (metadata?.FileHashes == null || metadata.FileHashes.Count == 0)
            {
                return (true, new List<string>()); // No hashes stored, assume valid
            }

            // Process files in parallel for better performance
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(metadata.FileHashes, parallelOptions, async (entry, token) =>
            {
                var (relativePath, expectedHash) = entry;
                var filePath = System.IO.Path.Combine(packageDir, relativePath);

                if (!File.Exists(filePath))
                {
                    mismatches.Add($"Missing: {relativePath}");
                    return;
                }

                try
                {
                    // Use MemoryMappedHasher for efficient large file handling
                    var actualHash = await MemoryMappedHasher.ComputeSha256Async(filePath, token);

                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        mismatches.Add($"Modified: {relativePath}");
                    }
                }
                catch (Exception ex)
                {
                    mismatches.Add($"Error: {relativePath} ({ex.Message})");
                }
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Error verifying integrity: {ex.Message}", ex, "PackageVerifier");
            return (false, new List<string> { $"Verification error: {ex.Message}" });
        }

        var mismatchList = mismatches.ToList();
        mismatchList.Sort();
        return (mismatchList.Count == 0, mismatchList);
    }

    /// <summary>
    /// Synchronous wrapper for VerifyIntegrityAsync. Use Async version where possible.
    /// </summary>
    [Obsolete("Use VerifyIntegrityAsync instead")]
    public static (bool IsValid, List<string> Mismatches) VerifyIntegrity(string packageDir)
    {
        return Task.Run(() => VerifyIntegrityAsync(packageDir)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Generates SHA256 hashes for key files in a package directory.
    /// </summary>
    /// <param name="packageDir">The package directory path.</param>
    /// <param name="mode">The hashing mode to use. Defaults to CriticalOnly for performance.</param>
    public static Dictionary<string, string> GenerateFileHashes(string packageDir, FileHashMode mode = FileHashMode.CriticalOnly)
    {
        // Skip hashing entirely if mode is None
        if (mode == FileHashMode.None)
        {
            LogService.Instance.Debug("File hashing skipped (mode: None)", "PackageVerifier");
            return new Dictionary<string, string>();
        }

        var hashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        try
        {
            IEnumerable<string> files;

            if (mode == FileHashMode.CriticalOnly)
            {
                // Only hash Steam-related DLLs for faster package creation
                var criticalPatterns = new[] { "steam_api.dll", "steam_api64.dll" };
                files = criticalPatterns
                    .SelectMany(pattern => Directory.GetFiles(packageDir, pattern, SearchOption.AllDirectories))
                    .Distinct();
                
                LogService.Instance.Debug($"CriticalOnly mode: hashing {files.Count()} Steam DLLs", "PackageVerifier");
            }
            else
            {
                // All mode: hash all .exe and .dll files
                var extensions = new[] { ".exe", ".dll" };
                files = Directory.EnumerateFiles(packageDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
                
                LogService.Instance.Debug($"All mode: hashing all executables and DLLs", "PackageVerifier");
            }

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (file) =>
            {
                // Use forward slashes for cross-platform compatibility
                var relativePath = Path.GetRelativePath(packageDir, file).Replace('\\', '/');
                try
                {
                    // Use synchronous MemoryMappedHasher for large files
                    // Note: For hash generation at packaging time, SHA256 is used for compatibility
                    var hash = MemoryMappedHasher.ComputeXxHash64(file);
                    // We use XxHash internally but store SHA256 for backward compat
                    using var sha256 = System.Security.Cryptography.SHA256.Create();
                    using var stream = File.OpenRead(file);
                    var hashBytes = sha256.ComputeHash(stream);
                    hashes[relativePath] = Convert.ToHexString(hashBytes).ToLowerInvariant();
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning($"Error generating hash for {file}: {ex.Message}", "PackageVerifier");
                }
            });
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Error generating file hashes: {ex.Message}", "PackageVerifier");
        }

        return new Dictionary<string, string>(hashes);
    }
}

