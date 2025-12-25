using System.IO;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;

namespace SteamRoll.Utils;

/// <summary>
/// Provides efficient file hashing using memory-mapped files for large files.
/// Falls back to streaming for smaller files to avoid memory-mapping overhead.
/// </summary>
public static class MemoryMappedHasher
{
    /// <summary>
    /// Threshold in bytes above which memory-mapped hashing is used.
    /// Files smaller than this use streaming to avoid mmap overhead.
    /// </summary>
    public const long MemoryMappedThreshold = 100 * 1024 * 1024; // 100 MB

    /// <summary>
    /// Chunk size for reading from memory-mapped files.
    /// Balances memory usage with performance.
    /// </summary>
    private const int ChunkSize = 16 * 1024 * 1024; // 16 MB chunks

    /// <summary>
    /// Computes XxHash64 for a file, using memory-mapped files for large files.
    /// </summary>
    /// <param name="filePath">Path to the file to hash.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Lowercase hex string of the hash.</returns>
    public static async Task<string> ComputeXxHash64Async(string filePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var fileInfo = new FileInfo(filePath);
        
        if (!fileInfo.Exists)
            throw new FileNotFoundException("File not found", filePath);

        if (fileInfo.Length == 0)
        {
            // Empty file - return hash of empty data
            var emptyHasher = new XxHash64();
            return Convert.ToHexString(emptyHasher.GetCurrentHash()).ToLowerInvariant();
        }

        // Use streaming for small files (less overhead than memory mapping)
        if (fileInfo.Length < MemoryMappedThreshold)
        {
            return await ComputeXxHash64StreamingAsync(filePath, ct);
        }

        // Use memory-mapped file for large files
        return await Task.Run(() => ComputeXxHash64MemoryMapped(filePath, fileInfo.Length, ct), ct);
    }

    /// <summary>
    /// Computes SHA256 for a file, using memory-mapped files for large files.
    /// </summary>
    /// <param name="filePath">Path to the file to hash.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Lowercase hex string of the hash.</returns>
    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        
        if (!fileInfo.Exists)
            throw new FileNotFoundException("File not found", filePath);

        if (fileInfo.Length == 0)
        {
            // Empty file - return hash of empty data
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(Array.Empty<byte>())).ToLowerInvariant();
        }

        // Use streaming for small files
        if (fileInfo.Length < MemoryMappedThreshold)
        {
            return await ComputeSha256StreamingAsync(filePath, ct);
        }

        // Use memory-mapped file for large files
        return await Task.Run(() => ComputeSha256MemoryMapped(filePath, fileInfo.Length, ct), ct);
    }

    /// <summary>
    /// Streaming XxHash64 for smaller files.
    /// </summary>
    private static async Task<string> ComputeXxHash64StreamingAsync(string filePath, CancellationToken ct)
    {
        var hasher = new XxHash64();
        var buffer = new byte[81920]; // 80KB buffer

        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 81920, useAsync: true);

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hasher.Append(buffer.AsSpan(0, bytesRead));
        }

        return Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
    }

    /// <summary>
    /// Streaming SHA256 for smaller files.
    /// </summary>
    private static async Task<string> ComputeSha256StreamingAsync(string filePath, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 81920, useAsync: true);

        var hashBytes = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Memory-mapped XxHash64 for large files.
    /// Processes file in chunks to avoid loading entire file into memory.
    /// </summary>
    private static string ComputeXxHash64MemoryMapped(string filePath, long fileLength, CancellationToken ct)
    {
        var hasher = new XxHash64();

        using var mmf = MemoryMappedFile.CreateFromFile(
            filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);

        long offset = 0;
        while (offset < fileLength)
        {
            ct.ThrowIfCancellationRequested();

            var chunkLength = (int)Math.Min(ChunkSize, fileLength - offset);

            using var accessor = mmf.CreateViewAccessor(offset, chunkLength, MemoryMappedFileAccess.Read);
            
            // Read chunk into buffer (we need a contiguous byte array for the hasher)
            var buffer = new byte[chunkLength];
            accessor.ReadArray(0, buffer, 0, chunkLength);
            
            hasher.Append(buffer);
            offset += chunkLength;
        }

        return Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
    }

    /// <summary>
    /// Memory-mapped SHA256 for large files.
    /// Uses IncrementalHash to avoid loading entire file.
    /// </summary>
    private static string ComputeSha256MemoryMapped(string filePath, long fileLength, CancellationToken ct)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        using var mmf = MemoryMappedFile.CreateFromFile(
            filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);

        long offset = 0;
        while (offset < fileLength)
        {
            ct.ThrowIfCancellationRequested();

            var chunkLength = (int)Math.Min(ChunkSize, fileLength - offset);

            using var accessor = mmf.CreateViewAccessor(offset, chunkLength, MemoryMappedFileAccess.Read);
            
            var buffer = new byte[chunkLength];
            accessor.ReadArray(0, buffer, 0, chunkLength);
            
            sha256.AppendData(buffer);
            offset += chunkLength;
        }

        return Convert.ToHexString(sha256.GetCurrentHash()).ToLowerInvariant();
    }

    /// <summary>
    /// Computes XxHash64 synchronously using the optimal method based on file size.
    /// Prefer async version when possible.
    /// </summary>
    public static string ComputeXxHash64(string filePath, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        
        if (!fileInfo.Exists)
            throw new FileNotFoundException("File not found", filePath);

        if (fileInfo.Length == 0)
        {
            var emptyHasher = new XxHash64();
            return Convert.ToHexString(emptyHasher.GetCurrentHash()).ToLowerInvariant();
        }

        if (fileInfo.Length < MemoryMappedThreshold)
        {
            // Streaming for small files
            var hasher = new XxHash64();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920);
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.Append(buffer.AsSpan(0, bytesRead));
            }
            return Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
        }

        return ComputeXxHash64MemoryMapped(filePath, fileInfo.Length, ct);
    }
}
