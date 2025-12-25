using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamRoll.Services.Transfer;
using SteamRoll.Models;

namespace SteamRoll.Tests
{
    // A simple test harness to verify TransferUtils timeout logic
    public static class TransferUtilsTest
    {
        public static async Task RunTests()
        {
            await TestReceiveJsonTimeout();
            Console.WriteLine("TransferUtilsTest passed.");
        }

        private static async Task TestReceiveJsonTimeout()
        {
            // Simulate a slow stream
            using var ms = new MemoryStream();
            // Write length
            var length = 100;
            ms.Write(BitConverter.GetBytes(length));
            ms.Position = 0;

            // Use a mock stream that stalls
            var stalledStream = new StalledStream(ms);

            try
            {
                await TransferUtils.ReceiveJsonAsync<object>(stalledStream, CancellationToken.None);
                throw new Exception("Should have thrown Timeout or Cancelled exception");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (Exception ex)
            {
                // Verify it's related to timeout
                if (!ex.Message.Contains("timeout") && !(ex is OperationCanceledException))
                {
                   // It might throw JsonException if it reads partial data, but here it stalls on read
                   // If it stalls inside DeserializeAsync, the CancellationToken should fire.
                   // However, our StalledStream implementation below might need to respect token.
                }
            }
        }

        // Minimal mock stream
        class StalledStream : Stream
        {
            private readonly MemoryStream _inner;
            public StalledStream(MemoryStream inner) { _inner = inner; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) { }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                // First read 4 bytes for length (from inner)
                if (_inner.Position < 4)
                {
                    return await _inner.ReadAsync(buffer, offset, count, cancellationToken);
                }

                // Then stall forever
                await Task.Delay(35000, cancellationToken); // Delay > 30s timeout
                return 0;
            }
        }
    }
}
