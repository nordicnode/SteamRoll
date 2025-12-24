using System.IO;
using System.IO.Hashing;

namespace SteamRoll.Services.DeltaSync;

/// <summary>
/// Core delta calculation engine implementing rsync-style block comparison.
/// Generates signatures for existing files and calculates minimal delta for transfers.
/// </summary>
public class DeltaCalculator
{
    /// <summary>
    /// Default block size for delta sync (64KB).
    /// Smaller = more granular but more overhead. Larger = less overhead but less granular.
    /// </summary>
    public const int DEFAULT_BLOCK_SIZE = 64 * 1024;

    /// <summary>
    /// Minimum file size to use delta sync (files smaller than this are sent whole).
    /// </summary>
    public const int MIN_DELTA_SIZE = 256 * 1024; // 256KB

    private readonly int _blockSize;

    public DeltaCalculator(int blockSize = DEFAULT_BLOCK_SIZE)
    {
        _blockSize = blockSize;
    }

    /// <summary>
    /// Generates block signatures for a file.
    /// The receiver calls this on their existing file.
    /// </summary>
    public List<BlockSignature> GenerateSignatures(string filePath)
    {
        var signatures = new List<BlockSignature>();

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, 
            FileShare.ReadWrite, _blockSize, FileOptions.SequentialScan);
        
        var buffer = new byte[_blockSize];
        long offset = 0;
        int index = 0;

        while (true)
        {
            int bytesRead = stream.Read(buffer, 0, _blockSize);
            if (bytesRead == 0) break;

            var weakHash = RollingHash.ComputeHash(buffer, 0, bytesRead);
            var strongHash = ComputeStrongHash(buffer, 0, bytesRead);

            signatures.Add(new BlockSignature
            {
                Offset = offset,
                Length = bytesRead,
                WeakHash = weakHash,
                StrongHash = strongHash,
                Index = index
            });

            offset += bytesRead;
            index++;
        }

        return signatures;
    }

    /// <summary>
    /// Calculates delta instructions by comparing source file against target signatures.
    /// The sender calls this to determine what data needs to be sent.
    /// </summary>
    public (List<DeltaInstruction> Instructions, byte[] LiteralData, DeltaSummary Summary) 
        CalculateDelta(string sourcePath, List<BlockSignature> targetSignatures)
    {
        var instructions = new List<DeltaInstruction>();
        using var literalStream = new MemoryStream();
        var summary = new DeltaSummary();

        // Build lookup table for fast signature matching
        var weakHashLookup = new Dictionary<uint, List<BlockSignature>>();
        foreach (var sig in targetSignatures)
        {
            if (!weakHashLookup.TryGetValue(sig.WeakHash, out var list))
            {
                list = new List<BlockSignature>();
                weakHashLookup[sig.WeakHash] = list;
            }
            list.Add(sig);
        }

        using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, _blockSize * 2, FileOptions.SequentialScan);

        summary.OriginalSize = sourceStream.Length;

        var buffer = new byte[_blockSize * 2]; // Double buffer for rolling
        var literalBuffer = new MemoryStream();
        
        int bytesInBuffer = 0;
        int bufferStart = 0;
        long sourceOffset = 0;

        // Fill initial buffer
        bytesInBuffer = sourceStream.Read(buffer, 0, buffer.Length);
        if (bytesInBuffer == 0)
        {
            return (instructions, Array.Empty<byte>(), summary);
        }

        while (bufferStart < bytesInBuffer)
        {
            int remaining = bytesInBuffer - bufferStart;
            int blockLen = Math.Min(_blockSize, remaining);

            // Compute weak hash for current position
            var weakHash = RollingHash.ComputeHash(buffer, bufferStart, blockLen);

            // Check for match
            BlockSignature? matchedSig = null;
            if (weakHashLookup.TryGetValue(weakHash, out var candidates))
            {
                // Verify with strong hash
                var strongHash = ComputeStrongHash(buffer, bufferStart, blockLen);
                matchedSig = candidates.FirstOrDefault(c => 
                    c.Length == blockLen && c.StrongHash == strongHash);
            }

            if (matchedSig != null)
            {
                // Flush any pending literal data
                if (literalBuffer.Length > 0)
                {
                    instructions.Add(new DeltaInstruction
                    {
                        Type = DeltaInstructionType.LiteralData,
                        Offset = literalStream.Position,
                        Length = (int)literalBuffer.Length
                    });
                    literalBuffer.WriteTo(literalStream);
                    summary.BytesToTransfer += literalBuffer.Length;
                    summary.ChangedBlocks++;
                    literalBuffer.SetLength(0);
                }

                // Add copy instruction
                instructions.Add(new DeltaInstruction
                {
                    Type = DeltaInstructionType.CopyFromTarget,
                    TargetBlockIndex = matchedSig.Index,
                    Offset = matchedSig.Offset,
                    Length = matchedSig.Length
                });
                summary.MatchedBlocks++;

                bufferStart += blockLen;
                sourceOffset += blockLen;
            }
            else
            {
                // No match - add byte to literal buffer
                literalBuffer.WriteByte(buffer[bufferStart]);
                bufferStart++;
                sourceOffset++;
            }

            // Refill buffer if needed
            if (bufferStart >= _blockSize && bytesInBuffer < buffer.Length)
            {
                // Shift remaining data to start
                int remaining2 = bytesInBuffer - bufferStart;
                if (remaining2 > 0)
                {
                    Array.Copy(buffer, bufferStart, buffer, 0, remaining2);
                }
                bufferStart = 0;
                bytesInBuffer = remaining2;

                // Try to fill rest of buffer
                int toRead = buffer.Length - bytesInBuffer;
                int read = sourceStream.Read(buffer, bytesInBuffer, toRead);
                bytesInBuffer += read;
            }
            else if (bufferStart >= _blockSize)
            {
                // Shift and refill
                int remaining2 = bytesInBuffer - bufferStart;
                Array.Copy(buffer, bufferStart, buffer, 0, remaining2);
                bufferStart = 0;
                bytesInBuffer = remaining2 + sourceStream.Read(buffer, remaining2, buffer.Length - remaining2);
            }
        }

        // Flush remaining literal data
        if (literalBuffer.Length > 0)
        {
            instructions.Add(new DeltaInstruction
            {
                Type = DeltaInstructionType.LiteralData,
                Offset = literalStream.Position,
                Length = (int)literalBuffer.Length
            });
            literalBuffer.WriteTo(literalStream);
            summary.BytesToTransfer += literalBuffer.Length;
            summary.ChangedBlocks++;
        }

        // Add overhead for instruction metadata (estimate)
        summary.BytesToTransfer += instructions.Count * 16;

        return (instructions, literalStream.ToArray(), summary);
    }

    /// <summary>
    /// Reconstructs a file from delta instructions and literal data.
    /// The receiver calls this to apply the delta.
    /// </summary>
    public void ApplyDelta(
        string targetPath, 
        string outputPath, 
        List<DeltaInstruction> instructions, 
        Stream literalDataStream)
    {
        using var targetFile = File.Exists(targetPath) 
            ? new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            : null;
        using var outputFile = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        var buffer = new byte[_blockSize];

        foreach (var instruction in instructions)
        {
            switch (instruction.Type)
            {
                case DeltaInstructionType.CopyFromTarget:
                    if (targetFile == null)
                        throw new InvalidOperationException("CopyFromTarget instruction but no target file");
                    
                    targetFile.Seek(instruction.Offset, SeekOrigin.Begin);
                    int remaining = instruction.Length;
                    while (remaining > 0)
                    {
                        int toRead = Math.Min(remaining, buffer.Length);
                        int read = targetFile.Read(buffer, 0, toRead);
                        if (read == 0) break;
                        outputFile.Write(buffer, 0, read);
                        remaining -= read;
                    }
                    break;

                case DeltaInstructionType.LiteralData:
                    literalDataStream.Seek(instruction.Offset, SeekOrigin.Begin);
                    int literalRemaining = instruction.Length;
                    while (literalRemaining > 0)
                    {
                        int toRead = Math.Min(literalRemaining, buffer.Length);
                        int read = literalDataStream.Read(buffer, 0, toRead);
                        if (read == 0) break;
                        outputFile.Write(buffer, 0, read);
                        literalRemaining -= read;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Computes XxHash64 for a buffer segment.
    /// </summary>
    private static string ComputeStrongHash(byte[] buffer, int offset, int length)
    {
        var hasher = new XxHash64();
        hasher.Append(buffer.AsSpan(offset, length));
        return Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
    }
}
