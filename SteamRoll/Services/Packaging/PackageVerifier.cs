using System.IO;
using System.Text.Json;
using SteamRoll.Models;

namespace SteamRoll.Services.Packaging;

/// <summary>
/// Verifies the integrity of game packages.
/// </summary>
public static class PackageVerifier
{
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
            LogService.Instance.Error($"Error verifying integrity: {ex.Message}", ex, "PackageVerifier");
            return (false, new List<string> { $"Verification error: {ex.Message}" });
        }

        return (mismatches.Count == 0, mismatches);
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

