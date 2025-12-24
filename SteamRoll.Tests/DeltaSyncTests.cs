using System.IO;
using SteamRoll.Services.DeltaSync;

namespace SteamRoll.Tests;

/// <summary>
/// Tests for the delta sync system (rsync-style block-level transfers).
/// </summary>
public class DeltaSyncTests : IDisposable
{
    private readonly string _testDir;

    public DeltaSyncTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"DeltaSyncTests_{Guid.NewGuid():N}");
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
    public void RollingHash_ComputeHash_ProducesConsistentHash()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        
        var hash1 = RollingHash.ComputeHash(data, 0, data.Length);
        var hash2 = RollingHash.ComputeHash(data, 0, data.Length);
        
        Assert.Equal(hash1, hash2);
        Assert.NotEqual(0u, hash1);
    }

    [Fact]
    public void RollingHash_DifferentData_ProducesDifferentHash()
    {
        var data1 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var data2 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 9 }; // Last byte different
        
        var hash1 = RollingHash.ComputeHash(data1, 0, data1.Length);
        var hash2 = RollingHash.ComputeHash(data2, 0, data2.Length);
        
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DeltaCalculator_GenerateSignatures_CreatesBlocks()
    {
        // Create a test file (200KB = ~3 blocks at 64KB)
        var testFile = Path.Combine(_testDir, "test.bin");
        var data = new byte[200 * 1024];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(testFile, data);

        var calculator = new DeltaCalculator();
        var signatures = calculator.GenerateSignatures(testFile);

        Assert.Equal(4, signatures.Count); // 200KB / 64KB = 3.125 -> 4 blocks
        Assert.All(signatures, s => Assert.NotEqual(0u, s.WeakHash));
        Assert.All(signatures, s => Assert.NotEmpty(s.StrongHash));
    }

    [Fact]
    public void DeltaCalculator_IdenticalFiles_NoLiteralData()
    {
        // Create identical source and target files
        var sourceFile = Path.Combine(_testDir, "source.bin");
        var targetFile = Path.Combine(_testDir, "target.bin");
        
        var data = new byte[128 * 1024]; // 128KB = 2 blocks
        new Random(42).NextBytes(data);
        File.WriteAllBytes(sourceFile, data);
        File.WriteAllBytes(targetFile, data);

        var calculator = new DeltaCalculator();
        var targetSigs = calculator.GenerateSignatures(targetFile);
        var (instructions, literalData, summary) = calculator.CalculateDelta(sourceFile, targetSigs);

        // All blocks should match, no literal data needed
        Assert.Equal(2, summary.MatchedBlocks);
        Assert.Equal(0, summary.ChangedBlocks);
        Assert.True(summary.SavingsPercent > 90); // Should save nearly all data
    }

    [Fact]
    public void DeltaCalculator_ModifiedFile_TransfersOnlyChanges()
    {
        // Create source file
        var sourceFile = Path.Combine(_testDir, "source.bin");
        var targetFile = Path.Combine(_testDir, "target.bin");
        
        var data = new byte[256 * 1024]; // 256KB = 4 blocks
        new Random(42).NextBytes(data);
        File.WriteAllBytes(targetFile, data);
        
        // Modify just one block in source (last 64KB)
        var sourceData = (byte[])data.Clone();
        new Random(999).NextBytes(sourceData.AsSpan(192 * 1024, 64 * 1024));
        File.WriteAllBytes(sourceFile, sourceData);

        var calculator = new DeltaCalculator();
        var targetSigs = calculator.GenerateSignatures(targetFile);
        var (instructions, literalData, summary) = calculator.CalculateDelta(sourceFile, targetSigs);

        // 3 of 4 blocks should match
        Assert.Equal(3, summary.MatchedBlocks);
        Assert.True(summary.SavingsPercent > 50); // Should save at least half
    }

    [Fact]
    public void DeltaCalculator_ApplyDelta_ReconstructsFile()
    {
        // Create original and modified versions
        var originalFile = Path.Combine(_testDir, "original.bin");
        var modifiedFile = Path.Combine(_testDir, "modified.bin");
        var outputFile = Path.Combine(_testDir, "output.bin");
        
        var originalData = new byte[128 * 1024];
        new Random(42).NextBytes(originalData);
        File.WriteAllBytes(originalFile, originalData);
        
        // Modify just the first half
        var modifiedData = (byte[])originalData.Clone();
        new Random(999).NextBytes(modifiedData.AsSpan(0, 64 * 1024));
        File.WriteAllBytes(modifiedFile, modifiedData);

        var calculator = new DeltaCalculator();
        
        // Generate signatures from original
        var originalSigs = calculator.GenerateSignatures(originalFile);
        
        // Calculate delta from modified to original
        var (instructions, literalData, summary) = calculator.CalculateDelta(modifiedFile, originalSigs);
        
        // Apply delta to reconstruct modified file
        using var literalStream = new MemoryStream(literalData);
        calculator.ApplyDelta(originalFile, outputFile, instructions, literalStream);

        // Verify output matches modified file
        var outputData = File.ReadAllBytes(outputFile);
        Assert.Equal(modifiedData, outputData);
    }

    [Fact]
    public void DeltaService_ShouldUseDelta_ReturnsFalseForSmallFiles()
    {
        var smallFile = Path.Combine(_testDir, "small.bin");
        var existingFile = Path.Combine(_testDir, "existing.bin");
        
        // Create small files (under 256KB threshold)
        File.WriteAllBytes(smallFile, new byte[100 * 1024]);
        File.WriteAllBytes(existingFile, new byte[100 * 1024]);

        var service = new DeltaService();
        Assert.False(service.ShouldUseDelta(smallFile, existingFile));
    }

    [Fact]
    public void DeltaService_ShouldUseDelta_ReturnsFalseForMissingTarget()
    {
        var sourceFile = Path.Combine(_testDir, "source.bin");
        File.WriteAllBytes(sourceFile, new byte[300 * 1024]);

        var service = new DeltaService();
        Assert.False(service.ShouldUseDelta(sourceFile, null));
        Assert.False(service.ShouldUseDelta(sourceFile, Path.Combine(_testDir, "nonexistent.bin")));
    }

    [Fact]
    public void DeltaService_ShouldUseDelta_ReturnsTrueForLargeMatchingFiles()
    {
        var sourceFile = Path.Combine(_testDir, "source.bin");
        var existingFile = Path.Combine(_testDir, "existing.bin");
        
        // Create large files (over 256KB threshold)
        var data = new byte[500 * 1024];
        File.WriteAllBytes(sourceFile, data);
        File.WriteAllBytes(existingFile, data);

        var service = new DeltaService();
        Assert.True(service.ShouldUseDelta(sourceFile, existingFile));
    }

    [Fact]
    public void DeltaService_SerializeDeserialize_Roundtrips()
    {
        var instructions = new List<DeltaInstruction>
        {
            new() { Type = DeltaInstructionType.CopyFromTarget, TargetBlockIndex = 0, Offset = 0, Length = 65536 },
            new() { Type = DeltaInstructionType.LiteralData, Offset = 0, Length = 1024 },
            new() { Type = DeltaInstructionType.CopyFromTarget, TargetBlockIndex = 1, Offset = 65536, Length = 65536 }
        };

        var serialized = DeltaService.SerializeInstructions(instructions);
        var deserialized = DeltaService.DeserializeInstructions(serialized);

        Assert.Equal(instructions.Count, deserialized.Count);
        for (int i = 0; i < instructions.Count; i++)
        {
            Assert.Equal(instructions[i].Type, deserialized[i].Type);
            Assert.Equal(instructions[i].TargetBlockIndex, deserialized[i].TargetBlockIndex);
            Assert.Equal(instructions[i].Offset, deserialized[i].Offset);
            Assert.Equal(instructions[i].Length, deserialized[i].Length);
        }
    }
}
