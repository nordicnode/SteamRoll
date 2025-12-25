using System.IO;
using SteamRoll.Utils;

namespace SteamRoll.Tests;

/// <summary>
/// Tests for the MemoryMappedHasher utility class.
/// </summary>
public class MemoryMappedHasherTests : IDisposable
{
    private readonly string _testDir;

    public MemoryMappedHasherTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"MemoryMappedHasherTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    [Fact]
    public async Task ComputeXxHash64Async_EmptyFile_ReturnsValidHash()
    {
        // Arrange
        var emptyFile = Path.Combine(_testDir, "empty.bin");
        File.WriteAllBytes(emptyFile, Array.Empty<byte>());

        // Act
        var hash = await MemoryMappedHasher.ComputeXxHash64Async(emptyFile);

        // Assert
        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
        Assert.Matches("^[a-f0-9]+$", hash); // Lowercase hex string
    }

    [Fact]
    public async Task ComputeSha256Async_EmptyFile_ReturnsValidHash()
    {
        // Arrange
        var emptyFile = Path.Combine(_testDir, "empty.bin");
        File.WriteAllBytes(emptyFile, Array.Empty<byte>());

        // Act
        var hash = await MemoryMappedHasher.ComputeSha256Async(emptyFile);

        // Assert
        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length); // SHA256 produces 64 hex characters
        Assert.Matches("^[a-f0-9]+$", hash);
    }

    [Fact]
    public async Task ComputeXxHash64Async_SmallFile_UsesStreaming()
    {
        // Arrange - file under 100MB threshold uses streaming
        var smallFile = Path.Combine(_testDir, "small.bin");
        var data = new byte[1024 * 1024]; // 1MB
        new Random(42).NextBytes(data);
        File.WriteAllBytes(smallFile, data);

        // Act
        var hash1 = await MemoryMappedHasher.ComputeXxHash64Async(smallFile);
        var hash2 = await MemoryMappedHasher.ComputeXxHash64Async(smallFile);

        // Assert - same content produces same hash
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task ComputeXxHash64Async_DifferentContent_ProducesDifferentHash()
    {
        // Arrange
        var file1 = Path.Combine(_testDir, "file1.bin");
        var file2 = Path.Combine(_testDir, "file2.bin");

        var data1 = new byte[1000];
        var data2 = new byte[1000];
        new Random(1).NextBytes(data1);
        new Random(2).NextBytes(data2);

        File.WriteAllBytes(file1, data1);
        File.WriteAllBytes(file2, data2);

        // Act
        var hash1 = await MemoryMappedHasher.ComputeXxHash64Async(file1);
        var hash2 = await MemoryMappedHasher.ComputeXxHash64Async(file2);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public async Task ComputeXxHash64Async_FileNotFound_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonExistentFile = Path.Combine(_testDir, "nonexistent.bin");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            MemoryMappedHasher.ComputeXxHash64Async(nonExistentFile));
    }

    [Fact]
    public async Task ComputeSha256Async_FileNotFound_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonExistentFile = Path.Combine(_testDir, "nonexistent.bin");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            MemoryMappedHasher.ComputeSha256Async(nonExistentFile));
    }

    [Fact]
    public async Task ComputeXxHash64Async_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        var file = Path.Combine(_testDir, "cancellation_test.bin");
        var data = new byte[10 * 1024 * 1024]; // 10MB
        new Random(42).NextBytes(data);
        File.WriteAllBytes(file, data);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            MemoryMappedHasher.ComputeXxHash64Async(file, cts.Token));
    }

    [Fact]
    public void ComputeXxHash64_Sync_ProducesSameHashAsAsync()
    {
        // Arrange
        var file = Path.Combine(_testDir, "sync_test.bin");
        var data = new byte[1024 * 1024]; // 1MB
        new Random(42).NextBytes(data);
        File.WriteAllBytes(file, data);

        // Act
        var syncHash = MemoryMappedHasher.ComputeXxHash64(file);
        var asyncHash = MemoryMappedHasher.ComputeXxHash64Async(file).GetAwaiter().GetResult();

        // Assert
        Assert.Equal(syncHash, asyncHash);
    }

    [Fact]
    public async Task ComputeSha256Async_MatchesStandardImplementation()
    {
        // Arrange
        var file = Path.Combine(_testDir, "sha256_verify.bin");
        var data = new byte[1024];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(file, data);

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var expectedHash = Convert.ToHexString(sha256.ComputeHash(data)).ToLowerInvariant();

        // Act
        var actualHash = await MemoryMappedHasher.ComputeSha256Async(file);

        // Assert
        Assert.Equal(expectedHash, actualHash);
    }

    [Fact]
    public async Task ComputeXxHash64Async_LargeishFile_Completes()
    {
        // Arrange - Test with 50MB file (under memory-mapped threshold but larger test)
        var largeFile = Path.Combine(_testDir, "large.bin");
        var data = new byte[50 * 1024 * 1024]; // 50MB
        new Random(42).NextBytes(data);
        File.WriteAllBytes(largeFile, data);

        // Act
        var hash = await MemoryMappedHasher.ComputeXxHash64Async(largeFile);

        // Assert
        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
    }
}
