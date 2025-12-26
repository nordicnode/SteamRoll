using System.IO;
using System.Net.Sockets;
using System.Text.Json;

namespace SteamRoll.Services.Transfer;

public static class TransferUtils
{
    /// <summary>
    /// Sends a JSON-serialized object with a 4-byte length prefix.
    /// </summary>
    /// <typeparam name="T">The type of object to serialize.</typeparam>
    /// <param name="stream">The stream to write to (typically a NetworkStream).</param>
    /// <param name="obj">The object to serialize and send.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task SendJsonAsync<T>(Stream stream, T obj, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await JsonSerializer.SerializeAsync(ms, obj, cancellationToken: ct);
        var data = ms.ToArray();

        var lengthBytes = BitConverter.GetBytes(data.Length);

        await stream.WriteAsync(lengthBytes, ct);
        await stream.WriteAsync(data, ct);
    }

    public static async Task<T?> ReceiveJsonAsync<T>(Stream stream, CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        await ReadExactlyAsync(stream, lengthBytes, ct);
        var length = BitConverter.ToInt32(lengthBytes, 0);

        if (length <= 0 || length > 128_000_000) return default;

        using var boundedStream = new BoundedStream(stream, length);

        // Add timeout for deserialization to prevent hanging on slow/malicious streams
        // Use a longer timeout (60s) to accommodate larger file lists on slower connections
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

        return await JsonSerializer.DeserializeAsync<T>(boundedStream, cancellationToken: timeoutCts.Token);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
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

    private class BoundedStream : Stream
    {
        private readonly Stream _innerStream;
        private long _remaining;

        public BoundedStream(Stream inner, long length)
        {
            _innerStream = inner;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _innerStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var toRead = (int)Math.Min(count, _remaining);
            var read = _innerStream.Read(buffer, offset, toRead);
            _remaining -= read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
             if (_remaining <= 0) return 0;
             var toRead = (int)Math.Min(count, _remaining);
             var read = await _innerStream.ReadAsync(buffer, offset, toRead, cancellationToken);
             _remaining -= read;
             return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0) return 0;
            var toRead = (int)Math.Min(buffer.Length, _remaining);
            var read = await _innerStream.ReadAsync(buffer.Slice(0, toRead), cancellationToken);
            _remaining -= read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
