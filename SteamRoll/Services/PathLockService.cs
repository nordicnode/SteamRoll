using System.Collections.Concurrent;

namespace SteamRoll.Services;

/// <summary>
/// Manages named locks for file system paths to prevent concurrent access issues
/// between different services (e.g., TransferService and PackageBuilder).
/// </summary>
public class PathLockService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Acquires an async lock for the specified path.
    /// The returned IDisposable will release the lock when disposed.
    /// </summary>
    /// <param name="path">The file system path to lock.</param>
    /// <param name="timeoutMs">Timeout in milliseconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A disposable handle that releases the lock, or null if acquisition failed.</returns>
    public async Task<IDisposable?> AcquireLockAsync(string path, int timeoutMs = 10000, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(path);
        var semaphore = _locks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));

        if (await semaphore.WaitAsync(timeoutMs, ct))
        {
            return new LockHandle(semaphore);
        }

        return null;
    }

    private class LockHandle : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public LockHandle(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _semaphore.Release();
                _disposed = true;
            }
        }
    }
}
