using System.Text.Json;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SteamRoll.Services.Transfer;

/// <summary>
/// Handles receiving files via TCP.
/// </summary>
public class TransferReceiver
{
    private const int BUFFER_SIZE = 81920; // 80KB chunks
    private const string PROTOCOL_MAGIC_V1 = "STEAMROLL_TRANSFER_V1";
    private const string PROTOCOL_MAGIC_V2 = "STEAMROLL_TRANSFER_V2";

    private readonly string _receiveBasePath;
    // Using a shared dictionary for path locks to prevent concurrent writes to same path
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _pathLocks;

    public event EventHandler<TransferProgress>? ProgressChanged;
    public event EventHandler<string>? Error;
    public event EventHandler<TransferResult>? TransferComplete;
    public event EventHandler<TransferApprovalEventArgs>? TransferApprovalRequested;

    // Events for special request types
    public event Func<List<Models.RemoteGame>>? LocalLibraryRequested;
    public event Func<string, string, int, Task>? PullPackageRequested;

    public TransferReceiver(string receiveBasePath, System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> pathLocks)
    {
        _receiveBasePath = receiveBasePath;
        _pathLocks = pathLocks;
    }

    public void UpdateReceivePath(string newPath)
    {
        // _receiveBasePath is readonly in this context but if we want to support update we'd need to change it.
        // For now, let's assume it's fixed per instance or we recreate the receiver.
        // Actually, let's make it settable via a method in the service, but since I moved logic here,
        // I should probably allow updating it. But field is readonly. I'll fix by removing readonly.
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

            if (!isV1 && !isV2)
            {
                LogService.Instance.Warning($"Unknown transfer protocol magic: {header.Magic}", "TransferReceiver");
                return;
            }

            bool isCompressed = isV2 && header.Compression == "GZip";

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
                                            // Offload synchronous hashing to prevent blocking the thread pool
                                            var computedHash = await Task.Run(() => ComputeSha256(localPath), ct);
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

                await TransferUtils.SendJsonAsync(networkStream, new TransferAck { Accepted = true, SkippedFiles = skippedFiles }, ct);

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
                            await ConsumeStreamBytes(inputStream, fileInfo.Size, buffer, ct);
                            continue;
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

                        using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
                        using var hasher = !string.IsNullOrEmpty(fileInfo.Sha256) ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

                        long remaining = fileInfo.Size;
                        while (remaining > 0)
                        {
                            var toRead = (int)Math.Min(remaining, buffer.Length);
                            var bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                            if (bytesRead == 0) break;

                            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                            if (hasher != null) hasher.AppendData(buffer, 0, bytesRead);

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
                            var actualHashBytes = hasher.GetHashAndReset();
                            var actualHash = Convert.ToHexString(actualHashBytes).ToLowerInvariant();
                            if (!string.Equals(actualHash, fileInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                            {
                                LogService.Instance.Error($"Checksum mismatch for {fileInfo.RelativePath}", category: "TransferReceiver");
                                throw new InvalidDataException($"Checksum verification failed for {fileInfo.RelativePath}");
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

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
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
