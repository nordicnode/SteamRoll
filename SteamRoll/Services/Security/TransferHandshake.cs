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

            // Send: [nonce][ciphertext][tag]
            await stream.WriteAsync(nonce, ct);
            await stream.WriteAsync(ciphertext, ct);
            await stream.WriteAsync(tag, ct);
            await stream.FlushAsync(ct);

            // Read response: [nonce][encrypted response][tag]
            var responseNonce = new byte[NONCE_SIZE];
            var responseRead = await ReadExactAsync(stream, responseNonce, ct);
            if (responseRead < NONCE_SIZE)
                return new HandshakeResult { Success = false, ErrorMessage = "Failed to read response nonce" };

            // Response should contain: remoteDeviceId + reversed challenge
            var expectedResponseLen = 64 + CHALLENGE_SIZE; // Max device ID + challenge
            var responseCiphertext = new byte[expectedResponseLen];
            var responseTag = new byte[TAG_SIZE];

            var cipherRead = await ReadExactAsync(stream, responseCiphertext, ct);
            var tagRead = await ReadExactAsync(stream, responseTag, ct);

            if (tagRead < TAG_SIZE)
                return new HandshakeResult { Success = false, ErrorMessage = "Failed to read response" };

            // Decrypt response
            var responsePlaintext = new byte[cipherRead];
            try
            {
                using var aes = new AesGcm(sharedKey, TAG_SIZE);
                var cipherSlice = new byte[cipherRead];
                Array.Copy(responseCiphertext, 0, cipherSlice, 0, cipherRead);
                aes.Decrypt(responseNonce, cipherSlice, responseTag, responsePlaintext);
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
            // Read challenge: [nonce][ciphertext][tag]
            var nonce = new byte[NONCE_SIZE];
            var nonceRead = await ReadExactAsync(stream, nonce, ct);
            if (nonceRead < NONCE_SIZE)
                return new HandshakeResult { Success = false, ErrorMessage = "Failed to read challenge nonce" };

            // Read ciphertext (prefix + device ID + challenge)
            var maxCiphertextLen = 64 + CHALLENGE_SIZE + CHALLENGE_PREFIX.Length;
            var ciphertext = new byte[maxCiphertextLen];
            var tag = new byte[TAG_SIZE];

            var cipherRead = await ReadExactAsync(stream, ciphertext, ct);
            var tagRead = await ReadExactAsync(stream, tag, ct);

            if (tagRead < TAG_SIZE)
                return new HandshakeResult { Success = false, ErrorMessage = "Failed to read challenge" };

            // Decrypt challenge
            var plaintext = new byte[cipherRead];
            try
            {
                using var aes = new AesGcm(sharedKey, TAG_SIZE);
                var cipherSlice = new byte[cipherRead];
                Array.Copy(ciphertext, 0, cipherSlice, 0, cipherRead);
                aes.Decrypt(nonce, cipherSlice, tag, plaintext);
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

            // Send response
            await stream.WriteAsync(responseNonce, ct);
            await stream.WriteAsync(responseCiphertext, ct);
            await stream.WriteAsync(responseTag, ct);
            await stream.FlushAsync(ct);

            return new HandshakeResult { Success = true, RemoteDeviceId = remoteDeviceId };
        }
        catch (Exception ex)
        {
            return new HandshakeResult { Success = false, ErrorMessage = ex.Message };
        }
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
