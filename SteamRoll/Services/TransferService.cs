using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;


namespace SteamRoll.Services;

/// <summary>
/// Handles TCP-based file transfers between SteamRoll instances.
/// Transfers entire game package folders with progress tracking.
/// </summary>
public class TransferService : IDisposable
{
    private const int DEFAULT_PORT = 27051;
    private const int BUFFER_SIZE = 81920; // 80KB chunks
    private const string PROTOCOL_MAGIC_V1 = "STEAMROLL_TRANSFER_V1";
    private const string PROTOCOL_MAGIC_V2 = "STEAMROLL_TRANSFER_V2";

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _receiveBasePath;
    private readonly SettingsService? _settingsService;

    public event EventHandler<TransferProgress>? ProgressChanged;
    public event EventHandler<TransferResult>? TransferComplete;
    public event EventHandler<string>? Error;
    
    /// <summary>
    /// Fired when an incoming transfer needs approval. Handler should set e.Approved.
    /// </summary>
    public event EventHandler<TransferApprovalEventArgs>? TransferApprovalRequested;

    public bool IsListening { get; private set; }
    public int Port { get; private set; }

    public TransferService(string receiveBasePath, SettingsService? settingsService = null)
    {
        _receiveBasePath = receiveBasePath;
        _settingsService = settingsService;
        Port = DEFAULT_PORT;
    }
    
    /// <summary>
    /// Updates the base path for received packages.
    /// </summary>
    public void UpdateReceivePath(string newPath)
    {
        _receiveBasePath = newPath;
        LogService.Instance.Info($"Transfer receive path updated to: {newPath}", "TransferService");
    }


    /// <summary>
    /// Starts listening for incoming transfers.
    /// </summary>
    public bool StartListening(int port = DEFAULT_PORT)
    {
        if (IsListening) return true;

        try
        {
            Port = port;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            IsListening = true;

            _ = AcceptClientsAsync(_cts.Token);

            LogService.Instance.Info($"Transfer service listening on port {port}", "TransferService");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to start transfer listener on port {port}", ex, "TransferService");
            Error?.Invoke(this, $"Failed to start listener: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stops listening for transfers.
    /// </summary>
    public void StopListening()
    {
        IsListening = false;
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
    }

    /// <summary>
    /// Sends a game package folder to a peer.
    /// </summary>
    public async Task<bool> SendPackageAsync(string targetIp, int targetPort, string packagePath, CancellationToken ct = default)
    {
        if (!Directory.Exists(packagePath))
        {
            Error?.Invoke(this, $"Package path does not exist: {packagePath}");
            return false;
        }

        var gameName = System.IO.Path.GetFileName(packagePath);
        
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(targetIp, targetPort, ct);

            using var networkStream = client.GetStream();
            
            // Gather file list with checksums
            var files = Directory.GetFiles(packagePath, "*", SearchOption.AllDirectories);
            var fileInfos = files.AsParallel().Select(f => new TransferFileInfo
            {
                RelativePath = System.IO.Path.GetRelativePath(packagePath, f),
                Size = new FileInfo(f).Length,
                Sha256 = ComputeSha256(f) // Include checksum for integrity verification
            }).ToList();

            var totalSize = fileInfos.Sum(f => f.Size);

            // Determine compression settings
            bool useCompression = _settingsService?.Settings.EnableTransferCompression ?? true;
            string protocolMagic = PROTOCOL_MAGIC_V2;

            // Send header
            var header = new TransferHeader
            {
                Magic = protocolMagic,
                GameName = gameName,
                TotalFiles = fileInfos.Count,
                TotalSize = totalSize,
                IsReceivedPackage = false, // Originated here
                Compression = useCompression ? "GZip" : "None"
            };

            await SendJsonAsync(networkStream, header, ct);

            // Send file list (uncompressed for now to keep it simple, or we could compress if header sent)
            await SendJsonAsync(networkStream, fileInfos, ct);

            // Wait for acknowledgment
            var ack = await ReceiveJsonAsync<TransferAck>(networkStream, ct);
            if (ack?.Accepted != true)
            {
                Error?.Invoke(this, "Transfer was rejected by recipient");
                return false;
            }

            var skippedFiles = new HashSet<string>(ack.SkippedFiles ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            // Adjust total size to transfer
            var bytesToTransfer = fileInfos.Where(f => !skippedFiles.Contains(f.RelativePath)).Sum(f => f.Size);
            var skippedBytes = totalSize - bytesToTransfer;

            // Send files
            long sentBytes = 0;
            // Initialize with skipped bytes so progress bar starts correctly if we're resuming/updating
            long totalProgressBytes = skippedBytes;

            var buffer = new byte[BUFFER_SIZE];
            var limiter = new BandwidthLimiter(_settingsService?.Settings.TransferSpeedLimit ?? 0);
            var startTime = DateTime.UtcNow;

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

                    var fullPath = System.IO.Path.Combine(packagePath, fileInfo.RelativePath);
                    using var fileStream = File.OpenRead(fullPath);

                    int bytesRead;
                    while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
                    {
                        // Throttle based on uncompressed bytes (read speed)
                        await limiter.WaitAsync(bytesRead, ct);

                        await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);

                        sentBytes += bytesRead;
                        totalProgressBytes += bytesRead;

                        // Calculate ETA based on actual transfer speed
                        TimeSpan? eta = null;
                        var elapsed = DateTime.UtcNow - startTime;
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
            finally
            {
                // Ensure we finish the compression stream properly
                if (gzipStream != null)
                {
                    // Dispose flushes the GZip footer
                    await gzipStream.DisposeAsync();
                }
            }

            // Wait for completion confirmation
            var complete = await ReceiveJsonAsync<TransferComplete>(networkStream, ct);

            TransferComplete?.Invoke(this, new TransferResult
            {
                Success = complete?.Success == true,
                GameName = gameName,
                Path = packagePath,
                BytesTransferred = sentBytes
            });

            return complete?.Success == true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Send error: {ex.Message}", ex, "TransferService");
            Error?.Invoke(this, $"Transfer failed: {ex.Message}");
            return false;
        }
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleIncomingTransferAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogService.Instance.Debug($"Accept error: {ex.Message}", "TransferService");
            }
        }
    }

    private async Task HandleIncomingTransferAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var _ = client;
            using var networkStream = client.GetStream();

            // Receive header
            var header = await ReceiveJsonAsync<TransferHeader>(networkStream, ct);
            if (header == null) return;

            // Check protocol compatibility
            bool isV1 = header.Magic == PROTOCOL_MAGIC_V1;
            bool isV2 = header.Magic == PROTOCOL_MAGIC_V2;

            if (!isV1 && !isV2)
            {
                LogService.Instance.Warning($"Unknown transfer protocol magic: {header.Magic}", "TransferService");
                return;
            }

            bool isCompressed = isV2 && header.Compression == "GZip";

            // Receive file list
            var fileInfos = await ReceiveJsonAsync<List<TransferFileInfo>>(networkStream, ct);
            if (fileInfos == null) return;

            var gameName = header.GameName ?? "Unknown";
            var destPath = System.IO.Path.Combine(_receiveBasePath, gameName);

            // Request approval from UI before accepting
            // We pass 0 for skipped files initially because we haven't analyzed yet to avoid hanging
            var approvalArgs = new TransferApprovalEventArgs
            {
                GameName = gameName,
                SizeBytes = header.TotalSize,
                FileCount = header.TotalFiles
            };
            
            // Invoke approval event (UI handler should call approvalArgs.SetApproval(true/false))
            TransferApprovalRequested?.Invoke(this, approvalArgs);
            
            // Wait for approval decision from UI thread (with 60 second timeout)
            var approved = await approvalArgs.WaitForApprovalAsync();

            if (!approved)
            {
                LogService.Instance.Info($"Transfer of {gameName} was rejected by user", "TransferService");
                await SendJsonAsync(networkStream, new TransferAck { Accepted = false }, ct);
                return;
            }

            // Now perform analysis for differential update
            // Check for existing files to skip (Smart Sync)
            var skippedFiles = new List<string>();

            if (Directory.Exists(destPath))
            {
                // Try to load local metadata first for fast checking
                Dictionary<string, string> localHashes = new();
                try
                {
                    var metadataPath = System.IO.Path.Combine(destPath, "steamroll.json");
                    if (File.Exists(metadataPath))
                    {
                        var json = await File.ReadAllTextAsync(metadataPath, ct);
                        var metadata = JsonSerializer.Deserialize<Models.PackageMetadata>(json); // Full qual to avoid using ambiguity
                        if (metadata?.FileHashes != null)
                        {
                            localHashes = metadata.FileHashes;
                        }
                    }
                }
                catch { /* Ignore metadata read errors */ }

                foreach (var fileInfo in fileInfos)
                {
                    // Security check: Prevent directory traversal in analysis phase
                    if (!IsPathSafe(fileInfo.RelativePath)) continue;

                    var localPath = System.IO.Path.Combine(destPath, fileInfo.RelativePath);

                    // Double check path containment
                    if (!Path.GetFullPath(localPath).StartsWith(Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase)) continue;

                    if (File.Exists(localPath))
                    {
                        bool matches = false;

                        // Check size first
                        var fi = new FileInfo(localPath);
                        if (fi.Length == fileInfo.Size)
                        {
                            // Check hash if available in metadata
                            if (localHashes.TryGetValue(fileInfo.RelativePath, out var localHash) &&
                                string.Equals(localHash, fileInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                            {
                                matches = true;
                            }
                            // Fallback to checking hash if size matches but no metadata hash (safe but slower)
                            else
                            {
                                try
                                {
                                    // Only hash if we have a hash to compare against
                                    if (!string.IsNullOrEmpty(fileInfo.Sha256))
                                    {
                                        var computedHash = ComputeSha256(localPath);
                                        if (string.Equals(computedHash, fileInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                                        {
                                            matches = true;
                                        }
                                    }
                                }
                                catch { /* If we can't read it, we can't skip it */ }
                            }
                        }

                        if (matches)
                        {
                            skippedFiles.Add(fileInfo.RelativePath);
                        }
                    }

                    // Update analysis progress (avoid UI hang appearance)
                    // We can't update percentage accurately without total files count processed so far,
                    // but we can show activity.
                    // This runs on a thread pool thread, so it won't block UI if marshaled correctly.
                    ProgressChanged?.Invoke(this, new TransferProgress
                    {
                        GameName = gameName,
                        CurrentFile = $"Analyzing local files: {fileInfo.RelativePath}",
                        IsSending = false
                    });
                }
            }
            
            // Send acknowledgment with approval and skipped files
            await SendJsonAsync(networkStream, new TransferAck { Accepted = true, SkippedFiles = skippedFiles }, ct);
            
            // If not approved, return early
            if (!approved)
            {
                LogService.Instance.Info($"Transfer of {gameName} was rejected by user", "TransferService");
                return;
            }

            // Create destination directory
            Directory.CreateDirectory(destPath);
            
            // Check for existing transfer state to resume
            var existingState = TransferState.Load(destPath);
            var fileListHash = TransferState.ComputeFileListHash(fileInfos);
            var completedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long previouslyReceivedBytes = 0;
            
            if (existingState != null && existingState.FileListHash == fileListHash)
            {
                // Valid resume state found - skip already completed files
                foreach (var completed in existingState.CompletedFiles)
                {
                    completedFiles.Add(completed);
                }
                previouslyReceivedBytes = existingState.BytesReceived;
                LogService.Instance.Info($"Resuming transfer for {gameName}: {existingState.FilesCompleted}/{existingState.TotalFiles} files already received", "TransferService");
            }
            else if (existingState != null)
            {
                // File list changed - can't resume, start fresh
                LogService.Instance.Info($"Transfer file list changed for {gameName}, starting fresh transfer", "TransferService");
                TransferState.Delete(destPath);
            }
            
            // Create or update transfer state
            var state = existingState ?? new TransferState
            {
                GameName = gameName,
                TotalFiles = fileInfos.Count,
                TotalSize = header.TotalSize,
                StartedAt = DateTime.UtcNow,
                FileListHash = fileListHash
            };

            // Receive files
            long receivedBytes = previouslyReceivedBytes;
            var buffer = new byte[BUFFER_SIZE];

            // Setup input stream (compressed or raw)
            Stream inputStream = networkStream;
            System.IO.Compression.GZipStream? gzipStream = null;

            if (isCompressed)
            {
                gzipStream = new System.IO.Compression.GZipStream(networkStream, System.IO.Compression.CompressionMode.Decompress, true);
                inputStream = gzipStream;
            }

            // Throttle state saves
            DateTime lastStateSave = DateTime.UtcNow;

            try
            {
                foreach (var fileInfo in fileInfos)
                {
                    // Validate path to prevent directory traversal
                    if (!IsPathSafe(fileInfo.RelativePath))
                    {
                        LogService.Instance.Warning($"Blocked potentially malicious file path: {fileInfo.RelativePath}", "TransferService");

                        // Consume bytes to keep stream in sync but don't write to disk
                        long toSkip = fileInfo.Size;
                        while (toSkip > 0)
                        {
                            var toRead = (int)Math.Min(toSkip, buffer.Length);
                            var bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                            if (bytesRead == 0) break;
                            toSkip -= bytesRead;
                        }
                        continue;
                    }

                    // Check if this file was already completed in a previous transfer attempt
                    if (completedFiles.Contains(fileInfo.RelativePath))
                    {
                        // Skip the bytes in the stream (sender still sends them)
                        // We need to consume them to keep the stream in sync
                        long toSkip = fileInfo.Size;
                        while (toSkip > 0)
                        {
                            var toRead = (int)Math.Min(toSkip, buffer.Length);
                            var bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                            if (bytesRead == 0) break;
                            toSkip -= bytesRead;
                        }

                        // Report progress for skipped file
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
                    
                    var fullPath = System.IO.Path.Combine(destPath, fileInfo.RelativePath);
                    // Ensure the final path is actually within destPath (double check)
                    if (!Path.GetFullPath(fullPath).StartsWith(Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                    {
                        LogService.Instance.Warning($"Blocked path traversal attempt: {fileInfo.RelativePath} -> {fullPath}", "TransferService");
                        // Consume bytes
                        long toSkip = fileInfo.Size;
                        while (toSkip > 0)
                        {
                            var toRead = (int)Math.Min(toSkip, buffer.Length);
                            var bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                            if (bytesRead == 0) break;
                            toSkip -= bytesRead;
                        }
                        continue;
                    }

                    var dir = System.IO.Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    using var fileStream = File.Create(fullPath);
                    long remaining = fileInfo.Size;

                    while (remaining > 0)
                    {
                        var toRead = (int)Math.Min(remaining, buffer.Length);
                        var bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                        if (bytesRead == 0) break;

                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        remaining -= bytesRead;
                        receivedBytes += bytesRead;

                        ProgressChanged?.Invoke(this, new TransferProgress
                        {
                            GameName = gameName,
                            CurrentFile = fileInfo.RelativePath,
                            BytesTransferred = receivedBytes,
                            TotalBytes = header.TotalSize,
                            IsSending = false
                        });
                    }

                    // Verify checksum if provided
                    if (!string.IsNullOrEmpty(fileInfo.Sha256))
                    {
                        if (!VerifySha256(fullPath, fileInfo.Sha256))
                        {
                            LogService.Instance.Error($"Checksum mismatch for {fileInfo.RelativePath}", category: "TransferService");
                            throw new InvalidDataException($"Checksum verification failed for {fileInfo.RelativePath}");
                        }
                    }

                    // Mark file as completed
                    state.FilesCompleted++;
                    state.BytesReceived = receivedBytes;
                    state.CompletedFiles.Add(fileInfo.RelativePath);

                    // Periodically save state (every 5 seconds or when finished)
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
                 // No need to dispose reader stream specifically if it just wraps,
                 // but good practice if we want to ensure any internal buffers are cleared
                 if (gzipStream != null) await gzipStream.DisposeAsync();
            }
            
            // Transfer complete - delete state file
            TransferState.Delete(destPath);

            // Mark as received from network
            await MarkAsReceivedPackage(destPath);
            
            // Verify package integrity using stored hashes in steamroll.json
            bool verificationPassed = true;
            var verificationErrors = new List<string>();
            
            try
            {
                var (isValid, mismatches) = PackageBuilder.VerifyIntegrity(destPath);
                verificationPassed = isValid;
                verificationErrors = mismatches;
                
                if (isValid)
                {
                    LogService.Instance.Info($"Package verification passed for {gameName}", "TransferService");
                }
                else
                {
                    LogService.Instance.Warning($"Package verification failed for {gameName}: {string.Join(", ", mismatches)}", "TransferService");
                }
            }
            catch (Exception verifyEx)
            {
                LogService.Instance.Warning($"Could not verify package {gameName}: {verifyEx.Message}", "TransferService");
                // Don't fail the transfer, just note we couldn't verify
            }

            // Send completion
            await SendJsonAsync(networkStream, new TransferComplete { Success = true }, ct);

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

            LogService.Instance.Info($"Received package: {gameName} ({receivedBytes} bytes, verified: {verificationPassed})", "TransferService");

        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Receive error: {ex.Message}", ex, "TransferService");
            Error?.Invoke(this, $"Receive failed: {ex.Message}");
        }
    }

    private async Task MarkAsReceivedPackage(string packagePath)
    {
        // Create a marker file to indicate this came from another SteamRoll client
        var markerPath = System.IO.Path.Combine(packagePath, ".steamroll_received");
        var markerContent = new ReceivedMarker
        {
            ReceivedAt = DateTime.Now,
            ReceivedFrom = "Network"
        };
        var json = JsonSerializer.Serialize(markerContent, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(markerPath, json);
    }

    private static async Task SendJsonAsync<T>(NetworkStream stream, T obj, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(obj);
        var data = Encoding.UTF8.GetBytes(json);
        var lengthBytes = BitConverter.GetBytes(data.Length);

        await stream.WriteAsync(lengthBytes, ct);
        await stream.WriteAsync(data, ct);
    }

    private static async Task<T?> ReceiveJsonAsync<T>(NetworkStream stream, CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        await ReadExactlyAsync(stream, lengthBytes, ct);
        var length = BitConverter.ToInt32(lengthBytes, 0);

        // Increased limit to 64MB to support large game file lists
        if (length <= 0 || length > 64_000_000) return default; // Sanity check

        var data = new byte[length];
        await ReadExactlyAsync(stream, data, ct);

        var json = Encoding.UTF8.GetString(data);
        return JsonSerializer.Deserialize<T>(json);
    }

    private static bool IsPathSafe(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        // Prevent path traversal explicitly
        // We can't just check for ".." because valid filenames might have it (e.g. "My..File.txt")
        // But we want to block "..\foo" or "../foo" or "foo/../bar"
        if (relativePath.StartsWith(".." + Path.DirectorySeparatorChar) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar) ||
            relativePath.Contains(Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar) ||
            relativePath.Contains(Path.AltDirectorySeparatorChar + ".." + Path.AltDirectorySeparatorChar) ||
            relativePath.EndsWith(Path.DirectorySeparatorChar + "..") ||
            relativePath.EndsWith(Path.AltDirectorySeparatorChar + "..") ||
            relativePath == "..")
        {
            return false;
        }

        // Also check for leading slash which might be interpreted as root
        if (relativePath.StartsWith("/") || relativePath.StartsWith("\\"))
            return false;

        if (Path.IsPathRooted(relativePath)) return false;

        // Check for invalid chars
        var invalidChars = Path.GetInvalidFileNameChars().Where(c => c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar).ToArray();
        if (relativePath.IndexOfAny(invalidChars) >= 0) return false;

        return true;
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (bytesRead == 0)
                throw new EndOfStreamException("Connection closed before receiving all data");
            totalRead += bytesRead;
        }
    }
    
    /// <summary>
    /// Computes SHA256 hash of a file for integrity verification.
    /// </summary>
    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    
    /// <summary>
    /// Verifies a file's SHA256 hash matches the expected value.
    /// </summary>
    private static bool VerifySha256(string filePath, string? expectedHash)
    {
        if (string.IsNullOrEmpty(expectedHash))
            return true; // No hash to verify - assume OK for backward compatibility
            
        try
        {
            var actualHash = ComputeSha256(filePath);
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        StopListening();
        _cts?.Dispose();
    }
}

/// <summary>
/// Simple token bucket bandwidth limiter.
/// </summary>
public class BandwidthLimiter
{
    private readonly long _bytesPerSecond;
    private double _tokens;
    private DateTime _lastUpdate;
    private const double MAX_TOKENS_MULTIPLIER = 1.0; // Max burst = 1 second worth

    public BandwidthLimiter(long bytesPerSecond)
    {
        _bytesPerSecond = bytesPerSecond;
        _tokens = bytesPerSecond; // Start full
        _lastUpdate = DateTime.UtcNow;
    }

    public async Task WaitAsync(int bytes, CancellationToken ct)
    {
        if (_bytesPerSecond <= 0) return; // No limit

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var now = DateTime.UtcNow;
            var elapsed = (now - _lastUpdate).TotalSeconds;
            _lastUpdate = now;

            // Refill tokens
            _tokens += elapsed * _bytesPerSecond;
            var maxTokens = _bytesPerSecond * MAX_TOKENS_MULTIPLIER;
            if (_tokens > maxTokens) _tokens = maxTokens;

            if (_tokens >= bytes)
            {
                _tokens -= bytes;
                return;
            }

            // Not enough tokens, wait
            var deficit = bytes - _tokens;
            var waitTimeSeconds = deficit / _bytesPerSecond;
            var waitTimeMs = (int)(waitTimeSeconds * 1000);

            if (waitTimeMs > 0)
            {
                await Task.Delay(waitTimeMs, ct);
            }
        }
    }
}


// ========================================
// Transfer Protocol Types
// ========================================

public class TransferHeader
{
    public string Magic { get; set; } = "";
    public string? GameName { get; set; }
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public bool IsReceivedPackage { get; set; }

    /// <summary>
    /// Compression mode used for file transfer (e.g., "None", "GZip").
    /// </summary>
    public string Compression { get; set; } = "None";
}

public class TransferFileInfo
{
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
    
    /// <summary>
    /// SHA256 hash of the file for integrity verification.
    /// </summary>
    public string? Sha256 { get; set; }
}


public class TransferAck
{
    public bool Accepted { get; set; }

    /// <summary>
    /// List of relative paths of files that the receiver already has and wants to skip.
    /// </summary>
    public List<string> SkippedFiles { get; set; } = new();
}

public class TransferComplete
{
    public bool Success { get; set; }
}

public class TransferProgress
{
    public string GameName { get; set; } = "";
    public string CurrentFile { get; set; } = "";
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public bool IsSending { get; set; }
    public TimeSpan? EstimatedTimeRemaining { get; set; }

    public double Percentage => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100 : 0;

    public string FormattedProgress
    {
        get
        {
            var transferred = FormatBytes(BytesTransferred);
            var total = FormatBytes(TotalBytes);
            var percentage = Percentage;
            var eta = EstimatedTimeRemaining.HasValue
                ? $" - ETA: {EstimatedTimeRemaining.Value:hh\\:mm\\:ss}"
                : "";
            return $"{transferred} / {total} ({percentage:F1}%){eta}";
        }
    }

    private static string FormatBytes(long bytes) => FormatUtils.FormatBytes(bytes);
}

public class TransferResult
{
    public bool Success { get; set; }
    public string GameName { get; set; } = "";
    public string Path { get; set; } = "";
    public long BytesTransferred { get; set; }
    public bool WasReceived { get; set; }
    
    /// <summary>
    /// Whether the package passed integrity verification after transfer.
    /// </summary>
    public bool VerificationPassed { get; set; } = true;
    
    /// <summary>
    /// List of verification errors if verification failed.
    /// </summary>
    public List<string> VerificationErrors { get; set; } = new();
}

public class ReceivedMarker
{
    public DateTime ReceivedAt { get; set; }
    public string ReceivedFrom { get; set; } = "";
}

/// <summary>
/// Event args for transfer approval requests.
/// Handler should call SetApproval(true/false) to approve or reject the transfer.
/// </summary>
public class TransferApprovalEventArgs : EventArgs
{
    private readonly TaskCompletionSource<bool> _approvalTcs = new();
    
    public string GameName { get; set; } = "";
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    
    /// <summary>
    /// Gets whether the transfer was approved.
    /// </summary>
    public bool Approved { get; private set; }
    
    public string FormattedSize => FormatUtils.FormatBytes(SizeBytes);
    
    /// <summary>
    /// Sets the approval status and signals completion.
    /// Call this from the UI handler to approve or reject the transfer.
    /// </summary>
    /// <param name="approved">True to approve, false to reject.</param>
    public void SetApproval(bool approved)
    {
        Approved = approved;
        _approvalTcs.TrySetResult(approved);
    }
    
    /// <summary>
    /// Waits for the approval decision from the UI thread.
    /// </summary>
    /// <param name="timeout">Timeout for waiting. Defaults to 60 seconds.</param>
    /// <returns>True if approved, false if rejected or timed out.</returns>
    public async Task<bool> WaitForApprovalAsync(TimeSpan? timeout = null)
    {
        var actualTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(actualTimeout);
        
        try
        {
            return await _approvalTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout - reject by default
            return false;
        }
    }
}

/// <summary>
/// Tracks the state of an ongoing or interrupted transfer for resumption.
/// </summary>
public class TransferState
{
    public const string StateFileName = ".steamroll_transfer_state";
    
    /// <summary>
    /// Game name being transferred.
    /// </summary>
    public string GameName { get; set; } = "";
    
    /// <summary>
    /// Total number of files in the transfer.
    /// </summary>
    public int TotalFiles { get; set; }
    
    /// <summary>
    /// Total size in bytes of all files.
    /// </summary>
    public long TotalSize { get; set; }
    
    /// <summary>
    /// Number of files fully received.
    /// </summary>
    public int FilesCompleted { get; set; }
    
    /// <summary>
    /// Total bytes received across all completed files.
    /// </summary>
    public long BytesReceived { get; set; }
    
    /// <summary>
    /// List of file paths that have been fully received (relative paths).
    /// </summary>
    public List<string> CompletedFiles { get; set; } = new();
    
    /// <summary>
    /// When this transfer was started.
    /// </summary>
    public DateTime StartedAt { get; set; }
    
    /// <summary>
    /// When this state was last updated.
    /// </summary>
    public DateTime LastUpdatedAt { get; set; }
    
    /// <summary>
    /// SHA256 hashes of the file list for verification (ensures same transfer).
    /// </summary>
    public string? FileListHash { get; set; }
    
    /// <summary>
    /// Whether this state is expired (older than 24 hours).
    /// </summary>
    public bool IsExpired => (DateTime.UtcNow - LastUpdatedAt).TotalHours > 24;
    
    /// <summary>
    /// Saves transfer state to disk.
    /// </summary>
    public static void Save(string destPath, TransferState state)
    {
        try
        {
            var statePath = System.IO.Path.Combine(destPath, StateFileName);
            state.LastUpdatedAt = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(statePath, json);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to save transfer state: {ex.Message}", "TransferService");
        }
    }
    
    /// <summary>
    /// Loads transfer state from disk if it exists.
    /// </summary>
    public static TransferState? Load(string destPath)
    {
        try
        {
            var statePath = System.IO.Path.Combine(destPath, StateFileName);
            if (!File.Exists(statePath)) return null;
            
            var json = File.ReadAllText(statePath);
            var state = JsonSerializer.Deserialize<TransferState>(json);
            
            // Don't return expired states
            if (state?.IsExpired == true)
            {
                Delete(destPath);
                return null;
            }
            
            return state;
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Deletes transfer state file (called on successful completion).
    /// </summary>
    public static void Delete(string destPath)
    {
        try
        {
            var statePath = System.IO.Path.Combine(destPath, StateFileName);
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
        catch { }
    }
    
    /// <summary>
    /// Computes a hash of the file list to verify we're resuming the same transfer.
    /// </summary>
    public static string ComputeFileListHash(List<TransferFileInfo> files)
    {
        using var sha256 = SHA256.Create();
        var combined = string.Join("|", files.Select(f => $"{f.RelativePath}:{f.Size}:{f.Sha256}"));
        var bytes = Encoding.UTF8.GetBytes(combined);
        return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
    }
}
