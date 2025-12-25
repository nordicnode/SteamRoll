using System.IO;

namespace SteamRoll.Services.DeltaSync;

/// <summary>
/// Service for managing delta sync operations during file transfers.
/// Coordinates signature generation, delta calculation, and file reconstruction.
/// </summary>
public class DeltaService
{
    private readonly DeltaCalculator _calculator;

    /// <summary>
    /// Event raised when delta calculation results are available.
    /// </summary>
    public event EventHandler<DeltaSummary>? DeltaCalculated;

    public DeltaService(int blockSize = DeltaCalculator.DEFAULT_BLOCK_SIZE)
    {
        _calculator = new DeltaCalculator(blockSize);
    }

    /// <summary>
    /// Determines if a file is suitable for delta sync.
    /// Small files or files without an existing version should be sent whole.
    /// </summary>
    public bool ShouldUseDelta(string sourcePath, string? existingTargetPath)
    {
        // No delta if no existing file
        if (string.IsNullOrEmpty(existingTargetPath) || !File.Exists(existingTargetPath))
            return false;

        // No delta for small files (overhead not worth it)
        var sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Length < DeltaCalculator.MIN_DELTA_SIZE)
            return false;

        // No delta if existing file is too different in size (likely different file)
        var targetInfo = new FileInfo(existingTargetPath);
        var sizeRatio = (double)Math.Min(sourceInfo.Length, targetInfo.Length) / 
                        Math.Max(sourceInfo.Length, targetInfo.Length);
        if (sizeRatio < 0.5)
            return false; // Files differ by more than 50% - likely not worth delta

        return true;
    }

    /// <summary>
    /// Generates block signatures for an existing file.
    /// The receiver calls this to prepare for delta transfer.
    /// </summary>
    public List<BlockSignature> GenerateSignatures(string filePath)
    {
        if (!File.Exists(filePath))
            return new List<BlockSignature>();

        try
        {
            return _calculator.GenerateSignatures(filePath);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to generate signatures for {filePath}: {ex.Message}", "DeltaService");
            return new List<BlockSignature>();
        }
    }

    /// <summary>
    /// Calculates delta between source file and target signatures.
    /// Returns the delta instructions, literal data, and summary.
    /// </summary>
    public (List<DeltaInstruction> Instructions, byte[] LiteralData, DeltaSummary Summary)?
        CalculateDelta(string sourcePath, List<BlockSignature> targetSignatures)
    {
        if (targetSignatures.Count == 0)
            return null;

        try
        {
            var result = _calculator.CalculateDelta(sourcePath, targetSignatures);
            
            DeltaCalculated?.Invoke(this, result.Summary);
            
            LogService.Instance.Debug(
                $"Delta calculated: {result.Summary.MatchedBlocks} matched, {result.Summary.ChangedBlocks} changed, " +
                $"{result.Summary.SavingsPercent:F1}% savings", "DeltaService");

            return result;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to calculate delta for {sourcePath}: {ex.Message}", "DeltaService");
            return null;
        }
    }

    /// <summary>
    /// Reconstructs a file from delta instructions.
    /// </summary>
    public bool ApplyDelta(
        string existingFilePath,
        string outputPath,
        List<DeltaInstruction> instructions,
        byte[] literalData)
    {
        try
        {
            using var literalStream = new MemoryStream(literalData);
            _calculator.ApplyDelta(existingFilePath, outputPath, instructions, literalStream);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to apply delta to {outputPath}: {ex.Message}", ex, "DeltaService");
            return false;
        }
    }

    /// <summary>
    /// Serializes delta instructions for transfer using compact binary format.
    /// Format: [count:int32][instruction1][instruction2]...
    /// Each instruction: [type:byte][targetBlockIndex:int32][offset:int64][length:int32]
    /// </summary>
    public static byte[] SerializeInstructions(List<DeltaInstruction> instructions)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        writer.Write(instructions.Count);
        foreach (var instr in instructions)
        {
            writer.Write((byte)instr.Type);
            writer.Write(instr.TargetBlockIndex);
            writer.Write(instr.Offset);
            writer.Write(instr.Length);
        }
        
        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes delta instructions from binary transfer data.
    /// </summary>
    public static List<DeltaInstruction> DeserializeInstructions(byte[] data)
    {
        var instructions = new List<DeltaInstruction>();
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        
        var count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            instructions.Add(new DeltaInstruction
            {
                Type = (DeltaInstructionType)reader.ReadByte(),
                TargetBlockIndex = reader.ReadInt32(),
                Offset = reader.ReadInt64(),
                Length = reader.ReadInt32()
            });
        }
        
        return instructions;
    }

    /// <summary>
    /// Serializes block signatures for transfer using compact binary format.
    /// Format: [count:int32][sig1][sig2]...
    /// Each signature: [offset:int64][length:int32][weakHash:uint32][strongHashLen:int32][strongHash:bytes][index:int32]
    /// </summary>
    public static byte[] SerializeSignatures(List<BlockSignature> signatures)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        writer.Write(signatures.Count);
        foreach (var sig in signatures)
        {
            writer.Write(sig.Offset);
            writer.Write(sig.Length);
            writer.Write(sig.WeakHash);
            // Write StrongHash as length-prefixed UTF8 bytes
            var hashBytes = System.Text.Encoding.UTF8.GetBytes(sig.StrongHash ?? string.Empty);
            writer.Write(hashBytes.Length);
            writer.Write(hashBytes);
            writer.Write(sig.Index);
        }
        
        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes block signatures from binary transfer data.
    /// </summary>
    public static List<BlockSignature> DeserializeSignatures(byte[] data)
    {
        var signatures = new List<BlockSignature>();
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        
        var count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var offset = reader.ReadInt64();
            var length = reader.ReadInt32();
            var weakHash = reader.ReadUInt32();
            var hashLen = reader.ReadInt32();
            var hashBytes = reader.ReadBytes(hashLen);
            var strongHash = System.Text.Encoding.UTF8.GetString(hashBytes);
            var index = reader.ReadInt32();
            
            signatures.Add(new BlockSignature
            {
                Offset = offset,
                Length = length,
                WeakHash = weakHash,
                StrongHash = strongHash,
                Index = index
            });
        }
        
        return signatures;
    }
}
