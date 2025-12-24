using System.IO;
using System.Text.Json;

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
    /// Serializes delta instructions for transfer.
    /// </summary>
    public static byte[] SerializeInstructions(List<DeltaInstruction> instructions)
    {
        var json = JsonSerializer.Serialize(instructions);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// Deserializes delta instructions from transfer data.
    /// </summary>
    public static List<DeltaInstruction> DeserializeInstructions(byte[] data)
    {
        var json = System.Text.Encoding.UTF8.GetString(data);
        return JsonSerializer.Deserialize<List<DeltaInstruction>>(json) ?? new List<DeltaInstruction>();
    }

    /// <summary>
    /// Serializes block signatures for transfer.
    /// </summary>
    public static byte[] SerializeSignatures(List<BlockSignature> signatures)
    {
        var json = JsonSerializer.Serialize(signatures);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// Deserializes block signatures from transfer data.
    /// </summary>
    public static List<BlockSignature> DeserializeSignatures(byte[] data)
    {
        var json = System.Text.Encoding.UTF8.GetString(data);
        return JsonSerializer.Deserialize<List<BlockSignature>>(json) ?? new List<BlockSignature>();
    }
}
