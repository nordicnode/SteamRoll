using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SteamRoll.Services.Security;

/// <summary>
/// Handles the cryptographic handshake for establishing encrypted connections.
/// Verifies both parties have the correct shared key before transferring data.
/// </summary>
public static class TransferHandshake
{
    private const string CHALLENGE_PREFIX = "STEAMROLL_AUTH_V1_";
    private const int CHALLENGE_SIZE = 32;
    private const int NONCE_SIZE = 12;
    private const int TAG_SIZE = 16;

    /// <summary>
    /// Result of a handshake attempt.
    /// </summary>
    public class HandshakeResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? RemoteDeviceId { get; set; }
    }

    /// <summary>
    /// Performs the sender-side handshake (initiator).
    /// Sends a challenge and verifies the response.
    /// </summary>
    public static async Task<HandshakeResult> InitiateHandshakeAsync(
        Stream stream,
        byte[] sharedKey,
        string localDeviceId,
        CancellationToken ct = default)
    {
        try
        {
            // Generate random challenge
            var challenge = RandomNumberGenerator.GetBytes(CHALLENGE_SIZE);
            var nonce = RandomNumberGenerator.GetBytes(NONCE_SIZE);

            // Prepare challenge message: localDeviceId + challenge
            var message = Encoding.UTF8.GetBytes($"{CHALLENGE_PREFIX}{localDeviceId}");
            var fullMessage = new byte[message.Length + challenge.Length];
            message.CopyTo(fullMessage, 0);
            challenge.CopyTo(fullMessage, message.Length);

            // Encrypt challenge
            var ciphertext = new byte[fullMessage.Length];
            var tag = new byte[TAG_SIZE];
            using (var aes = new AesGcm(sharedKey, TAG_SIZE))
            {
                aes.Encrypt(nonce, fullMessage, ciphertext, tag);
            }

            // Build framed message: [nonce][ciphertext][tag]
            var framedData = new byte[NONCE_SIZE + ciphertext.Length + TAG_SIZE];
            nonce.CopyTo(framedData, 0);
            ciphertext.CopyTo(framedData, NONCE_SIZE);
            tag.CopyTo(framedData, NONCE_SIZE + ciphertext.Length);
            
            // Send with length prefix
            await WriteFramedAsync(stream, framedData, ct);
            await stream.FlushAsync(ct);

            // Read framed response
            var responseData = await ReadFramedAsync(stream, ct);
            if (responseData == null || responseData.Length < NONCE_SIZE + TAG_SIZE)
                return new HandshakeResult { Success = false, ErrorMessage = "Failed to read response" };

            // Parse response: [nonce][ciphertext][tag]
            var responseNonce = responseData.AsSpan(0, NONCE_SIZE).ToArray();
            var responseCiphertextLen = responseData.Length - NONCE_SIZE - TAG_SIZE;
            var responseCiphertext = responseData.AsSpan(NONCE_SIZE, responseCiphertextLen).ToArray();
            var responseTag = responseData.AsSpan(NONCE_SIZE + responseCiphertextLen, TAG_SIZE).ToArray();

            // Decrypt response
            var responsePlaintext = new byte[responseCiphertextLen];
            try
            {
                using var aes = new AesGcm(sharedKey, TAG_SIZE);
                aes.Decrypt(responseNonce, responseCiphertext, responseTag, responsePlaintext);
            }
            catch (CryptographicException)
            {
                return new HandshakeResult { Success = false, ErrorMessage = "Authentication failed - wrong key" };
            }

            // Parse response
            var responseStr = Encoding.UTF8.GetString(responsePlaintext);
            if (!responseStr.StartsWith(CHALLENGE_PREFIX))
                return new HandshakeResult { Success = false, ErrorMessage = "Invalid response format" };

            var remoteDeviceId = responseStr.Substring(CHALLENGE_PREFIX.Length, 
                responseStr.Length - CHALLENGE_PREFIX.Length - CHALLENGE_SIZE);

            // Verify challenge response (should be reversed challenge)
            var receivedChallenge = new byte[CHALLENGE_SIZE];
            Array.Copy(responsePlaintext, responsePlaintext.Length - CHALLENGE_SIZE, receivedChallenge, 0, CHALLENGE_SIZE);
            var expectedReversed = challenge.Reverse().ToArray();
            if (!receivedChallenge.SequenceEqual(expectedReversed))
                return new HandshakeResult { Success = false, ErrorMessage = "Challenge verification failed" };

            return new HandshakeResult { Success = true, RemoteDeviceId = remoteDeviceId };
        }
        catch (Exception ex)
        {
            return new HandshakeResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Performs the receiver-side handshake (responder).
    /// Decrypts challenge and sends response.
    /// </summary>
    public static async Task<HandshakeResult> RespondToHandshakeAsync(
        Stream stream,
        byte[] sharedKey,
        string localDeviceId,
        CancellationToken ct = default)
    {
        try
        {
            // Read framed challenge
            var challengeData = await ReadFramedAsync(stream, ct);
            if (challengeData == null || challengeData.Length < NONCE_SIZE + TAG_SIZE)
                return new HandshakeResult { Success = false, ErrorMessage = "Failed to read challenge" };

            // Parse challenge: [nonce][ciphertext][tag]
            var nonce = challengeData.AsSpan(0, NONCE_SIZE).ToArray();
            var ciphertextLen = challengeData.Length - NONCE_SIZE - TAG_SIZE;
            var ciphertext = challengeData.AsSpan(NONCE_SIZE, ciphertextLen).ToArray();
            var tag = challengeData.AsSpan(NONCE_SIZE + ciphertextLen, TAG_SIZE).ToArray();

            // Decrypt challenge
            var plaintext = new byte[ciphertextLen];
            try
            {
                using var aes = new AesGcm(sharedKey, TAG_SIZE);
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }
            catch (CryptographicException)
            {
                return new HandshakeResult { Success = false, ErrorMessage = "Authentication failed - wrong key" };
            }

            // Parse challenge
            var challengeStr = Encoding.UTF8.GetString(plaintext, 0, plaintext.Length - CHALLENGE_SIZE);
            if (!challengeStr.StartsWith(CHALLENGE_PREFIX))
                return new HandshakeResult { Success = false, ErrorMessage = "Invalid challenge format" };

            var remoteDeviceId = challengeStr.Substring(CHALLENGE_PREFIX.Length);
            var challenge = new byte[CHALLENGE_SIZE];
            Array.Copy(plaintext, plaintext.Length - CHALLENGE_SIZE, challenge, 0, CHALLENGE_SIZE);

            // Create response: localDeviceId + reversed challenge
            var responseNonce = RandomNumberGenerator.GetBytes(NONCE_SIZE);
            var responseMessage = Encoding.UTF8.GetBytes($"{CHALLENGE_PREFIX}{localDeviceId}");
            var reversedChallenge = challenge.Reverse().ToArray();
            var fullResponse = new byte[responseMessage.Length + reversedChallenge.Length];
            responseMessage.CopyTo(fullResponse, 0);
            reversedChallenge.CopyTo(fullResponse, responseMessage.Length);

            // Encrypt response
            var responseCiphertext = new byte[fullResponse.Length];
            var responseTag = new byte[TAG_SIZE];
            using (var aes = new AesGcm(sharedKey, TAG_SIZE))
            {
                aes.Encrypt(responseNonce, fullResponse, responseCiphertext, responseTag);
            }

            // Build framed response: [nonce][ciphertext][tag]
            var framedResponse = new byte[NONCE_SIZE + responseCiphertext.Length + TAG_SIZE];
            responseNonce.CopyTo(framedResponse, 0);
            responseCiphertext.CopyTo(framedResponse, NONCE_SIZE);
            responseTag.CopyTo(framedResponse, NONCE_SIZE + responseCiphertext.Length);

            // Send with length prefix
            await WriteFramedAsync(stream, framedResponse, ct);
            await stream.FlushAsync(ct);

            return new HandshakeResult { Success = true, RemoteDeviceId = remoteDeviceId };
        }
        catch (Exception ex)
        {
            return new HandshakeResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Writes data with a 4-byte length prefix to prevent deadlock from variable-length messages.
    /// </summary>
    private static async Task WriteFramedAsync(Stream stream, byte[] data, CancellationToken ct)
    {
        var length = BitConverter.GetBytes(data.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(data, ct);
    }

    /// <summary>
    /// Reads length-prefixed data. Returns null if stream ends or data is invalid.
    /// </summary>
    private static async Task<byte[]?> ReadFramedAsync(Stream stream, CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        if (await ReadExactAsync(stream, lengthBytes, ct) < 4)
            return null;
        
        var length = BitConverter.ToInt32(lengthBytes);
        
        // Sanity check: handshake messages should be small (<1MB)
        if (length < 0 || length > 1024 * 1024)
            return null;
        
        var buffer = new byte[length];
        if (await ReadExactAsync(stream, buffer, ct) < length)
            return null;
        
        return buffer;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }
}
