using SteamRoll.Services.Transfer;
using Xunit;

namespace SteamRoll.Tests;

/// <summary>
/// Unit tests for SwarmCoordinator block management.
/// </summary>
public class SwarmCoordinatorTests
{
    [Fact]
    public void CreateBlockJobs_CreatesCorrectNumberOfBlocks()
    {
        // Arrange
        var coordinator = new SwarmCoordinator();
        long fileSize = 50 * 1024 * 1024; // 50MB
        int expectedBlocks = (int)Math.Ceiling((double)fileSize / SwarmCoordinator.CHUNK_SIZE);

        // Act
        var blocks = coordinator.CreateBlockJobs(fileSize);

        // Assert
        Assert.Equal(expectedBlocks, blocks.Count);
        Assert.Equal(expectedBlocks, coordinator.TotalBlocks);
        Assert.Equal(0, coordinator.CompletedBlocks);
    }

    [Fact]
    public void CreateBlockJobs_LastBlockHasCorrectSize()
    {
        // Arrange
        var coordinator = new SwarmCoordinator();
        long fileSize = 5 * 1024 * 1024 + 12345; // 5MB + 12345 bytes

        // Act
        var blocks = coordinator.CreateBlockJobs(fileSize);
        var lastBlock = blocks.Last();

        // Assert
        Assert.Equal(2, blocks.Count); // One 4MB block + one partial block
        Assert.Equal(SwarmCoordinator.CHUNK_SIZE, blocks[0].Length); // First block is full size
        Assert.Equal(12345 + 1024 * 1024, lastBlock.Length); // Last block is remaining
    }

    [Fact]
    public void DequeueBlock_AssignsPeerIdAndTime()
    {
        // Arrange
        var coordinator = new SwarmCoordinator();
        coordinator.CreateBlockJobs(10 * 1024 * 1024);
        var peerId = Guid.NewGuid();

        // Act
        var block = coordinator.DequeueBlock(peerId);

        // Assert
        Assert.NotNull(block);
        Assert.Equal(peerId, block.AssignedPeerId);
        Assert.NotNull(block.AssignmentTime);
        Assert.Equal(1, coordinator.InFlightBlocks);
    }

    [Fact]
    public void MarkComplete_MovesBlockToCompleted()
    {
        // Arrange
        var coordinator = new SwarmCoordinator();
        coordinator.CreateBlockJobs(4 * 1024 * 1024); // Single block
        var peerId = Guid.NewGuid();
        var block = coordinator.DequeueBlock(peerId)!;

        // Act
        coordinator.MarkComplete(block.Index);

        // Assert
        Assert.Equal(1, coordinator.CompletedBlocks);
        Assert.Equal(0, coordinator.InFlightBlocks);
        Assert.True(coordinator.IsComplete);
    }

    [Fact]
    public void MarkFailed_RequeuesBlock()
    {
        // Arrange
        var coordinator = new SwarmCoordinator();
        coordinator.CreateBlockJobs(4 * 1024 * 1024); // Single block
        var peerId = Guid.NewGuid();
        var block = coordinator.DequeueBlock(peerId)!;

        // Act
        coordinator.MarkFailed(block.Index, "Test failure");

        // Assert
        Assert.Equal(0, coordinator.InFlightBlocks);
        Assert.Equal(1, coordinator.PendingBlocks);
        Assert.False(coordinator.IsComplete);
        
        // Should be able to dequeue again
        var retriedBlock = coordinator.DequeueBlock(Guid.NewGuid());
        Assert.NotNull(retriedBlock);
        Assert.Equal(1, retriedBlock.FailedAttempts);
    }

    [Fact]
    public void MarkFailed_DoesNotRequeueAfterMaxRetries()
    {
        // Arrange
        var coordinator = new SwarmCoordinator();
        coordinator.CreateBlockJobs(4 * 1024 * 1024); // Single block

        // Act - Fail MAX_RETRY_ATTEMPTS times
        for (int i = 0; i < SwarmCoordinator.MAX_RETRY_ATTEMPTS; i++)
        {
            var block = coordinator.DequeueBlock(Guid.NewGuid())!;
            coordinator.MarkFailed(block.Index, $"Failure {i + 1}");
        }

        // Assert - Block should be permanently removed
        Assert.Equal(0, coordinator.PendingBlocks);
        Assert.Equal(0, coordinator.InFlightBlocks);
    }

    [Fact]
    public void GetProgress_ReturnsAccurateStats()
    {
        // Arrange
        var coordinator = new SwarmCoordinator();
        coordinator.CreateBlockJobs(12 * 1024 * 1024); // 3 blocks
        
        // Complete one block
        var block1 = coordinator.DequeueBlock(Guid.NewGuid())!;
        coordinator.MarkComplete(block1.Index);
        
        // In-flight another
        coordinator.DequeueBlock(Guid.NewGuid());

        // Act
        var progress = coordinator.GetProgress("TestGame", "testfile.bin");

        // Assert
        Assert.Equal("TestGame", progress.GameName);
        Assert.Equal(3, progress.TotalBlocks);
        Assert.Equal(1, progress.CompletedBlocks);
        Assert.Equal(1, progress.InFlightBlocks);
        Assert.Equal(1, progress.PendingBlocks);
        Assert.True(progress.Percentage > 30 && progress.Percentage < 40); // ~33%
    }
}
