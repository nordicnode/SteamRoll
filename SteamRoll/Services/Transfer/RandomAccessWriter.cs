using System.IO;
using System.IO.MemoryMappedFiles;

namespace SteamRoll.Services.Transfer;

/// <summary>
/// Thread-safe wrapper around MemoryMappedFile for writing file chunks out of order.
/// Used by SwarmManager to write blocks received from different peers simultaneously.
/// </summary>
/// <remarks>
/// Architecture Note: Using MemoryMappedFile provides:
/// - True random access without seeking overhead
/// - Automatic OS-level buffering and write-behind
/// - Thread-safe writes to different regions
/// - Efficient handling of large files (50GB+)
/// </remarks>
public class RandomAccessWriter : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly long _fileSize;
    private readonly string _filePath;
    private readonly object _writeLock = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new random access writer for the specified file.
    /// </summary>
    /// <param name="filePath">Path to the file to write.</param>
    /// <param name="fileSize">Total expected file size in bytes.</param>
    public RandomAccessWriter(string filePath, long fileSize)
    {
        _filePath = filePath;
        _fileSize = fileSize;

        // Ensure directory exists
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Create the file with the expected size first
        // This pre-allocates disk space, preventing fragmentation
        using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(fileSize);
        }

        // Create memory-mapped file from the pre-sized file
        _mmf = MemoryMappedFile.CreateFromFile(
            filePath,
            FileMode.Open,
            mapName: null,
            capacity: fileSize,
            access: MemoryMappedFileAccess.ReadWrite);

        _accessor = _mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Write);
    }

    /// <summary>
    /// Writes data at the specified offset. Thread-safe for concurrent calls.
    /// </summary>
    /// <param name="offset">Byte offset within the file.</param>
    /// <param name="data">Data to write.</param>
    /// <exception cref="ArgumentOutOfRangeException">If offset + length exceeds file size.</exception>
    /// <exception cref="ObjectDisposedException">If writer has been disposed.</exception>
    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
        
        if (offset + data.Length > _fileSize)
            throw new ArgumentOutOfRangeException(nameof(offset), 
                $"Write would exceed file size. Offset: {offset}, Length: {data.Length}, FileSize: {_fileSize}");

        // MemoryMappedViewAccessor is thread-safe for non-overlapping regions
        // We use a lock here for safety, though writes to different regions 
        // could technically be done in parallel
        lock (_writeLock)
        {
            _accessor.WriteArray(offset, data.ToArray(), 0, data.Length);
        }
    }

    /// <summary>
    /// Writes data at the specified offset. Thread-safe for concurrent calls.
    /// </summary>
    public void Write(long offset, byte[] data)
    {
        Write(offset, data.AsSpan());
    }

    /// <summary>
    /// Writes data at the specified offset. Thread-safe for concurrent calls.
    /// </summary>
    public void Write(long offset, byte[] data, int dataOffset, int count)
    {
        Write(offset, data.AsSpan(dataOffset, count));
    }

    /// <summary>
    /// Flushes any buffered writes to disk.
    /// </summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _accessor.Flush();
    }

    /// <summary>
    /// Verifies the final file size matches expected.
    /// Call after all writes are complete.
    /// </summary>
    /// <returns>True if file size matches expected.</returns>
    public bool VerifyFileSize()
    {
        Flush();
        var info = new FileInfo(_filePath);
        return info.Exists && info.Length == _fileSize;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _accessor.Flush();
            _accessor.Dispose();
        }
        catch { /* Ignore cleanup errors */ }

        try
        {
            _mmf.Dispose();
        }
        catch { /* Ignore cleanup errors */ }
    }
}
