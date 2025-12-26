using System.IO;
using SteamRoll.Services.Transfer;

namespace SteamRoll.Tests;

/// <summary>
/// Tests for the TransferUtils class.
/// </summary>
public class TransferUtilsTests
{
    [Fact]
    public async Task SendJsonAsync_WritesLengthPrefixedData()
    {
        // Arrange
        using var memoryStream = new MemoryStream();
        var testObject = new TestMessage { Name = "Test", Value = 42 };

        // Act
        await TransferUtils.SendJsonAsync(memoryStream, testObject, CancellationToken.None);

        // Assert
        memoryStream.Position = 0;
        
        // Read length prefix (4 bytes)
        var lengthBytes = new byte[4];
        await memoryStream.ReadAsync(lengthBytes);
        var length = BitConverter.ToInt32(lengthBytes, 0);
        
        Assert.True(length > 0);
        Assert.True(length < memoryStream.Length); // Length should be less than total stream
    }

    [Fact]
    public async Task ReceiveJsonAsync_ReadsLengthPrefixedData()
    {
        // Arrange
        using var memoryStream = new MemoryStream();
        var testObject = new TestMessage { Name = "Hello", Value = 123 };
        
        // Write using SendJsonAsync
        await TransferUtils.SendJsonAsync(memoryStream, testObject, CancellationToken.None);
        memoryStream.Position = 0;

        // Act
        var result = await TransferUtils.ReceiveJsonAsync<TestMessage>(memoryStream, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Hello", result.Name);
        Assert.Equal(123, result.Value);
    }

    [Fact]
    public async Task ReceiveJsonAsync_ReturnsDefault_ForInvalidLength()
    {
        // Arrange
        using var memoryStream = new MemoryStream();
        
        // Write an invalid (negative) length
        var invalidLength = BitConverter.GetBytes(-1);
        await memoryStream.WriteAsync(invalidLength);
        memoryStream.Position = 0;

        // Act
        var result = await TransferUtils.ReceiveJsonAsync<TestMessage>(memoryStream, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ReceiveJsonAsync_ReturnsDefault_ForExcessiveLength()
    {
        // Arrange
        using var memoryStream = new MemoryStream();
        
        // Write an excessively large length (> 128MB limit)
        var excessiveLength = BitConverter.GetBytes(200_000_000);
        await memoryStream.WriteAsync(excessiveLength);
        memoryStream.Position = 0;

        // Act
        var result = await TransferUtils.ReceiveJsonAsync<TestMessage>(memoryStream, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task RoundTrip_ComplexObject_PreservesData()
    {
        // Arrange
        using var memoryStream = new MemoryStream();
        var complexObject = new ComplexMessage
        {
            Id = Guid.NewGuid().ToString(),
            Items = new List<string> { "item1", "item2", "item3" },
            Metadata = new Dictionary<string, int>
            {
                { "count", 5 },
                { "version", 2 }
            },
            Timestamp = DateTime.UtcNow
        };

        // Act
        await TransferUtils.SendJsonAsync(memoryStream, complexObject, CancellationToken.None);
        memoryStream.Position = 0;
        var result = await TransferUtils.ReceiveJsonAsync<ComplexMessage>(memoryStream, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(complexObject.Id, result.Id);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(5, result.Metadata["count"]);
    }

    [Fact]
    public async Task SendJsonAsync_CancellationToken_ThrowsWhenCancelled()
    {
        // Arrange
        using var memoryStream = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert - TaskCanceledException derives from OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransferUtils.SendJsonAsync(memoryStream, new TestMessage(), cts.Token));
    }

    // Test helper classes
    private class TestMessage
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }

    private class ComplexMessage
    {
        public string Id { get; set; } = "";
        public List<string> Items { get; set; } = new();
        public Dictionary<string, int> Metadata { get; set; } = new();
        public DateTime Timestamp { get; set; }
    }
}
