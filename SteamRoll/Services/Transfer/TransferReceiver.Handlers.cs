using System.IO;
using System.IO.Hashing;
using System.Net.Sockets;
using System.Text.Json;

namespace SteamRoll.Services.Transfer;

/// <summary>
/// Auxiliary handlers for TransferReceiver.
/// Contains handlers for save sync, library list, pull requests, and speed tests.
/// </summary>
public partial class TransferReceiver
{
    /// <summary>
    /// Handles incoming save sync transfer from a peer.
    /// Receives save data as a zip file and notifies caller.
    /// </summary>
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

    /// <summary>
    /// Handles library list request from a peer.
    /// Returns the local library of packaged games.
    /// </summary>
    private async Task HandleListRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var games = LocalLibraryRequested?.Invoke() ?? new List<Models.RemoteGame>();
        await TransferUtils.SendJsonAsync(stream, games, ct);
    }

    /// <summary>
    /// Handles pull request from a peer.
    /// Triggers sending a package to the requesting peer.
    /// </summary>
    private async Task HandlePullRequestAsync(TcpClient client, TransferHeader header, CancellationToken ct)
    {
        var remoteEndpoint = client.Client.RemoteEndPoint as System.Net.IPEndPoint;
        if (remoteEndpoint == null) return;

        var targetIp = remoteEndpoint.Address.ToString();
        var targetPort = AppConstants.DEFAULT_TRANSFER_PORT;

        if (PullPackageRequested != null && header.GameName != null)
        {
             _ = PullPackageRequested.Invoke(header.GameName, targetIp, targetPort);
        }
    }

    /// <summary>
    /// Handles speed test request from a peer.
    /// Receives test data and acknowledges completion.
    /// </summary>
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

    /// <summary>
    /// Marks a package directory as received from network.
    /// Creates a marker file with transfer metadata.
    /// </summary>
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
    /// Consumes and discards bytes from a stream.
    /// Used to skip over data that won't be processed.
    /// </summary>
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

    /// <summary>
    /// Validates that a relative path is safe and doesn't escape the target directory.
    /// Prevents path traversal attacks.
    /// </summary>
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
