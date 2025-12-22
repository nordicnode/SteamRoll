using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamRoll.Services.Transfer;

/// <summary>
/// Simple token bucket bandwidth limiter.
/// </summary>
public class BandwidthLimiter
{
    private readonly Func<long> _bytesPerSecondProvider;
    private double _tokens;
    private DateTime _lastUpdate;
    private const double MAX_TOKENS_MULTIPLIER = 1.0; // Max burst = 1 second worth

    public BandwidthLimiter(long fixedBytesPerSecond) : this(() => fixedBytesPerSecond) { }

    public BandwidthLimiter(Func<long> bytesPerSecondProvider)
    {
        _bytesPerSecondProvider = bytesPerSecondProvider;
        var initialRate = _bytesPerSecondProvider();
        _tokens = initialRate; // Start full
        _lastUpdate = DateTime.UtcNow;
    }

    public async Task WaitAsync(int bytes, CancellationToken ct)
    {
        var bytesPerSecond = _bytesPerSecondProvider();

        if (bytesPerSecond <= 0) return; // No limit

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Re-fetch rate in loop in case it changes
            bytesPerSecond = _bytesPerSecondProvider();
            if (bytesPerSecond <= 0) return;

            var now = DateTime.UtcNow;
            var elapsed = (now - _lastUpdate).TotalSeconds;
            _lastUpdate = now;

            // Refill tokens
            _tokens += elapsed * bytesPerSecond;
            var maxTokens = bytesPerSecond * MAX_TOKENS_MULTIPLIER;
            if (_tokens > maxTokens) _tokens = maxTokens;

            if (_tokens >= bytes)
            {
                _tokens -= bytes;
                return;
            }

            // Not enough tokens, wait
            var deficit = bytes - _tokens;
            var waitTimeSeconds = deficit / bytesPerSecond;
            var waitTimeMs = (int)(waitTimeSeconds * 1000);

            if (waitTimeMs > 0)
            {
                await Task.Delay(waitTimeMs, ct);
            }
        }
    }
}
