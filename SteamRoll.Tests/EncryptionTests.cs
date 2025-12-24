using System.IO;
using System.Security.Cryptography;
using SteamRoll.Services.Security;

namespace SteamRoll.Tests;

/// <summary>
/// Tests for the PSK encryption system.
/// </summary>
public class EncryptionTests
{
    [Fact]
    public void PairingService_GenerateCode_Returns6Digits()
    {
        var service = new PairingService();
        var code = service.GeneratePairingCode();
        
        Assert.Equal(6, code.Length);
        Assert.True(int.TryParse(code, out _));
    }

    [Fact]
    public void PairingService_DeriveKey_IsConsistent()
    {
        var service = new PairingService();
        var code = "123456";
        var device1 = "DEVICE_A";
        var device2 = "DEVICE_B";

        var key1 = service.DeriveKey(code, device1, device2);
        var key2 = service.DeriveKey(code, device1, device2);
        
        // Same inputs should produce same key
        Assert.Equal(key1, key2);
        Assert.Equal(32, key1.Length); // 256-bit key
    }

    [Fact]
    public void PairingService_DeriveKey_OrderIndependent()
    {
        var service = new PairingService();
        var code = "123456";
        var device1 = "DEVICE_A";
        var device2 = "DEVICE_B";

        // Device order shouldn't matter - both ends get same key
        var keyAB = service.DeriveKey(code, device1, device2);
        var keyBA = service.DeriveKey(code, device2, device1);
        
        Assert.Equal(keyAB, keyBA);
    }

    [Fact]
    public void PairingService_DeriveKey_DifferentCodesDifferentKeys()
    {
        var service = new PairingService();
        var device1 = "DEVICE_A";
        var device2 = "DEVICE_B";

        var key1 = service.DeriveKey("123456", device1, device2);
        var key2 = service.DeriveKey("654321", device1, device2);
        
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public async Task EncryptedStream_Roundtrip_SmallData()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var originalData = "Hello, encrypted world!"u8.ToArray();

        using var memoryStream = new MemoryStream();
        
        // Write encrypted data
        using (var encryptedWrite = new EncryptedStream(memoryStream, key, leaveOpen: true))
        {
            await encryptedWrite.WriteAsync(originalData);
        }

        // Read it back
        memoryStream.Position = 0;
        using var encryptedRead = new EncryptedStream(memoryStream, key);
        
        var buffer = new byte[originalData.Length];
        var bytesRead = await encryptedRead.ReadAsync(buffer);

        Assert.Equal(originalData.Length, bytesRead);
        Assert.Equal(originalData, buffer);
    }

    [Fact]
    public async Task EncryptedStream_Roundtrip_LargeData()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var originalData = new byte[256 * 1024]; // 256KB
        RandomNumberGenerator.Fill(originalData);

        using var memoryStream = new MemoryStream();
        
        // Write encrypted data
        using (var encryptedWrite = new EncryptedStream(memoryStream, key, leaveOpen: true))
        {
            await encryptedWrite.WriteAsync(originalData);
        }

        // Read it back
        memoryStream.Position = 0;
        using var encryptedRead = new EncryptedStream(memoryStream, key);
        
        var buffer = new byte[originalData.Length];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await encryptedRead.ReadAsync(buffer.AsMemory(totalRead));
            if (read == 0) break;
            totalRead += read;
        }

        Assert.Equal(originalData.Length, totalRead);
        Assert.Equal(originalData, buffer);
    }

    [Fact]
    public async Task EncryptedStream_WrongKey_FailsToDecrypt()
    {
        var key1 = RandomNumberGenerator.GetBytes(32);
        var key2 = RandomNumberGenerator.GetBytes(32);
        var data = "Secret data"u8.ToArray();

        using var memoryStream = new MemoryStream();
        
        // Write with key1
        using (var encryptedWrite = new EncryptedStream(memoryStream, key1, leaveOpen: true))
        {
            await encryptedWrite.WriteAsync(data);
        }

        // Try to read with key2 - should throw
        memoryStream.Position = 0;
        using var encryptedRead = new EncryptedStream(memoryStream, key2);
        
        var buffer = new byte[data.Length];
        var exceptionThrown = false;
        try
        {
            await encryptedRead.ReadAsync(buffer);
        }
        catch (CryptographicException)
        {
            exceptionThrown = true;
        }
        
        Assert.True(exceptionThrown, "Expected CryptographicException when using wrong key");
    }

    [Fact]
    public void PairingService_KeyDerivation_IsSecure()
    {
        // Verify key derivation produces valid keys
        var service = new PairingService();
        var key = service.DeriveKey("123456", "DEVICE_A", "DEVICE_B");
        
        Assert.Equal(32, key.Length);
        Assert.False(key.All(b => b == 0)); // Not all zeros
        Assert.False(key.All(b => b == key[0])); // Not all same
    }

    [Fact]
    public void EncryptedStream_RequiresCorrectKeyLength()
    {
        var shortKey = new byte[16]; // Too short
        var stream = new MemoryStream();
        
        Assert.Throws<ArgumentException>(() => new EncryptedStream(stream, shortKey));
    }
}
