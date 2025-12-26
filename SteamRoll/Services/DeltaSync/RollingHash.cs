namespace SteamRoll.Services.DeltaSync;

/// <summary>
/// Rolling hash implementation (Adler-32 variant) for rsync-style delta sync.
/// Allows efficient computation of hash for sliding window over data.
/// </summary>
public class RollingHash
{
    private const ushort MOD_ADLER = 65521; // Largest prime smaller than 65536

    private uint _a = 1;
    private uint _b = 0;
    private readonly int _windowSize;
    private readonly byte[] _window;
    private int _windowPos;

    /// <summary>
    /// Creates a new rolling hash with specified window size.
    /// </summary>
    /// <param name="windowSize">Size of the sliding window in bytes.</param>
    public RollingHash(int windowSize)
    {
        _windowSize = windowSize;
        _window = new byte[windowSize];
        _windowPos = 0;
    }

    /// <summary>
    /// Current hash value.
    /// </summary>
    public uint Hash => (_b << 16) | _a;

    /// <summary>
    /// Resets the hash to initial state.
    /// </summary>
    public void Reset()
    {
        _a = 1;
        _b = 0;
        _windowPos = 0;
        Array.Clear(_window, 0, _window.Length);
    }

    /// <summary>
    /// Adds a byte to the hash (initial fill of window).
    /// </summary>
    public void Add(byte b)
    {
        _a = (_a + b) % MOD_ADLER;
        _b = (_b + _a) % MOD_ADLER;

        _window[_windowPos] = b;
        _windowPos++;
        if (_windowPos >= _windowSize)
        {
            _windowPos = 0;
        }
    }

    /// <summary>
    /// Rolls the hash by removing the oldest byte and adding a new one.
    /// This is the key operation for efficient sliding window hashing.
    /// </summary>
    public void Roll(byte outgoing, byte incoming)
    {
        // Remove outgoing byte contribution
        // Use long arithmetic to prevent underflow before modulo
        // We cast _a to long first to ensure the subtraction result is a long, preventing unsigned underflow
        _a = (uint)(( (long)_a - outgoing + incoming + MOD_ADLER) % MOD_ADLER);

        long bCalc = _b - (long)_windowSize * outgoing + _a - 1;

        // Optimize: Use modulo arithmetic instead of loop for handling negative values
        // This handles cases where bCalc is very negative (large window size) in O(1)
        bCalc %= MOD_ADLER;
        if (bCalc < 0) bCalc += MOD_ADLER;

        _b = (uint)bCalc;

        // Update window
        _window[_windowPos] = incoming;
        _windowPos = (_windowPos + 1) % _windowSize;
    }

    /// <summary>
    /// Computes hash for an entire buffer (non-rolling).
    /// Used for initial block hashing.
    /// </summary>
    public static uint ComputeHash(byte[] data, int offset, int length)
    {
        uint a = 1, b = 0;
        for (int i = 0; i < length; i++)
        {
            a = (a + data[offset + i]) % MOD_ADLER;
            b = (b + a) % MOD_ADLER;
        }
        return (b << 16) | a;
    }

    /// <summary>
    /// Computes hash for a span (non-rolling).
    /// </summary>
    public static uint ComputeHash(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (var by in data)
        {
            a = (a + by) % MOD_ADLER;
            b = (b + a) % MOD_ADLER;
        }
        return (b << 16) | a;
    }
}
