namespace SteamRoll.Services.DeltaSync;

/// <summary>
/// Represents a block signature for delta comparison.
/// Contains both a weak hash (for fast comparison) and strong hash (for verification).
/// </summary>
public class BlockSignature
{
    /// <summary>
    /// File-relative offset where this block starts.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Length of this block in bytes.
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// Weak rolling hash (Adler-32 style) for fast comparison.
    /// Used to quickly identify potential matching blocks.
    /// </summary>
    public uint WeakHash { get; set; }

    /// <summary>
    /// Strong hash (XxHash64) for verification after weak hash match.
    /// Ensures the block content actually matches.
    /// </summary>
    public string StrongHash { get; set; } = string.Empty;

    /// <summary>
    /// Block index within the file (0-based).
    /// </summary>
    public int Index { get; set; }
}

/// <summary>
/// Delta instruction types for file reconstruction.
/// </summary>
public enum DeltaInstructionType : byte
{
    /// <summary>
    /// Copy a block from the existing target file.
    /// </summary>
    CopyFromTarget = 0,

    /// <summary>
    /// Copy literal data from the delta stream.
    /// </summary>
    LiteralData = 1
}

/// <summary>
/// A single delta instruction for file reconstruction.
/// </summary>
public class DeltaInstruction
{
    /// <summary>
    /// Type of instruction.
    /// </summary>
    public DeltaInstructionType Type { get; set; }

    /// <summary>
    /// For CopyFromTarget: the block index to copy.
    /// </summary>
    public int TargetBlockIndex { get; set; }

    /// <summary>
    /// For LiteralData: the offset in the delta stream.
    /// For CopyFromTarget: the offset in the target file.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Length of data to copy or read.
    /// </summary>
    public int Length { get; set; }
}

/// <summary>
/// Summary of delta calculation results.
/// </summary>
public class DeltaSummary
{
    /// <summary>
    /// Original file size.
    /// </summary>
    public long OriginalSize { get; set; }

    /// <summary>
    /// Number of blocks that matched (no transfer needed).
    /// </summary>
    public int MatchedBlocks { get; set; }

    /// <summary>
    /// Number of new/changed blocks (need transfer).
    /// </summary>
    public int ChangedBlocks { get; set; }

    /// <summary>
    /// Bytes that need to be transferred.
    /// </summary>
    public long BytesToTransfer { get; set; }

    /// <summary>
    /// Bytes saved by delta sync.
    /// </summary>
    public long BytesSaved => OriginalSize - BytesToTransfer;

    /// <summary>
    /// Percentage of data saved.
    /// </summary>
    public double SavingsPercent => OriginalSize > 0 
        ? (double)BytesSaved / OriginalSize * 100 
        : 0;
}
