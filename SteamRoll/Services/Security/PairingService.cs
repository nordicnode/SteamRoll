using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamRoll.Services.Security;

/// <summary>
/// Handles device pairing and cryptographic key management for encrypted transfers.
/// Uses a 6-digit pairing code to derive a shared secret between devices.
/// </summary>
public class PairingService
{
    private const int PAIRING_CODE_LENGTH = 6;
    private const int KEY_SIZE_BYTES = 32; // 256-bit key
    private const int PBKDF2_ITERATIONS = 100_000;
    private const string PAIRING_SALT_PREFIX = "STEAMROLL_PAIR_V1_";

    private readonly string _storagePath;
    private Dictionary<string, PairedDevice> _pairedDevices = new();

    public PairingService()
    {
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamRoll", "paired_devices.json");
        LoadPairedDevices();
    }

    /// <summary>
    /// Generates a random 6-digit pairing code.
    /// </summary>
    public string GeneratePairingCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(4);
        var number = BitConverter.ToUInt32(bytes) % 1_000_000;
        return number.ToString("D6");
    }

    /// <summary>
    /// Derives a 256-bit encryption key from a pairing code and device identifiers.
    /// Uses PBKDF2 with 100k iterations for key stretching.
    /// </summary>
    public byte[] DeriveKey(string pairingCode, string localDeviceId, string remoteDeviceId)
    {
        // Create deterministic salt from device IDs (sorted to ensure same key on both ends)
        var orderedIds = new[] { localDeviceId, remoteDeviceId }.OrderBy(x => x).ToArray();
        var salt = Encoding.UTF8.GetBytes($"{PAIRING_SALT_PREFIX}{orderedIds[0]}_{orderedIds[1]}");

        using var pbkdf2 = new Rfc2898DeriveBytes(
            pairingCode,
            salt,
            PBKDF2_ITERATIONS,
            HashAlgorithmName.SHA256);

        return pbkdf2.GetBytes(KEY_SIZE_BYTES);
    }

    /// <summary>
    /// Saves a paired device with its encryption key.
    /// </summary>
    public void SavePairedDevice(string deviceId, string deviceName, byte[] key)
    {
        _pairedDevices[deviceId] = new PairedDevice
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            EncryptedKey = ProtectKey(key),
            PairedAt = DateTime.UtcNow
        };
        SavePairedDevices();
        LogService.Instance.Info($"Paired with device: {deviceName} ({deviceId})", "PairingService");
    }

    /// <summary>
    /// Gets the encryption key for a paired device.
    /// </summary>
    public byte[]? GetPairedKey(string deviceId)
    {
        if (_pairedDevices.TryGetValue(deviceId, out var device))
        {
            return UnprotectKey(device.EncryptedKey);
        }
        return null;
    }

    /// <summary>
    /// Checks if a device is paired.
    /// </summary>
    public bool IsPaired(string deviceId) => _pairedDevices.ContainsKey(deviceId);

    /// <summary>
    /// Gets all paired devices.
    /// </summary>
    public IReadOnlyList<PairedDevice> GetPairedDevices() => _pairedDevices.Values.ToList();

    /// <summary>
    /// Removes a paired device.
    /// </summary>
    public void UnpairDevice(string deviceId)
    {
        if (_pairedDevices.Remove(deviceId))
        {
            SavePairedDevices();
            LogService.Instance.Info($"Unpaired device: {deviceId}", "PairingService");
        }
    }

    /// <summary>
    /// Encrypts a key for secure storage using DPAPI (Windows) or obfuscation (other platforms).
    /// </summary>
    private string ProtectKey(byte[] key)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Use DPAPI on Windows for secure key storage
                var protectedBytes = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
                return "DPAPI:" + Convert.ToBase64String(protectedBytes);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning($"DPAPI protection failed: {ex.Message}", "PairingService");
            }
        }
        else
        {
            // Log a clear warning about security limitations on non-Windows
            LogService.Instance.Warning(
                "Key storage: DPAPI unavailable on this platform. " +
                "Encryption keys will be stored with basic obfuscation only. " +
                "For maximum security, use Windows or limit file system access.", 
                "PairingService");
        }
        
        // Fallback: XOR obfuscation (not cryptographically secure, but better than plain Base64)
        var obfuscated = new byte[key.Length];
        byte obfuscationKey = 0x5A;
        for (int i = 0; i < key.Length; i++)
            obfuscated[i] = (byte)(key[i] ^ obfuscationKey);
        return "OBF:" + Convert.ToBase64String(obfuscated);
    }

    /// <summary>
    /// Decrypts a stored key.
    /// </summary>
    private byte[] UnprotectKey(string protectedKey)
    {
        if (protectedKey.StartsWith("DPAPI:") && OperatingSystem.IsWindows())
        {
            try
            {
                var protectedBytes = Convert.FromBase64String(protectedKey.Substring(6));
                return ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning($"DPAPI unprotection failed: {ex.Message}", "PairingService");
            }
        }
        
        if (protectedKey.StartsWith("OBF:"))
        {
            // De-obfuscate XOR encoded key
            var obfuscated = Convert.FromBase64String(protectedKey.Substring(4));
            var key = new byte[obfuscated.Length];
            byte obfuscationKey = 0x5A;
            for (int i = 0; i < obfuscated.Length; i++)
                key[i] = (byte)(obfuscated[i] ^ obfuscationKey);
            return key;
        }
        
        // Legacy fallback for old plain Base64 keys
        return Convert.FromBase64String(protectedKey);
    }

    private void LoadPairedDevices()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                _pairedDevices = JsonSerializer.Deserialize<Dictionary<string, PairedDevice>>(json) ?? new();
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to load paired devices: {ex.Message}", "PairingService");
            _pairedDevices = new();
        }
    }

    private void SavePairedDevices()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
            var json = JsonSerializer.Serialize(_pairedDevices, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to save paired devices: {ex.Message}", ex, "PairingService");
        }
    }
}

/// <summary>
/// Represents a paired device.
/// </summary>
public class PairedDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string EncryptedKey { get; set; } = string.Empty;
    public DateTime PairedAt { get; set; }
}
