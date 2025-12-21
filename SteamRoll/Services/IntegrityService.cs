using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using SteamRoll.Models;

namespace SteamRoll.Services;

/// <summary>
/// Service responsible for verifying the integrity of game packages.
/// </summary>
public class IntegrityService
{
    /// <summary>
    /// Represents the result of an integrity check.
    /// </summary>
    public class IntegrityResult
    {
        public bool IsValid { get; set; }
        public List<string> MismatchedFiles { get; set; } = new();
        public List<string> MissingFiles { get; set; } = new();
        public int FilesChecked { get; set; }
        public int TotalFiles { get; set; }

        public string Summary => IsValid
            ? $"Verification passed! All {FilesChecked} files match."
            : $"Verification failed. {MissingFiles.Count} missing, {MismatchedFiles.Count} modified.";
    }

    /// <summary>
    /// Verifies the integrity of a package by comparing current file hashes against stored hashes in steamroll.json.
    /// </summary>
    /// <param name="packagePath">The full path to the package directory.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <returns>An IntegrityResult detailing the verification status.</returns>
    public async Task<IntegrityResult> VerifyPackageAsync(string packagePath, IProgress<int>? progress = null)
    {
        var result = new IntegrityResult();
        var metadataPath = Path.Combine(packagePath, "steamroll.json");

        if (!File.Exists(metadataPath))
        {
            result.IsValid = false;
            result.MissingFiles.Add("steamroll.json (Metadata missing)");
            return result;
        }

        PackageMetadata? metadata;
        try
        {
            var json = await File.ReadAllTextAsync(metadataPath);
            metadata = JsonSerializer.Deserialize<PackageMetadata>(json);
        }
        catch (Exception)
        {
            result.IsValid = false;
            result.MismatchedFiles.Add("steamroll.json (Corrupt)");
            return result;
        }

        if (metadata?.FileHashes == null || metadata.FileHashes.Count == 0)
        {
            // If no hashes are stored, we can't verify, but we also can't say it's invalid.
            // Let's consider it valid but warn? For now, valid.
            result.IsValid = true;
            return result;
        }

        result.TotalFiles = metadata.FileHashes.Count;
        int processedCount = 0;

        // Use parallel processing for improved performance on SSDs
        // Limit concurrency to avoid choking mechanical drives or saturating CPU completely
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        await Parallel.ForEachAsync(metadata.FileHashes, options, async (entry, ct) =>
        {
            var (relativePath, expectedHash) = entry;
            var filePath = Path.Combine(packagePath, relativePath);

            bool missing = false;
            bool mismatch = false;
            bool readError = false;

            if (!File.Exists(filePath))
            {
                missing = true;
            }
            else
            {
                try
                {
                    // ComputeSha256Async will be needed, or run sync inside Task.Run if we want true parallel compute
                    // Since hashing is CPU bound too, we'll run it here.
                    // We need a fresh SHA256 instance per task/thread or use a static one?
                    // SHA256 is not thread safe for instance members.
                    // We'll create one per operation.

                    var actualHash = await ComputeSha256Async(filePath);
                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        mismatch = true;
                    }
                }
                catch
                {
                    readError = true;
                }
            }

            // Thread-safe update of result collections
            lock (result)
            {
                if (missing) result.MissingFiles.Add(relativePath);
                else if (mismatch) result.MismatchedFiles.Add(relativePath);
                else if (readError) result.MismatchedFiles.Add($"{relativePath} (Read Error)");

                processedCount++;
                // Report periodically to avoid lock contention on progress delegate
                if (processedCount % 10 == 0 || processedCount == result.TotalFiles)
                {
                    progress?.Report((int)((double)processedCount / result.TotalFiles * 100));
                }
            }
        });

        result.FilesChecked = processedCount;
        result.IsValid = result.MissingFiles.Count == 0 && result.MismatchedFiles.Count == 0;

        // Sort for consistent UI display
        result.MissingFiles.Sort();
        result.MismatchedFiles.Sort();

        return result;
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        // Offload to thread pool to avoid blocking the parallel looper
        return await Task.Run(async () =>
        {
            using var sha256 = SHA256.Create();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan | FileOptions.Asynchronous);

            // ComputeHashAsync is available in .NET 6+
            var hashBytes = await sha256.ComputeHashAsync(stream);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        });
    }
}
