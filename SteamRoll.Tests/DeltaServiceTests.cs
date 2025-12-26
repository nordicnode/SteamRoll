using System.IO;
using SteamRoll.Services.DeltaSync;

namespace SteamRoll.Tests;

/// <summary>
/// Tests for the DeltaService class.
/// </summary>
public class DeltaServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly DeltaService _service;

    public DeltaServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"DeltaServiceTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _service = new DeltaService();
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

    #region ShouldUseDelta Tests

    [Fact]
    public void ShouldUseDelta_ReturnsFalse_WhenNoExistingFile()
    {
        // Arrange
        var sourcePath = CreateTestFile("source.dat", 1024 * 1024); // 1MB

        // Act
        var result = _service.ShouldUseDelta(sourcePath, null);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldUseDelta_ReturnsFalse_WhenFileIsTooSmall()
    {
        // Arrange
        var sourcePath = CreateTestFile("small_source.dat", 1024); // 1KB - below minimum
        var targetPath = CreateTestFile("small_target.dat", 1024);

        // Act
        var result = _service.ShouldUseDelta(sourcePath, targetPath);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldUseDelta_ReturnsFalse_WhenSizesDifferGreatly()
    {
        // Arrange
        var sourcePath = CreateTestFile("large_source.dat", 1024 * 1024); // 1MB
        var targetPath = CreateTestFile("tiny_target.dat", 100 * 1024);   // 100KB - more than 50% difference

        // Act
        var result = _service.ShouldUseDelta(sourcePath, targetPath);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldUseDelta_ReturnsTrue_WhenFilesAreSimilar()
    {
        // Arrange
        var sourcePath = CreateTestFile("similar_source.dat", 1024 * 1024); // 1MB
        var targetPath = CreateTestFile("similar_target.dat", 900 * 1024);  // 900KB - within 50% threshold

        // Act
        var result = _service.ShouldUseDelta(sourcePath, targetPath);

        // Assert
        Assert.True(result);
    }

    #endregion

    #region Signature Serialization Tests

    [Fact]
    public void SerializeSignatures_ThenDeserialize_PreservesData()
    {
        // Arrange
        var signatures = new List<BlockSignature>
        {
            new() { Offset = 0, Length = 4096, WeakHash = 12345, StrongHash = "abc123", Index = 0 },
            new() { Offset = 4096, Length = 4096, WeakHash = 67890, StrongHash = "def456", Index = 1 },
            new() { Offset = 8192, Length = 2048, WeakHash = 11111, StrongHash = "ghi789", Index = 2 }
        };

        // Act
        var serialized = DeltaService.SerializeSignatures(signatures);
        var deserialized = DeltaService.DeserializeSignatures(serialized);

        // Assert
        Assert.Equal(signatures.Count, deserialized.Count);
        for (int i = 0; i < signatures.Count; i++)
        {
            Assert.Equal(signatures[i].Offset, deserialized[i].Offset);
            Assert.Equal(signatures[i].Length, deserialized[i].Length);
            Assert.Equal(signatures[i].WeakHash, deserialized[i].WeakHash);
            Assert.Equal(signatures[i].StrongHash, deserialized[i].StrongHash);
            Assert.Equal(signatures[i].Index, deserialized[i].Index);
        }
    }

    [Fact]
    public void SerializeSignatures_EmptyList_ReturnsValidData()
    {
        // Arrange
        var signatures = new List<BlockSignature>();

        // Act
        var serialized = DeltaService.SerializeSignatures(signatures);
        var deserialized = DeltaService.DeserializeSignatures(serialized);

        // Assert
        Assert.Empty(deserialized);
    }

    #endregion

    #region Instruction Serialization Tests

    [Fact]
    public void SerializeInstructions_ThenDeserialize_PreservesData()
    {
        // Arrange
        var instructions = new List<DeltaInstruction>
        {
            new() { Type = DeltaInstructionType.CopyFromTarget, TargetBlockIndex = 0, Offset = 0, Length = 4096 },
            new() { Type = DeltaInstructionType.LiteralData, TargetBlockIndex = -1, Offset = 100, Length = 512 },
            new() { Type = DeltaInstructionType.CopyFromTarget, TargetBlockIndex = 2, Offset = 8192, Length = 4096 }
        };

        // Act
        var serialized = DeltaService.SerializeInstructions(instructions);
        var deserialized = DeltaService.DeserializeInstructions(serialized);

        // Assert
        Assert.Equal(instructions.Count, deserialized.Count);
        for (int i = 0; i < instructions.Count; i++)
        {
            Assert.Equal(instructions[i].Type, deserialized[i].Type);
            Assert.Equal(instructions[i].TargetBlockIndex, deserialized[i].TargetBlockIndex);
            Assert.Equal(instructions[i].Offset, deserialized[i].Offset);
            Assert.Equal(instructions[i].Length, deserialized[i].Length);
        }
    }

    [Fact]
    public void SerializeInstructions_EmptyList_ReturnsValidData()
    {
        // Arrange
        var instructions = new List<DeltaInstruction>();

        // Act
        var serialized = DeltaService.SerializeInstructions(instructions);
        var deserialized = DeltaService.DeserializeInstructions(serialized);

        // Assert
        Assert.Empty(deserialized);
    }

    #endregion

    #region GenerateSignatures Tests

    [Fact]
    public void GenerateSignatures_ReturnsEmpty_WhenFileDoesNotExist()
    {
        // Act
        var signatures = _service.GenerateSignatures(@"C:\NonExistent\file.dat");

        // Assert
        Assert.Empty(signatures);
    }

    [Fact]
    public void GenerateSignatures_ReturnsSignatures_ForValidFile()
    {
        // Arrange
        var filePath = CreateTestFile("sig_test.dat", 1024 * 1024); // 1MB

        // Act
        var signatures = _service.GenerateSignatures(filePath);

        // Assert
        Assert.NotEmpty(signatures);
        Assert.All(signatures, sig =>
        {
            Assert.True(sig.Length > 0);
            Assert.NotNull(sig.StrongHash);
        });
    }

    #endregion

    #region Helper Methods

    private string CreateTestFile(string name, int size)
    {
        var path = Path.Combine(_testDir, name);
        var data = new byte[size];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    #endregion
}
