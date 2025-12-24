using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamRoll.Services;

/// <summary>
/// Vector clock implementation for distributed save synchronization.
/// Tracks causality between save modifications across multiple devices
/// without relying on synchronized system clocks.
/// </summary>
public class VectorClock
{
    /// <summary>
    /// Clock entries: DeviceId -> LogicalTimestamp
    /// </summary>
    public Dictionary<string, long> Clocks { get; set; } = new();

    /// <summary>
    /// Creates an empty vector clock.
    /// </summary>
    public VectorClock() { }

    /// <summary>
    /// Creates a copy of another vector clock.
    /// </summary>
    public VectorClock(VectorClock other)
    {
        Clocks = new Dictionary<string, long>(other.Clocks);
    }

    /// <summary>
    /// Increments the clock for the specified device.
    /// Call this when the local device modifies a save.
    /// </summary>
    public void Increment(string deviceId)
    {
        if (Clocks.TryGetValue(deviceId, out var current))
        {
            Clocks[deviceId] = current + 1;
        }
        else
        {
            Clocks[deviceId] = 1;
        }
    }

    /// <summary>
    /// Merges another vector clock into this one (takes max of each entry).
    /// Call this when receiving a save from another device.
    /// </summary>
    public void Merge(VectorClock other)
    {
        foreach (var (deviceId, timestamp) in other.Clocks)
        {
            if (Clocks.TryGetValue(deviceId, out var current))
            {
                Clocks[deviceId] = Math.Max(current, timestamp);
            }
            else
            {
                Clocks[deviceId] = timestamp;
            }
        }
    }

    /// <summary>
    /// Compares this clock with another to determine causality.
    /// </summary>
    /// <returns>
    /// -1: This clock happened-before other (other is newer)
    ///  0: Clocks are concurrent (conflict!)
    ///  1: This clock happened-after other (this is newer)
    /// </returns>
    public int CompareTo(VectorClock other)
    {
        bool thisGreater = false;
        bool otherGreater = false;

        // Get all device IDs from both clocks
        var allDevices = Clocks.Keys.Union(other.Clocks.Keys);

        foreach (var deviceId in allDevices)
        {
            var thisValue = Clocks.GetValueOrDefault(deviceId, 0);
            var otherValue = other.Clocks.GetValueOrDefault(deviceId, 0);

            if (thisValue > otherValue) thisGreater = true;
            if (otherValue > thisValue) otherGreater = true;
        }

        if (thisGreater && !otherGreater) return 1;  // This is strictly newer
        if (otherGreater && !thisGreater) return -1; // Other is strictly newer
        if (!thisGreater && !otherGreater) return 0; // Equal (or both empty)
        
        // Both are greater in some dimension = concurrent/conflict
        return 0;
    }

    /// <summary>
    /// Returns true if this clock happened-before or is equal to other.
    /// </summary>
    public bool HappenedBefore(VectorClock other) => CompareTo(other) <= 0;

    /// <summary>
    /// Returns true if clocks are concurrent (neither happened-before the other).
    /// This indicates a conflict that requires user resolution.
    /// </summary>
    public bool IsConcurrentWith(VectorClock other)
    {
        bool thisGreater = false;
        bool otherGreater = false;

        var allDevices = Clocks.Keys.Union(other.Clocks.Keys);

        foreach (var deviceId in allDevices)
        {
            var thisValue = Clocks.GetValueOrDefault(deviceId, 0);
            var otherValue = other.Clocks.GetValueOrDefault(deviceId, 0);

            if (thisValue > otherValue) thisGreater = true;
            if (otherValue > thisValue) otherGreater = true;
        }

        // Concurrent if both have some entries greater than the other
        return thisGreater && otherGreater;
    }

    /// <summary>
    /// Gets the total logical time (sum of all clocks) for display purposes.
    /// </summary>
    [JsonIgnore]
    public long TotalLogicalTime => Clocks.Values.Sum();

    /// <summary>
    /// Serializes the vector clock to JSON.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(Clocks);

    /// <summary>
    /// Deserializes a vector clock from JSON.
    /// </summary>
    public static VectorClock FromJson(string json)
    {
        try
        {
            var clocks = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
            return new VectorClock { Clocks = clocks ?? new() };
        }
        catch
        {
            return new VectorClock();
        }
    }

    public override string ToString()
    {
        var entries = Clocks.Select(kv => $"{kv.Key}:{kv.Value}");
        return $"[{string.Join(", ", entries)}]";
    }
}
