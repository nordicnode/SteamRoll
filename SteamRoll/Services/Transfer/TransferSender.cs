using System.Buffers;
using System.IO;
using System.IO.Hashing;
using System.Net.Sockets;
using System.Text.Json;
using SteamRoll.Services.DeltaSync;
using SteamRoll.Services.Security;

namespace SteamRoll.Services.Transfer;

/// <summary>
/// Handles sending files via TCP.
/// </summary>
public class TransferSender
{
    private const int BUFFER_SIZE = 81920; // 80KB chunks
    private const string PROTOCOL_MAGIC_V2 = ProtocolConstants.TRANSFER_MAGIC_V2;
    private const string PROTOCOL_MAGIC_V3 = ProtocolConstants.TRANSFER_MAGIC_V3;

    private readonly SettingsService? _settingsService;
    private readonly DeltaService _deltaService = new();
    private readonly PairingService _pairingService = new();

    public event EventHandler<TransferProgress>? ProgressChanged;
    public event EventHandler<string>? Error;
    public event EventHandler<TransferResult>? TransferComplete;

    /// <summary>
    /// Raised when delta sync saves bandwidth.
    /// </summary>
    public event EventHandler<DeltaSummary>? DeltaSyncUsed;

    public TransferSender(SettingsService? settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<bool> SendFileOrFolderAsync(string targetIp, int targetPort, string path, string transferType, string? overrideGameName = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            Error?.Invoke(this, $"Path does not exist: {path}");
            return false;
        }

        var gameName = overrideGameName ?? System.IO.Path.GetFileName(path);
        var sourcePath = path;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(targetIp, targetPort, ct);

            using var networkStream = client.GetStream();

            // Gather file list with checksums - use EnumerateFiles for lazy evaluation
            var files = File.Exists(sourcePath)
                ? new[] { sourcePath }
                : Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories);

            // Base path for relative calculations
            var basePath = File.Exists(sourcePath) ? System.IO.Path.GetDirectoryName(sourcePath)! : sourcePath;

            // Try to load local metadata to speed up hashing (Smart Hash)
            Dictionary<string, string> knownHashes = new();
            DateTime metadataDate = DateTime.MinValue;
            try
            {
                if (Directory.Exists(sourcePath))
                {
                    var metadataPath = System.IO.Path.Combine(sourcePath, "steamroll.json");
                    if (File.Exists(metadataPath))
                    {
                        var json = await File.ReadAllTextAsync(metadataPath, ct);
                        var metadata = JsonSerializer.Deserialize<Models.PackageMetadata>(json);
                        if (metadata?.FileHashes != null)
                        {
                            knownHashes = metadata.FileHashes;
                            metadataDate = metadata.CreatedDate;
                        }
                    }
                }
            }
            catch { /* Ignore metadata read errors */ }

            // Sequential processing for HDD-friendly I/O (parallel thrashes mechanical drives)
            // Process files iteratively with cancellation checks for responsiveness
            var fileInfos = new List<TransferFileInfo>();
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested(); // Allow cancellation between files
                
                var relativePath = System.IO.Path.GetRelativePath(basePath, f).Replace('\\', '/');
                var fi = new FileInfo(f);
                var size = fi.Length;
                string hash;

                bool isUnchanged = fi.LastWriteTimeUtc <= metadataDate;
                var originalRelativePath = System.IO.Path.GetRelativePath(basePath, f);

                string? cachedHash = null;
                if (isUnchanged)
                {
                    if (!knownHashes.TryGetValue(relativePath, out cachedHash))
                    {
                        knownHashes.TryGetValue(originalRelativePath, out cachedHash);
                    }
                }

                if (!string.IsNullOrEmpty(cachedHash))
                {
                     hash = cachedHash;
                }
                else
                {
                     hash = ComputeXxHash64(f);
                }

                fileInfos.Add(new TransferFileInfo
                {
                    RelativePath = relativePath, // Always send forward slashes
                    Size = size,
                    Sha256 = hash
                });
            }

            var totalSize = fileInfos.Sum(f => f.Size);

            // Determine compression and encryption settings
            bool useCompression = _settingsService?.Settings.EnableTransferCompression ?? true;
            bool useEncryption = _settingsService?.Settings.RequireTransferEncryption ?? false;
            string localDeviceId = _settingsService?.Settings.DeviceId ?? "UNKNOWN";
            string protocolMagic = useEncryption ? PROTOCOL_MAGIC_V3 : PROTOCOL_MAGIC_V2;

            // If encryption is enabled, perform handshake first
            Stream dataStream = networkStream;
            if (useEncryption)
            {
                // For now, try to get cached key or fail gracefully
                // In production, you'd need a pairing UI flow
                var knownKey = _pairingService.GetPairedKey(targetIp);
                if (knownKey != null)
                {
                    var handshakeResult = await TransferHandshake.InitiateHandshakeAsync(
                        networkStream, knownKey, localDeviceId, ct);
                    
                    if (!handshakeResult.Success)
                    {
                        Error?.Invoke(this, $"Encryption handshake failed: {handshakeResult.ErrorMessage}");
                        return false;
                    }
                    
                    dataStream = new EncryptedStream(networkStream, knownKey, leaveOpen: true);
                    LogService.Instance.Info($"Encrypted connection established with {handshakeResult.RemoteDeviceId}", "TransferSender");
                }
                else
                {
                    // SECURITY: Do not fallback to unencrypted when encryption is required
                    var msg = $"Encryption required but no paired key found for {targetIp}. Pair the device first.";
                    LogService.Instance.Error(msg, category: "TransferSender");
                    Error?.Invoke(this, msg);
                    return false;
                }
            }

            // Send header
            var header = new TransferHeader
            {
                Magic = protocolMagic,
                GameName = gameName,
                TotalFiles = fileInfos.Count,
                TotalSize = totalSize,
                IsReceivedPackage = false, // Originated here
                Compression = useCompression ? "GZip" : "None",
                TransferType = transferType
            };

            await TransferUtils.SendJsonAsync(networkStream, header, ct);

            // Send file list
            await TransferUtils.SendJsonAsync(networkStream, fileInfos, ct);

            // Wait for acknowledgment
            var ack = await TransferUtils.ReceiveJsonAsync<TransferAck>(networkStream, ct);
            if (ack?.Accepted != true)
            {
                var reason = !string.IsNullOrEmpty(ack?.Reason) ? $": {ack.Reason}" : "";
                Error?.Invoke(this, $"Transfer was rejected by recipient{reason}");
                return false;
            }

            var skippedFiles = new HashSet<string>(ack.SkippedFiles ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            
            // Parse delta signatures for files that receiver wants to update via delta
            var deltaSignatures = new Dictionary<string, List<BlockSignature>>(StringComparer.OrdinalIgnoreCase);
            if (ack.SupportsDelta && ack.DeltaSignatures != null)
            {
                foreach (var (filePath, sigJson) in ack.DeltaSignatures)
                {
                    var sigs = DeltaService.DeserializeSignatures(System.Text.Encoding.UTF8.GetBytes(sigJson));
                    if (sigs.Count > 0)
                    {
                        deltaSignatures[filePath] = sigs;
                    }
                }
                if (deltaSignatures.Count > 0)
                {
                    LogService.Instance.Info($"Delta sync enabled for {deltaSignatures.Count} files", "TransferSender");
                }
            }

            // Adjust total size to transfer
            var bytesToTransfer = fileInfos.Where(f => !skippedFiles.Contains(f.RelativePath)).Sum(f => f.Size);
            var skippedBytes = totalSize - bytesToTransfer;

            // Send files
            long sentBytes = 0;
            // Initialize with skipped bytes so progress bar starts correctly if we're resuming/updating
            long totalProgressBytes = skippedBytes;

            var buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
            var limiter = new BandwidthLimiter(() => _settingsService?.Settings.TransferSpeedLimit ?? 0);
            var startTime = DateTime.UtcNow;
            var lastProgressTime = DateTime.MinValue;

            // Setup the output stream (either raw network stream or compressed wrapper)
            Stream outputStream = networkStream;
            System.IO.Compression.GZipStream? gzipStream = null;

            if (useCompression)
            {
                gzipStream = new System.IO.Compression.GZipStream(networkStream, System.IO.Compression.CompressionLevel.Fastest, true);
                outputStream = gzipStream;
            }

            try
            {
                foreach (var fileInfo in fileInfos)
                {
                    if (skippedFiles.Contains(fileInfo.RelativePath))
                    {
                        // Skip sending content, but update progress
                        ProgressChanged?.Invoke(this, new TransferProgress
                        {
                            GameName = gameName,
                            CurrentFile = fileInfo.RelativePath + " (skipped)",
                            BytesTransferred = totalProgressBytes,
                            TotalBytes = totalSize, // Keep total relative to full package size
                            IsSending = true,
                            EstimatedTimeRemaining = null
                        });

                        continue;
                    }

                    var fullPath = System.IO.Path.Combine(basePath, fileInfo.RelativePath);

                    // Check if we should use delta sync for this file
                    if (deltaSignatures.TryGetValue(fileInfo.RelativePath, out var targetSigs))
                    {
                        var deltaResult = _deltaService.CalculateDelta(fullPath, targetSigs);
                        if (deltaResult.HasValue && deltaResult.Value.Summary.SavingsPercent > 20)
                        {
                            var (instructions, literalData, summary) = deltaResult.Value;
                            
                            // Send delta mode flag (1 = delta transfer)
                            await outputStream.WriteAsync(new byte[] { 1 }, ct);
                            
                            // Send instruction count (4 bytes) + literal data size (4 bytes)
                            await outputStream.WriteAsync(BitConverter.GetBytes(instructions.Count), ct);
                            await outputStream.WriteAsync(BitConverter.GetBytes(literalData.Length), ct);
                            
                            // Send serialized instructions
                            var instructionBytes = DeltaService.SerializeInstructions(instructions);
                            await outputStream.WriteAsync(BitConverter.GetBytes(instructionBytes.Length), ct);
                            await outputStream.WriteAsync(instructionBytes, ct);
                            
                            // Send literal data
                            await outputStream.WriteAsync(literalData, ct);
                            
                            sentBytes += 1 + 4 + 4 + 4 + instructionBytes.Length + literalData.Length;
                            totalProgressBytes += fileInfo.Size;
                            
                            DeltaSyncUsed?.Invoke(this, summary);
                            LogService.Instance.Info(
                                $"Delta sync for {fileInfo.RelativePath}: {summary.SavingsPercent:F1}% saved " +
                                $"({FormatUtils.FormatBytes(summary.BytesSaved)} saved)", "TransferSender");
                            
                            ProgressChanged?.Invoke(this, new TransferProgress
                            {
                                GameName = gameName,
                                CurrentFile = fileInfo.RelativePath + " (delta)",
                                BytesTransferred = totalProgressBytes,
                                TotalBytes = totalSize,
                                IsSending = true
                            });
                            
                            continue;
                        }
                    }

                    // Send full file mode flag (0 = full file transfer)
                    // Only send flag if receiver sent signatures for this file (meaning it expects mode flag)
                    if (deltaSignatures.ContainsKey(fileInfo.RelativePath))
                    {
                        // This shouldn't happen often - we get here if delta calculation failed
                        await outputStream.WriteAsync(new byte[] { 0 }, ct);
                    }

                    // Use FileOptions for better sequential read performance
                    // Use FileShare.ReadWrite to allow reading files that are currently open by the game
                    using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                        bufferSize: 4096, useAsync: true);

                    int bytesRead;
                    while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
                    {
                        // Throttle based on uncompressed bytes (read speed)
                        await limiter.WaitAsync(bytesRead, ct);

                        await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);

                        sentBytes += bytesRead;
                        totalProgressBytes += bytesRead;

                        var now = DateTime.UtcNow;
                        if ((now - lastProgressTime).TotalMilliseconds >= 100)
                        {
                            lastProgressTime = now;

                            // Calculate ETA based on actual transfer speed
                            TimeSpan? eta = null;
                            var elapsed = now - startTime;
                            if (elapsed.TotalSeconds > 2 && sentBytes > 0)
                            {
                                var bytesPerSecond = sentBytes / elapsed.TotalSeconds;
                                if (bytesPerSecond > 0)
                                {
                                    var remainingBytes = bytesToTransfer - sentBytes;
                                    eta = TimeSpan.FromSeconds(remainingBytes / bytesPerSecond);
                                }
                            }

                            ProgressChanged?.Invoke(this, new TransferProgress
                            {
                                GameName = gameName,
                                CurrentFile = fileInfo.RelativePath,
                                BytesTransferred = totalProgressBytes,
                                TotalBytes = totalSize,
                                IsSending = true,
                                EstimatedTimeRemaining = eta
                            });
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);

                // Ensure we finish the compression stream properly
                if (gzipStream != null)
                {
                    // Dispose flushes the GZip footer
                    await gzipStream.DisposeAsync();
                }
            }

            // Wait for completion confirmation
            var complete = await TransferUtils.ReceiveJsonAsync<TransferComplete>(networkStream, ct);

            TransferComplete?.Invoke(this, new TransferResult
            {
                Success = complete?.Success == true,
                GameName = gameName,
                Path = sourcePath,
                BytesTransferred = sentBytes
            });

            return complete?.Success == true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Send error: {ex.Message}", ex, "TransferSender");
            Error?.Invoke(this, $"Transfer failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Computes XxHash64 for a file. 10-20x faster than SHA-256.
    /// Used for integrity checking during transfers (not cryptographic security).
    /// </summary>
    private static string ComputeXxHash64(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        var hash = new XxHash64();
        hash.Append(stream);
        return Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
    }
}
