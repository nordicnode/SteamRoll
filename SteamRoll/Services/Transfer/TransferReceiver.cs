using System.Text.Json;
using System.Buffers;
using System.IO;
using System.IO.Hashing;
using System.Net.Sockets;
using System.Security;
using SteamRoll.Services.DeltaSync;
using SteamRoll.Services.Security;

namespace SteamRoll.Services.Transfer;

/// <summary>
/// Handles receiving files via TCP.
/// </summary>
public class TransferReceiver
{
    private const int BUFFER_SIZE = 81920; // 80KB chunks
    private const string PROTOCOL_MAGIC_V1 = ProtocolConstants.TRANSFER_MAGIC_V1;
    private const string PROTOCOL_MAGIC_V2 = ProtocolConstants.TRANSFER_MAGIC_V2;
    private const string PROTOCOL_MAGIC_V3 = ProtocolConstants.TRANSFER_MAGIC_V3;

    private string _receiveBasePath;
    private readonly DeltaService _deltaService = new();
    private readonly PairingService _pairingService = new();
    private readonly SettingsService? _settingsService;
    // Using a shared dictionary for path locks to prevent concurrent writes to same path
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _pathLocks;

    public event EventHandler<TransferProgress>? ProgressChanged;
    public event EventHandler<string>? Error;
    public event EventHandler<TransferResult>? TransferComplete;
    public event EventHandler<TransferApprovalEventArgs>? TransferApprovalRequested;

    // Events for special request types
    public event Func<List<Models.RemoteGame>>? LocalLibraryRequested;
    public event Func<string, string, int, Task>? PullPackageRequested;

    public TransferReceiver(string receiveBasePath, System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> pathLocks, SettingsService? settingsService = null)
    {
        _receiveBasePath = receiveBasePath;
        _pathLocks = pathLocks;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Updates the receive path for incoming transfers.
    /// Called when the user changes output path in settings.
    /// </summary>
    public void UpdateReceivePath(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath)) return;
        
        _receiveBasePath = newPath;
        LogService.Instance.Info($"Receive path updated to: {newPath}", "TransferReceiver");
    }

    public async Task HandleIncomingTransferAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var _ = client;
            using var networkStream = client.GetStream();

            // Receive header
            var header = await TransferUtils.ReceiveJsonAsync<TransferHeader>(networkStream, ct);
            if (header == null) return;

            // Check protocol compatibility
            bool isV1 = header.Magic == PROTOCOL_MAGIC_V1;
            bool isV2 = header.Magic == PROTOCOL_MAGIC_V2;
            bool isV3 = header.Magic == PROTOCOL_MAGIC_V3;

            if (!isV1 && !isV2 && !isV3)
            {
                LogService.Instance.Warning($"Unknown transfer protocol magic: {header.Magic}", "TransferReceiver");
                return;
            }

            // Handle encryption for V3 protocol
            Stream dataStream = networkStream;
            if (isV3)
            {
                var remoteEndpoint = client.Client.RemoteEndPoint?.ToString()?.Split(':')[0] ?? "unknown";
                var knownKey = _pairingService.GetPairedKey(remoteEndpoint);
                var localDeviceId = _settingsService?.Settings.DeviceId ?? "UNKNOWN";
                
                if (knownKey == null)
                {
                    LogService.Instance.Warning($"V3 encrypted transfer requested but no paired key for {remoteEndpoint}", "TransferReceiver");
                    return;
                }
                
                var handshakeResult = await TransferHandshake.RespondToHandshakeAsync(
                    networkStream, knownKey, localDeviceId, ct);
                
                if (!handshakeResult.Success)
                {
                    LogService.Instance.Warning($"Encryption handshake failed: {handshakeResult.ErrorMessage}", "TransferReceiver");
                    return;
                }
                
                dataStream = new EncryptedStream(networkStream, knownKey, leaveOpen: true);
                LogService.Instance.Info($"Encrypted connection established with {handshakeResult.RemoteDeviceId}", "TransferReceiver");
            }

            bool isCompressed = (isV2 || isV3) && header.Compression == "GZip";

            // Receive file list
            var fileInfos = await TransferUtils.ReceiveJsonAsync<List<TransferFileInfo>>(networkStream, ct);
            if (fileInfos == null) return;

            // Validate header integrity
            long calculatedTotalSize = fileInfos.Sum(f => f.Size);
            if (calculatedTotalSize != header.TotalSize)
            {
                var msg = $"Header integrity check failed. Claimed size: {header.TotalSize}, Actual: {calculatedTotalSize}";
                LogService.Instance.Warning($"Rejected transfer: {msg}", "TransferReceiver");
                await TransferUtils.SendJsonAsync(networkStream, new TransferAck { Accepted = false, Reason = msg }, ct);
                return;
            }

            // Sanitize GameName from header to prevent path traversal
            var gameName = FormatUtils.SanitizeFileName(header.GameName ?? "Unknown");

            // --- Security Check: Disk Space ---
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(_receiveBasePath)) ?? _receiveBasePath;
                var drive = new DriveInfo(root);
                long safetyBuffer = 500 * 1024 * 1024; // 500 MB buffer
                long required = header.TotalSize + safetyBuffer;

                if (drive.AvailableFreeSpace < required)
                {
                    var msg = $"Insufficient disk space. Required: {FormatUtils.FormatBytes(required)}, Available: {FormatUtils.FormatBytes(drive.AvailableFreeSpace)}";
                    LogService.Instance.Warning($"Rejected transfer: {msg}", "TransferReceiver");
                    await TransferUtils.SendJsonAsync(networkStream, new TransferAck { Accepted = false, Reason = msg }, ct);
                    return;
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning($"Could not verify disk space: {ex.Message}", "TransferReceiver");
            }

            // Handle Special Requests
            if (header.TransferType == "SaveSync")
            {
                await HandleIncomingSaveSyncAsync(networkStream, header, fileInfos, ct);
                return;
            }
            if (header.TransferType == "ListRequest")
            {
                await HandleListRequestAsync(networkStream, ct);
                return;
            }
            if (header.TransferType == "PullRequest")
            {
                await HandlePullRequestAsync(client, header, ct);
                return;
            }
            if (header.TransferType == "SpeedTest")
            {
                await HandleSpeedTestAsync(networkStream, header, ct);
                return;
            }

            var destPath = System.IO.Path.Combine(_receiveBasePath, gameName);

            // Acquire lock for this destination path
            var pathLock = _pathLocks.GetOrAdd(destPath, _ => new SemaphoreSlim(1, 1));

            if (!await pathLock.WaitAsync(2000, ct))
            {
                var msg = $"Transfer rejected: A transfer for '{gameName}' is already in progress.";
                LogService.Instance.Warning(msg, "TransferReceiver");
                await TransferUtils.SendJsonAsync(networkStream, new TransferAck { Accepted = false, Reason = msg }, ct);
                return;
            }

            try
            {
                // Request approval from UI
                var approvalArgs = new TransferApprovalEventArgs
                {
                    GameName = gameName,
                    SizeBytes = header.TotalSize,
                    FileCount = header.TotalFiles
                };

                TransferApprovalRequested?.Invoke(this, approvalArgs);

                var approved = await approvalArgs.WaitForApprovalAsync();

                if (!approved)
                {
                    LogService.Instance.Info($"Transfer of {gameName} was rejected by user", "TransferReceiver");
                    await TransferUtils.SendJsonAsync(networkStream, new TransferAck { Accepted = false }, ct);
                    return;
                }

                // Smart Sync Analysis
                var skippedFiles = new List<string>();
                var deltaSignatures = new Dictionary<string, string>();

                if (Directory.Exists(destPath))
                {
                    Dictionary<string, string> localHashes = new();
                    try
                    {
                        var metadataPath = System.IO.Path.Combine(destPath, "steamroll.json");
                        if (File.Exists(metadataPath))
                        {
                            var json = await File.ReadAllTextAsync(metadataPath, ct);
                            var metadata = JsonSerializer.Deserialize<Models.PackageMetadata>(json);
                            if (metadata?.FileHashes != null)
                            {
                                localHashes = metadata.FileHashes;
                            }
                        }
                    }
                    catch { /* Ignore */ }

                    foreach (var fileInfo in fileInfos)
                    {
                        var localRelativePath = fileInfo.RelativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                        if (!IsPathSafe(localRelativePath)) continue;

                        var localPath = System.IO.Path.Combine(destPath, localRelativePath);
                        if (!Path.GetFullPath(localPath).StartsWith(Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase)) continue;

                        if (File.Exists(localPath))
                        {
                            bool matches = false;
                            var fi = new FileInfo(localPath);
                            if (fi.Length == fileInfo.Size)
                            {
                                if (localHashes.TryGetValue(fileInfo.RelativePath, out var localHash) &&
                                    string.Equals(localHash, fileInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                                {
                                    matches = true;
                                }
                                else
                                {
                                    try
                                    {
                                        if (!string.IsNullOrEmpty(fileInfo.Sha256))
                                        {
                                            // Hash small files synchronously to reduce thread pool pressure
                                            // Only offload larger files (>1MB) to background threads
                                            var computedHash = fi.Length < 1024 * 1024
                                                ? ComputeXxHash64(localPath)
                                                : await Task.Run(() => ComputeXxHash64(localPath), ct);
                                            if (string.Equals(computedHash, fileInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                                            {
                                                matches = true;
                                            }
                                        }
                                    }
                                    catch { /* Ignore */ }
                                }
                            }

                            if (matches)
                            {
                                skippedFiles.Add(fileInfo.RelativePath);
                            }
                            else if (header.SupportsDelta && fi.Length >= DeltaCalculator.MIN_DELTA_SIZE)
                            {
                                // File exists but differs - generate signatures for delta sync
                                try
                                {
                                    var sigs = _deltaService.GenerateSignatures(localPath);
                                    if (sigs.Count > 0)
                                    {
                                        var sigBytes = DeltaService.SerializeSignatures(sigs);
                                        deltaSignatures[fileInfo.RelativePath] = System.Text.Encoding.UTF8.GetString(sigBytes);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogService.Instance.Debug($"Failed to generate delta signatures: {ex.Message}", "TransferReceiver");
                                }
                            }
                        }

                        // Analysis progress
                        ProgressChanged?.Invoke(this, new TransferProgress
                        {
                            GameName = gameName,
                            CurrentFile = $"Analyzing local files: {fileInfo.RelativePath}",
                            IsSending = false
                        });
                    }
                }

                // Send ACK with skip list and delta signatures
                await TransferUtils.SendJsonAsync(networkStream, new TransferAck 
                { 
                    Accepted = true, 
                    SkippedFiles = skippedFiles,
                    SupportsDelta = true,
                    DeltaSignatures = deltaSignatures
                }, ct);
                
                if (deltaSignatures.Count > 0)
                {
                    LogService.Instance.Info($"Sent delta signatures for {deltaSignatures.Count} files", "TransferReceiver");
                }

                Directory.CreateDirectory(destPath);

                // Transfer State Management
                var existingState = TransferState.Load(destPath);
                var fileListHash = TransferState.ComputeFileListHash(fileInfos);
                var completedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long previouslyReceivedBytes = 0;

                if (existingState != null && existingState.FileListHash == fileListHash)
                {
                    foreach (var completed in existingState.CompletedFiles)
                    {
                        completedFiles.Add(completed);
                    }
                    previouslyReceivedBytes = existingState.BytesReceived;
                    LogService.Instance.Info($"Resuming transfer for {gameName}", "TransferReceiver");
                }
                else if (existingState != null)
                {
                    TransferState.Delete(destPath);
                }

                var state = existingState ?? new TransferState
                {
                    GameName = gameName,
                    TotalFiles = fileInfos.Count,
                    TotalSize = header.TotalSize,
                    StartedAt = DateTime.UtcNow,
                    FileListHash = fileListHash
                };

                long receivedBytes = previouslyReceivedBytes;
                var buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);

                Stream inputStream = networkStream;
                System.IO.Compression.GZipStream? gzipStream = null;

                if (isCompressed)
                {
                    gzipStream = new System.IO.Compression.GZipStream(networkStream, System.IO.Compression.CompressionMode.Decompress, true);
                    inputStream = gzipStream;
                }

                DateTime lastStateSave = DateTime.UtcNow;
                DateTime lastProgressTime = DateTime.MinValue;

                try
                {
                    foreach (var fileInfo in fileInfos)
                    {
                        var localRelativePath = fileInfo.RelativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

                        if (!IsPathSafe(localRelativePath))
                        {
                            LogService.Instance.Warning($"Blocked potentially malicious file path: {localRelativePath}", "TransferReceiver");
                            // SECURITY: Do NOT consume stream data from malicious peers.
                            // Throw exception to immediately close connection and prevent DoS attacks.
                            throw new SecurityException($"Malicious path detected: {localRelativePath}. Connection terminated.");
                        }

                        if (completedFiles.Contains(fileInfo.RelativePath))
                        {
                            await ConsumeStreamBytes(inputStream, fileInfo.Size, buffer, ct);
                            ProgressChanged?.Invoke(this, new TransferProgress
                            {
                                GameName = gameName,
                                CurrentFile = fileInfo.RelativePath + " (skipped - already received)",
                                BytesTransferred = receivedBytes,
                                TotalBytes = header.TotalSize,
                                IsSending = false
                            });
                            continue;
                        }

                        var fullPath = System.IO.Path.Combine(destPath, localRelativePath);
                        if (!Path.GetFullPath(fullPath).StartsWith(Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                        {
                            LogService.Instance.Warning($"Blocked path traversal attempt: {localRelativePath}", "TransferReceiver");
                            await ConsumeStreamBytes(inputStream, fileInfo.Size, buffer, ct);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                        // Check for delta mode flag if we sent delta signatures
                        bool isDeltaTransfer = false;
                        if (deltaSignatures.ContainsKey(fileInfo.RelativePath))
                        {
                            var modeFlagBuffer = new byte[1];
                            var modeRead = await inputStream.ReadAsync(modeFlagBuffer, ct);
                            if (modeRead == 1)
                            {
                                isDeltaTransfer = modeFlagBuffer[0] == 1;
                            }
                        }

                        if (isDeltaTransfer)
                        {
                            // Delta transfer mode - read and apply delta
                            var intBuffer = new byte[4];
                            
                            // Read instruction count
                            await inputStream.ReadAsync(intBuffer, ct);
                            var instructionCount = BitConverter.ToInt32(intBuffer);
                            
                            // Read literal data size
                            await inputStream.ReadAsync(intBuffer, ct);
                            var literalDataSize = BitConverter.ToInt32(intBuffer);
                            
                            // Read instruction bytes length
                            await inputStream.ReadAsync(intBuffer, ct);
                            var instructionBytesLength = BitConverter.ToInt32(intBuffer);
                            
                            // Read instructions
                            var instructionBytes = new byte[instructionBytesLength];
                            var totalInstructionRead = 0;
                            while (totalInstructionRead < instructionBytesLength)
                            {
                                var read = await inputStream.ReadAsync(
                                    instructionBytes.AsMemory(totalInstructionRead, instructionBytesLength - totalInstructionRead), ct);
                                if (read == 0) break;
                                totalInstructionRead += read;
                            }
                            
                            // Read literal data
                            var literalData = new byte[literalDataSize];
                            var totalLiteralRead = 0;
                            while (totalLiteralRead < literalDataSize)
                            {
                                var read = await inputStream.ReadAsync(
                                    literalData.AsMemory(totalLiteralRead, literalDataSize - totalLiteralRead), ct);
                                if (read == 0) break;
                                totalLiteralRead += read;
                            }
                            
                            // Deserialize instructions and apply delta
                            var instructions = DeltaService.DeserializeInstructions(instructionBytes);
                            
                            // Create temp file for delta application
                            var tempPath = fullPath + ".delta_tmp";
                            var existingPath = fullPath; // The file we have signatures for
                            
                            try
                            {
                                _deltaService.ApplyDelta(existingPath, tempPath, instructions, literalData);
                                
                                // Replace original with reconstructed file
                                if (File.Exists(fullPath))
                                    File.Delete(fullPath);
                                File.Move(tempPath, fullPath);
                            }
                            finally
                            {
                                // Clean up temp file if it still exists (e.g., on failure)
                                if (File.Exists(tempPath))
                                {
                                    try { File.Delete(tempPath); } catch { /* Ignore cleanup failures */ }
                                }
                            }
                            
                            receivedBytes += fileInfo.Size; // Count as full file for progress
                            
                            LogService.Instance.Info($"Applied delta for {fileInfo.RelativePath}", "TransferReceiver");
                            
                            ProgressChanged?.Invoke(this, new TransferProgress
                            {
                                GameName = gameName,
                                CurrentFile = fileInfo.RelativePath + " (delta applied)",
                                BytesTransferred = receivedBytes,
                                TotalBytes = header.TotalSize,
                                IsSending = false
                            });
                        }
                        else
                        {
                            // Full file transfer mode
                            using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
                            // Use XxHash64 for faster integrity checking (10-20x faster than SHA-256)
                            var hasher = !string.IsNullOrEmpty(fileInfo.Sha256) ? new XxHash64() : null;

                            long remaining = fileInfo.Size;
                            while (remaining > 0)
                            {
                                var toRead = (int)Math.Min(remaining, buffer.Length);
                                var bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                                if (bytesRead == 0) break;

                                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                                hasher?.Append(buffer.AsSpan(0, bytesRead));

                                remaining -= bytesRead;
                                receivedBytes += bytesRead;

                                var progressNow = DateTime.UtcNow;
                                if ((progressNow - lastProgressTime).TotalMilliseconds >= 100)
                                {
                                    lastProgressTime = progressNow;
                                    ProgressChanged?.Invoke(this, new TransferProgress
                                    {
                                        GameName = gameName,
                                        CurrentFile = fileInfo.RelativePath,
                                        BytesTransferred = receivedBytes,
                                        TotalBytes = header.TotalSize,
                                        IsSending = false
                                    });
                                }
                            }

                            if (!string.IsNullOrEmpty(fileInfo.Sha256) && hasher != null)
                            {
                                var actualHash = Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
                                if (!string.Equals(actualHash, fileInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                                {
                                    LogService.Instance.Error($"Checksum mismatch for {fileInfo.RelativePath}", category: "TransferReceiver");
                                    throw new InvalidDataException($"Checksum verification failed for {fileInfo.RelativePath}");
                                }
                            }
                        }

                        state.FilesCompleted++;
                        state.BytesReceived = receivedBytes;
                        state.CompletedFiles.Add(fileInfo.RelativePath);

                        var now = DateTime.UtcNow;
                        if ((now - lastStateSave).TotalSeconds >= 5 || state.FilesCompleted == state.TotalFiles)
                        {
                            TransferState.Save(destPath, state);
                            lastStateSave = now;
                        }
                    }
                }
                finally
                {
                     ArrayPool<byte>.Shared.Return(buffer);
                     if (gzipStream != null) await gzipStream.DisposeAsync();
                }

                TransferState.Delete(destPath);
                await MarkAsReceivedPackage(destPath);

                bool verificationPassed = true;
                var verificationErrors = new List<string>();

                try
                {
                    var (isValid, mismatches) = await PackageBuilder.VerifyIntegrityAsync(destPath);
                    verificationPassed = isValid;
                    verificationErrors = mismatches;
                }
                catch (Exception verifyEx)
                {
                    LogService.Instance.Warning($"Could not verify package {gameName}: {verifyEx.Message}", "TransferReceiver");
                }

                await TransferUtils.SendJsonAsync(networkStream, new TransferComplete { Success = true }, ct);

                TransferComplete?.Invoke(this, new TransferResult
                {
                    Success = true,
                    GameName = gameName,
                    Path = destPath,
                    BytesTransferred = receivedBytes,
                    WasReceived = true,
                    VerificationPassed = verificationPassed,
                    VerificationErrors = verificationErrors
                });

                LogService.Instance.Info($"Received package: {gameName}", "TransferReceiver");
            }
            finally
            {
                pathLock.Release();
            }

        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Receive error: {ex.Message}", ex, "TransferReceiver");
            Error?.Invoke(this, $"Receive failed: {ex.Message}");
        }
    }

    private async Task ConsumeStreamBytes(Stream stream, long count, byte[] buffer, CancellationToken ct)
    {
        long remaining = count;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, buffer.Length);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (bytesRead == 0) break;
            remaining -= bytesRead;
        }
    }

    private async Task HandleIncomingSaveSyncAsync(NetworkStream stream, TransferHeader header, List<TransferFileInfo> fileInfos, CancellationToken ct)
    {
        var gameName = header.GameName ?? "Unknown";
        var tempZip = Path.GetTempFileName();

        await TransferUtils.SendJsonAsync(stream, new TransferAck { Accepted = true }, ct);

        using (var fs = File.Create(tempZip))
        {
            var buffer = new byte[BUFFER_SIZE];
            foreach (var info in fileInfos)
            {
                long remaining = info.Size;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
                    if (read == 0) break;
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                }
            }
        }

        await TransferUtils.SendJsonAsync(stream, new TransferComplete { Success = true }, ct);

        TransferComplete?.Invoke(this, new TransferResult
        {
            Success = true,
            GameName = gameName,
            Path = tempZip,
            WasReceived = true,
            VerificationPassed = true,
            IsSaveSync = true
        });
    }

    private async Task HandleListRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var games = LocalLibraryRequested?.Invoke() ?? new List<Models.RemoteGame>();
        await TransferUtils.SendJsonAsync(stream, games, ct);
    }

    private async Task HandlePullRequestAsync(TcpClient client, TransferHeader header, CancellationToken ct)
    {
        var remoteEndpoint = client.Client.RemoteEndPoint as System.Net.IPEndPoint;
        if (remoteEndpoint == null) return;

        var targetIp = remoteEndpoint.Address.ToString();
        // We need port but we can't get listener port easily here, assume same port config
        // Or pass it via header? But header is standard.
        // Assuming default port for now or we need to pass it in constructor
        var targetPort = 27051; // DEFAULT_PORT

        if (PullPackageRequested != null && header.GameName != null)
        {
             _ = PullPackageRequested.Invoke(header.GameName, targetIp, targetPort);
        }
    }

    private async Task HandleSpeedTestAsync(NetworkStream stream, TransferHeader header, CancellationToken ct)
    {
        var buffer = new byte[BUFFER_SIZE];
        long received = 0;
        while (received < header.TotalSize)
        {
             var toRead = (int)Math.Min(buffer.Length, header.TotalSize - received);
             var read = await stream.ReadAsync(buffer, 0, toRead, ct);
             if (read == 0) break;
             received += read;
        }
        await TransferUtils.SendJsonAsync(stream, new TransferComplete { Success = true }, ct);
    }

    private async Task MarkAsReceivedPackage(string packagePath)
    {
        var markerPath = System.IO.Path.Combine(packagePath, ".steamroll_received");
        var markerContent = new ReceivedMarker
        {
            ReceivedAt = DateTime.Now,
            ReceivedFrom = "Network"
        };
        var json = JsonSerializer.Serialize(markerContent, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(markerPath, json);
    }

    /// <summary>
    /// Computes XxHash64 for a file. 10-20x faster than SHA-256.
    /// </summary>
    private static string ComputeXxHash64(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        var hash = new XxHash64();
        hash.Append(stream);
        return Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
    }

    private static bool IsPathSafe(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        if (relativePath.StartsWith(".." + Path.DirectorySeparatorChar) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar) ||
            relativePath.Contains(Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar) ||
            relativePath.Contains(Path.AltDirectorySeparatorChar + ".." + Path.AltDirectorySeparatorChar) ||
            relativePath.EndsWith(Path.DirectorySeparatorChar + "..") ||
            relativePath.EndsWith(Path.AltDirectorySeparatorChar + "..") ||
            relativePath == "..") return false;
        if (relativePath.StartsWith("/") || relativePath.StartsWith("\\")) return false;
        if (Path.IsPathRooted(relativePath)) return false;
        var invalidChars = Path.GetInvalidFileNameChars().Where(c => c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar).ToArray();
        if (relativePath.IndexOfAny(invalidChars) >= 0) return false;
        return true;
    }
}
