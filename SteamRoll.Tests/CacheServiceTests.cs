using System.IO;
using SteamRoll.Models;
using SteamRoll.Services;
using System.Collections.Concurrent;

namespace SteamRoll.Tests;

/// <summary>
/// Tests for the CacheService class.
/// </summary>
public class CacheServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _originalAppDataPath;

    public CacheServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"CacheServiceTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        
        // Store original path
        _originalAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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
    public void GetCachedGame_ReturnsNull_WhenNotCached()
    {
        // Arrange
        var service = new CacheService();

        // Act
        var result = service.GetCachedGame(999999); // Non-existent AppId

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void UpdateCache_ThenGetCachedGame_ReturnsData()
    {
        // Arrange
        var service = new CacheService();
        var game = new InstalledGame
        {
            AppId = 12345,
            Name = "Test Game",
            SizeOnDisk = 1024 * 1024 * 100, // 100MB
            BuildId = 999,
            FullPath = @"C:\Games\TestGame"
        };

        // Act
        service.UpdateCache(game);
        var result = service.GetCachedGame(12345);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(12345, result.AppId);
        Assert.Equal("Test Game", result.Name);
        Assert.Equal(999, result.BuildId);
    }

    [Fact]
    public void GetStats_ReturnsCorrectCount()
    {
        // Arrange
        var service = new CacheService();
        var game1 = new InstalledGame { AppId = 1, Name = "Game 1", FullPath = @"C:\G1" };
        var game2 = new InstalledGame { AppId = 2, Name = "Game 2", FullPath = @"C:\G2" };

        // Act
        service.UpdateCache(game1);
        service.UpdateCache(game2);
        var (count, _) = service.GetStats();

        // Assert
        Assert.True(count >= 2); // May have other cached games from previous tests
    }

    [Fact]
    public void SetFileHashes_ThenGetFileHashes_ReturnsData()
    {
        // Arrange
        var service = new CacheService();
        var packagePath = Path.Combine(_testDir, "TestPackage");
        Directory.CreateDirectory(packagePath);
        
        // Create steamroll.json to mark as valid package
        File.WriteAllText(Path.Combine(packagePath, "steamroll.json"), "{}");
        
        var hashes = new Dictionary<string, string>
        {
            { "file1.exe", "abc123" },
            { "file2.dll", "def456" }
        };

        // Act
        service.SetFileHashes(packagePath, hashes);
        var result = service.GetFileHashes(packagePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("abc123", result["file1.exe"]);
        Assert.Equal("def456", result["file2.dll"]);
    }

    [Fact]
    public void GetFileHashes_ReturnsNull_WhenPackageModified()
    {
        // Arrange
        var service = new CacheService();
        var packagePath = Path.Combine(_testDir, "ModifiedPackage");
        Directory.CreateDirectory(packagePath);
        
        var metadataPath = Path.Combine(packagePath, "steamroll.json");
        File.WriteAllText(metadataPath, "{}");
        
        var hashes = new Dictionary<string, string> { { "file.exe", "hash123" } };

        // Act - cache the hashes
        service.SetFileHashes(packagePath, hashes);
        
        // Wait a bit and modify the metadata file
        Thread.Sleep(100);
        File.SetLastWriteTime(metadataPath, DateTime.Now.AddSeconds(1));
        
        var result = service.GetFileHashes(packagePath);

        // Assert - should be null because package was modified
        Assert.Null(result);
    }

    [Fact]
    public void ClearCache_RemovesAllData()
    {
        // Arrange
        var service = new CacheService();
        var game = new InstalledGame { AppId = 99999, Name = "Clear Test", FullPath = @"C:\Clear" };
        service.UpdateCache(game);

        // Act
        service.ClearCache();
        var result = service.GetCachedGame(99999);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ConcurrentAccess_DoesNotThrow()
    {
        // Arrange
        var service = new CacheService();
        var exceptions = new ConcurrentBag<Exception>();

        // Act - simulate concurrent access
        Parallel.For(0, 100, i =>
        {
            try
            {
                var game = new InstalledGame
                {
                    AppId = 100000 + i,
                    Name = $"Concurrent Game {i}",
                    FullPath = $@"C:\Games\Game{i}"
                };
                service.UpdateCache(game);
                _ = service.GetCachedGame(100000 + i);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Assert - no exceptions should occur with ConcurrentDictionary
        Assert.Empty(exceptions);
    }
}
