using System.IO;
using System.Text.Json;
using SteamRoll.Models;

namespace SteamRoll.Services;

/// <summary>
/// Scans for and removes orphaned files (files not belonging to any valid SteamRoll package)
/// to reclaim disk space.
/// </summary>
public class LibraryCleanupService
{
    private readonly string _libraryPath;

    /// <summary>
    /// Represents a file or folder that is considered orphaned.
    /// </summary>
    public class OrphanedItem
    {
        public string Path { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
    }

    public LibraryCleanupService(string libraryPath)
    {
        _libraryPath = libraryPath;
    }

    /// <summary>
    /// Scans the library directory for files that are not referenced by any valid package manifest (steamroll.json).
    /// </summary>
    public async Task<List<OrphanedItem>> ScanForOrphansAsync(CancellationToken ct = default)
    {
        var orphans = new List<OrphanedItem>();

        if (!Directory.Exists(_libraryPath))
            return orphans;

        await Task.Run(async () =>
        {
            // 1. Identify all package folders (folders containing steamroll.json)
            var packageFolders = new List<string>();
            var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Scan top-level directories
            foreach (var dir in Directory.GetDirectories(_libraryPath))
            {
                ct.ThrowIfCancellationRequested();
                var metadataPath = Path.Combine(dir, "steamroll.json");

                if (File.Exists(metadataPath))
                {
                    packageFolders.Add(dir);

                    // Add manifest itself
                    knownFiles.Add(metadataPath);

                    // Add files listed in manifest
                    try
                    {
                        var json = await File.ReadAllTextAsync(metadataPath, ct);
                        var metadata = JsonSerializer.Deserialize<PackageMetadata>(json);

                        if (metadata?.FileHashes != null)
                        {
                            foreach (var relPath in metadata.FileHashes.Keys)
                            {
                                var fullPath = Path.Combine(dir, relPath);
                                // Normalize
                                knownFiles.Add(Path.GetFullPath(fullPath));
                            }
                        }

                        // Add known auxiliary files not usually in hash list
                        knownFiles.Add(Path.GetFullPath(Path.Combine(dir, "steam_appid.txt")));
                        knownFiles.Add(Path.GetFullPath(Path.Combine(dir, "steam_interfaces.txt")));
                        knownFiles.Add(Path.GetFullPath(Path.Combine(dir, "README.txt")));
                        knownFiles.Add(Path.GetFullPath(Path.Combine(dir, "LAUNCH.bat")));
                        knownFiles.Add(Path.GetFullPath(Path.Combine(dir, "launch.sh")));
                        knownFiles.Add(Path.GetFullPath(Path.Combine(dir, ".steamroll_received")));
                        knownFiles.Add(Path.GetFullPath(Path.Combine(dir, ".steamroll_transfer_state"))); // In case of active transfer

                        // Add steam_settings folder contents blindly (config files often change)
                        var settingsDir = Path.Combine(dir, "steam_settings");
                        if (Directory.Exists(settingsDir))
                        {
                            foreach (var f in Directory.GetFiles(settingsDir, "*", SearchOption.AllDirectories))
                            {
                                knownFiles.Add(Path.GetFullPath(f));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.Warning($"Error reading metadata for {Path.GetFileName(dir)}: {ex.Message}", "CleanupService");
                        // If metadata is corrupt, we probably shouldn't delete everything else in the folder automatically
                        // Consider the whole folder "known" to be safe?
                        // For safety, let's mark all files in this folder as known to avoid accidental deletion
                        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            knownFiles.Add(Path.GetFullPath(f));
                        }
                    }
                }
                else
                {
                    // Folder has no steamroll.json - is it a partial transfer?
                    if (File.Exists(Path.Combine(dir, TransferState.StateFileName)))
                    {
                        // Active transfer - keep everything
                        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            knownFiles.Add(Path.GetFullPath(f));
                        }
                    }
                    else
                    {
                        // Folder is completely unknown (not a package)
                        // Add the folder itself as an orphan?
                        // Or recurse?
                        // Let's treat the whole folder as an orphan
                        orphans.Add(new OrphanedItem
                        {
                            Path = dir,
                            RelativePath = Path.GetFileName(dir),
                            IsDirectory = true,
                            Size = GetDirectorySize(dir)
                        });
                    }
                }
            }

            // 2. Scan for unknown files INSIDE valid package folders
            foreach (var dir in packageFolders)
            {
                ct.ThrowIfCancellationRequested();

                var allFiles = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                foreach (var file in allFiles)
                {
                    var fullPath = Path.GetFullPath(file);

                    // Allow save files
                    if (fullPath.Contains("SteamRoll_Saves") || fullPath.Contains("Goldberg SteamEmu Saves"))
                        continue;

                    if (!knownFiles.Contains(fullPath))
                    {
                        orphans.Add(new OrphanedItem
                        {
                            Path = fullPath,
                            RelativePath = Path.GetRelativePath(_libraryPath, fullPath),
                            IsDirectory = false,
                            Size = new FileInfo(fullPath).Length
                        });
                    }
                }
            }
        });

        return orphans;
    }

    /// <summary>
    /// Deletes the specified orphaned items.
    /// </summary>
    public async Task CleanupAsync(List<OrphanedItem> items)
    {
        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                try
                {
                    if (item.IsDirectory)
                    {
                        if (Directory.Exists(item.Path))
                            Directory.Delete(item.Path, true);
                    }
                    else
                    {
                        if (File.Exists(item.Path))
                            File.Delete(item.Path);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning($"Failed to delete {item.Path}: {ex.Message}", "CleanupService");
                }
            }
        });
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories).Sum(t => new FileInfo(t).Length);
        }
        catch { return 0; }
    }
}
