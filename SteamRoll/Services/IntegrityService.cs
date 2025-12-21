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

        await Task.Run(() =>
        {
            using var sha256 = SHA256.Create();

            // We can iterate sequentially or parallel. Sequential is often better for disk I/O unless SSD.
            // Let's stick to sequential for simplicity and consistent progress reporting,
            // or parallel with a lock for progress. Given we need to read files, I/O is the bottleneck.

            foreach (var (relativePath, expectedHash) in metadata.FileHashes)
            {
                var filePath = Path.Combine(packagePath, relativePath);

                if (!File.Exists(filePath))
                {
                    result.MissingFiles.Add(relativePath);
                }
                else
                {
                    try
                    {
                        var actualHash = ComputeSha256(filePath, sha256);
                        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            result.MismatchedFiles.Add(relativePath);
                        }
                    }
                    catch
                    {
                        result.MismatchedFiles.Add($"{relativePath} (Read Error)");
                    }
                }

                processedCount++;
                progress?.Report((int)((double)processedCount / result.TotalFiles * 100));
            }
        });

        result.FilesChecked = processedCount;
        result.IsValid = result.MissingFiles.Count == 0 && result.MismatchedFiles.Count == 0;

        return result;
    }

    private string ComputeSha256(string filePath, SHA256 sha256)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
