using System.Buffers;
using System.IO;
using System.Security.Cryptography;

namespace SteamRoll.Services.Security;

/// <summary>
/// Provides AES-GCM encryption for network streams.
/// Each message is framed as: [4-byte length][12-byte nonce][ciphertext][16-byte auth tag]
/// </summary>
public class EncryptedStream : Stream
{
    private const int NONCE_SIZE = 12;  // AES-GCM standard
    private const int TAG_SIZE = 16;    // AES-GCM standard
    private const int LENGTH_SIZE = 4;
    private const int MAX_CHUNK_SIZE = 64 * 1024; // 64KB chunks for streaming

    private readonly Stream _innerStream;
    private readonly byte[] _key;
    private readonly bool _leaveOpen;

    // Read buffer for decrypted data
    private byte[] _readBuffer = Array.Empty<byte>();
    private int _readBufferPos;
    private int _readBufferLen;

    public EncryptedStream(Stream innerStream, byte[] key, bool leaveOpen = false)
    {
        if (key.Length != 32)
            throw new ArgumentException("Key must be 256 bits (32 bytes)", nameof(key));

        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _key = key;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Writes data to the stream, encrypting it with AES-GCM.
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Process in chunks to limit memory usage
        var remaining = buffer;
        while (remaining.Length > 0)
        {
            var chunkSize = Math.Min(remaining.Length, MAX_CHUNK_SIZE);
            var chunk = remaining.Slice(0, chunkSize);
            remaining = remaining.Slice(chunkSize);

            await WriteEncryptedChunkAsync(chunk, cancellationToken);
        }
    }

    private async ValueTask WriteEncryptedChunkAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct)
    {
        // Generate random nonce
        var nonce = RandomNumberGenerator.GetBytes(NONCE_SIZE);

        // Rent buffers from pool to reduce GC pressure during large transfers
        var ciphertext = ArrayPool<byte>.Shared.Rent(plaintext.Length);
        var tag = ArrayPool<byte>.Shared.Rent(TAG_SIZE);
        try
        {
            // Encrypt with AES-GCM
            using var aes = new AesGcm(_key, TAG_SIZE);
            aes.Encrypt(nonce, plaintext.Span, ciphertext.AsSpan(0, plaintext.Length), tag.AsSpan(0, TAG_SIZE));

            // Write framed message: [length][nonce][ciphertext][tag]
            var totalLength = NONCE_SIZE + plaintext.Length + TAG_SIZE;
            var lengthBytes = BitConverter.GetBytes(totalLength);

            await _innerStream.WriteAsync(lengthBytes, ct);
            await _innerStream.WriteAsync(nonce, ct);
            await _innerStream.WriteAsync(ciphertext.AsMemory(0, plaintext.Length), ct);
            await _innerStream.WriteAsync(tag.AsMemory(0, TAG_SIZE), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(ciphertext);
            ArrayPool<byte>.Shared.Return(tag);
        }
    }

    /// <summary>
    /// Reads and decrypts data from the stream.
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Return from buffer if we have data
        if (_readBufferPos < _readBufferLen)
        {
            var toCopy = Math.Min(buffer.Length, _readBufferLen - _readBufferPos);
            _readBuffer.AsSpan(_readBufferPos, toCopy).CopyTo(buffer.Span);
            _readBufferPos += toCopy;
            return toCopy;
        }

        // Read next encrypted chunk
        var plaintext = await ReadDecryptedChunkAsync(cancellationToken);
        if (plaintext == null || plaintext.Length == 0)
            return 0;

        // Copy what fits to output buffer
        var copyLen = Math.Min(buffer.Length, plaintext.Length);
        plaintext.AsSpan(0, copyLen).CopyTo(buffer.Span);

        // Store remainder in read buffer
        if (plaintext.Length > copyLen)
        {
            _readBuffer = plaintext;
            _readBufferPos = copyLen;
            _readBufferLen = plaintext.Length;
        }
        else
        {
            _readBufferPos = 0;
            _readBufferLen = 0;
        }

        return copyLen;
    }

    private async Task<byte[]?> ReadDecryptedChunkAsync(CancellationToken ct)
    {
        // Read length header
        var lengthBytes = new byte[LENGTH_SIZE];
        var lengthRead = await ReadExactAsync(_innerStream, lengthBytes, ct);
        if (lengthRead < LENGTH_SIZE)
            return null; // End of stream

        var totalLength = BitConverter.ToInt32(lengthBytes);
        if (totalLength < NONCE_SIZE + TAG_SIZE || totalLength > MAX_CHUNK_SIZE + NONCE_SIZE + TAG_SIZE)
            throw new CryptographicException("Invalid encrypted chunk length");

        // Rent buffer from pool to reduce GC pressure
        var encryptedData = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            var dataRead = await ReadExactAsync(_innerStream, encryptedData.AsMemory(0, totalLength), ct);
            if (dataRead < totalLength)
                throw new CryptographicException("Unexpected end of encrypted stream");

            // Decrypt in sync method to avoid Span-in-async issue (C# 12 limitation)
            return DecryptChunk(encryptedData, totalLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encryptedData);
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.Slice(totalRead), ct);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    private byte[] DecryptChunk(byte[] encryptedData, int totalLength)
    {
        // Parse components
        var nonce = encryptedData.AsSpan(0, NONCE_SIZE);
        var ciphertextLength = totalLength - NONCE_SIZE - TAG_SIZE;
        var ciphertext = encryptedData.AsSpan(NONCE_SIZE, ciphertextLength);
        var tag = encryptedData.AsSpan(NONCE_SIZE + ciphertextLength, TAG_SIZE);

        // Decrypt
        var plaintext = new byte[ciphertextLength];
        using var aes = new AesGcm(_key, TAG_SIZE);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
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

    public override void Flush()
    {
        _innerStream.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _innerStream.FlushAsync(cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _innerStream.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _innerStream.DisposeAsync();
        }
        await base.DisposeAsync();
    }
}
