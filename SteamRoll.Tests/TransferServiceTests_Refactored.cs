using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SteamRoll.Services;
using Xunit;

namespace SteamRoll.Tests;

public class TransferServiceTests_Refactored
{
    private const string TEST_RECEIVE_DIR = "TestReceive";
    private const string TEST_PACKAGE_DIR = "TestPackage";

    public TransferServiceTests_Refactored()
    {
        // Cleanup
        if (Directory.Exists(TEST_RECEIVE_DIR)) Directory.Delete(TEST_RECEIVE_DIR, true);
        if (Directory.Exists(TEST_PACKAGE_DIR)) Directory.Delete(TEST_PACKAGE_DIR, true);

        Directory.CreateDirectory(TEST_RECEIVE_DIR);
        Directory.CreateDirectory(TEST_PACKAGE_DIR);

        // Create dummy package
        File.WriteAllText(Path.Combine(TEST_PACKAGE_DIR, "test.txt"), "This is a test file content.");
        File.WriteAllText(Path.Combine(TEST_PACKAGE_DIR, "large.bin"), new string('A', 10000));
        Directory.CreateDirectory(Path.Combine(TEST_PACKAGE_DIR, "subdir"));
        File.WriteAllText(Path.Combine(TEST_PACKAGE_DIR, "subdir", "sub.txt"), "Subdirectory file.");
    }

    [Fact]
    public async Task TestTransferLoopback()
    {
        // Setup
        int port = 27099;
        using var receiver = new TransferService(TEST_RECEIVE_DIR);
        using var sender = new TransferService(TEST_PACKAGE_DIR); // Path doesn't matter for sender

        // Auto-approve transfers for test
        receiver.TransferApprovalRequested += (s, e) => e.SetApproval(true);

        var tcs = new TaskCompletionSource<bool>();
        receiver.TransferComplete += (s, e) =>
        {
            if (e.Success)
                tcs.TrySetResult(true);
            else
                tcs.TrySetException(new Exception("Transfer reported failure"));
        };

        // Act
        Assert.True(receiver.StartListening(port));

        var senderTask = sender.SendPackageAsync("127.0.0.1", port, TEST_PACKAGE_DIR);

        // Wait for both sender and receiver to complete
        // We use WhenAll to catch exceptions from either side
        var completedTask = await Task.WhenAny(senderTask, tcs.Task, Task.Delay(5000));

        if (completedTask == Task.Delay(5000))
        {
            throw new TimeoutException("Transfer timed out");
        }

        Assert.True(await senderTask, "Sender reported failure");
        Assert.True(await tcs.Task, "Receiver did not complete successfully");

        // Verify files
        var receivedDir = Path.Combine(TEST_RECEIVE_DIR, "TestPackage");
        Assert.True(Directory.Exists(receivedDir));
        Assert.True(File.Exists(Path.Combine(receivedDir, "test.txt")));
        Assert.True(File.Exists(Path.Combine(receivedDir, "large.bin")));
        Assert.True(File.Exists(Path.Combine(receivedDir, "subdir", "sub.txt")));

        Assert.Equal("This is a test file content.", File.ReadAllText(Path.Combine(receivedDir, "test.txt")));
    }
}
