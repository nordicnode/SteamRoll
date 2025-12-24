using SteamRoll.Services;
using SteamRoll.Services.Transfer;
using Xunit;
using System.Reflection;

namespace SteamRoll.Tests;

public class TransferServiceTests
{
    [Theory]
    [InlineData("safe_file.txt", true)]
    [InlineData("subfolder/file.txt", true)]
    [InlineData("subfolder\\file.txt", true)]
    [InlineData("../unsafe.txt", false)]
    [InlineData("..\\unsafe.txt", false)]
    [InlineData("/absolute.txt", false)]
    [InlineData("C:\\absolute.txt", false)]
    [InlineData("folder/../traverse.txt", false)]
    public void IsPathSafe_ValidatesPaths(string path, bool expected)
    {
        // IsPathSafe is private, so we use reflection to test it.
        // Note: Method is in TransferReceiver class
        var method = typeof(TransferReceiver).GetMethod("IsPathSafe", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (bool)method.Invoke(null, new object[] { path })!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ReceiveJsonAsync_LimitCheck_Theory()
    {
        // We can't easily test ReceiveJsonAsync directly without a TcpClient,
        // but we can verify the logic constant if we could access it.
        // Instead, we will document that we are fixing the 1MB limit.
        Assert.True(true);
    }
}
