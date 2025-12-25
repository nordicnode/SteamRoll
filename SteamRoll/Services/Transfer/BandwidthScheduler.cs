using System.Text.Json;
using System.IO;

namespace SteamRoll.Services.Transfer;

/// <summary>
/// Time period with associated bandwidth limit.
/// </summary>
public class BandwidthPeriod
{
    /// <summary>
    /// Start time of day for this period (e.g., 09:00).
    /// </summary>
    public TimeSpan StartTime { get; set; }

    /// <summary>
    /// End time of day for this period (e.g., 17:00).
    /// </summary>
    public TimeSpan EndTime { get; set; }

    /// <summary>
    /// Maximum bytes per second during this period (0 = unlimited).
    /// </summary>
    public long MaxBytesPerSecond { get; set; }

    /// <summary>
    /// Days of the week this period applies to (null = all days).
    /// </summary>
    public DayOfWeek[]? DaysOfWeek { get; set; }

    /// <summary>
    /// Display string for the bandwidth limit.
    /// </summary>
    public string LimitDisplay => MaxBytesPerSecond == 0 
        ? "Unlimited" 
        : $"{FormatUtils.FormatBytes(MaxBytesPerSecond)}/s";

    /// <summary>
    /// Display string for the time range.
    /// </summary>
    public string TimeRangeDisplay => $"{StartTime:hh\\:mm} - {EndTime:hh\\:mm}";

    /// <summary>
    /// Checks if the given time falls within this period.
    /// </summary>
    public bool IsActive(DateTime dateTime)
    {
        var time = dateTime.TimeOfDay;
        var dayOfWeek = dateTime.DayOfWeek;

        // Check day of week if specified
        if (DaysOfWeek != null && DaysOfWeek.Length > 0)
        {
            if (!DaysOfWeek.Contains(dayOfWeek))
                return false;
        }

        // Handle overnight periods (e.g., 22:00 - 06:00)
        if (EndTime < StartTime)
        {
            return time >= StartTime || time < EndTime;
        }

        return time >= StartTime && time < EndTime;
    }
}

/// <summary>
/// Manages bandwidth limits based on time of day.
/// </summary>
public class BandwidthScheduler
{
    private static readonly Lazy<BandwidthScheduler> _instance = new(() => new BandwidthScheduler());
    public static BandwidthScheduler Instance => _instance.Value;

    private readonly List<BandwidthPeriod> _periods = new();
    private readonly string _configFile;
    private long _defaultLimit = 0; // 0 = unlimited

    /// <summary>
    /// Gets all configured bandwidth periods.
    /// </summary>
    public IReadOnlyList<BandwidthPeriod> Periods => _periods.AsReadOnly();

    /// <summary>
    /// Default bandwidth limit when no period is active (0 = unlimited).
    /// </summary>
    public long DefaultBytesPerSecond
    {
        get => _defaultLimit;
        set
        {
            _defaultLimit = Math.Max(0, value);
            Save();
        }
    }

    /// <summary>
    /// Whether bandwidth scheduling is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    private BandwidthScheduler()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamRoll");
        _configFile = Path.Combine(cacheDir, "bandwidth_schedule.json");

        Load();
    }

    /// <summary>
    /// Gets the current bandwidth limit based on time of day.
    /// </summary>
    /// <returns>Maximum bytes per second (0 = unlimited).</returns>
    public long GetCurrentLimit()
    {
        if (!IsEnabled)
            return 0; // Unlimited when disabled

        var now = DateTime.Now;

        // Find first matching period
        foreach (var period in _periods)
        {
            if (period.IsActive(now))
            {
                return period.MaxBytesPerSecond;
            }
        }

        return _defaultLimit;
    }

    /// <summary>
    /// Adds a new bandwidth period.
    /// </summary>
    public void AddPeriod(BandwidthPeriod period)
    {
        _periods.Add(period);
        Save();
        LogService.Instance.Info($"Added bandwidth period: {period.TimeRangeDisplay} = {period.LimitDisplay}", "BandwidthScheduler");
    }

    /// <summary>
    /// Removes a bandwidth period.
    /// </summary>
    public void RemovePeriod(int index)
    {
        if (index >= 0 && index < _periods.Count)
        {
            _periods.RemoveAt(index);
            Save();
            LogService.Instance.Info($"Removed bandwidth period at index {index}", "BandwidthScheduler");
        }
    }

    /// <summary>
    /// Clears all bandwidth periods.
    /// </summary>
    public void ClearPeriods()
    {
        _periods.Clear();
        Save();
    }

    /// <summary>
    /// Sets up common presets.
    /// </summary>
    public void SetDaytimeThrottlePreset(long daytimeLimitBps)
    {
        _periods.Clear();
        
        // Daytime (9 AM - 6 PM weekdays) - throttled
        _periods.Add(new BandwidthPeriod
        {
            StartTime = TimeSpan.FromHours(9),
            EndTime = TimeSpan.FromHours(18),
            MaxBytesPerSecond = daytimeLimitBps,
            DaysOfWeek = new[] { 
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, 
                DayOfWeek.Thursday, DayOfWeek.Friday 
            }
        });

        // Outside daytime = unlimited (default)
        _defaultLimit = 0;

        Save();
        LogService.Instance.Info($"Set daytime throttle preset: {FormatUtils.FormatBytes(daytimeLimitBps)}/s on weekdays 9-18", "BandwidthScheduler");
    }

    /// <summary>
    /// Sets up night-only full speed preset.
    /// </summary>
    public void SetNightOnlyPreset()
    {
        _periods.Clear();

        // Night time (11 PM - 7 AM) - unlimited
        _periods.Add(new BandwidthPeriod
        {
            StartTime = TimeSpan.FromHours(23),
            EndTime = TimeSpan.FromHours(7),
            MaxBytesPerSecond = 0 // Unlimited
        });

        // Daytime - very slow
        _defaultLimit = 1 * 1024 * 1024; // 1 MB/s

        Save();
        LogService.Instance.Info("Set night-only preset: unlimited 23:00-07:00, 1 MB/s otherwise", "BandwidthScheduler");
    }

    /// <summary>
    /// Calculates delay needed to achieve target bandwidth.
    /// </summary>
    /// <param name="bytesSent">Bytes just sent.</param>
    /// <param name="elapsedMs">Time taken in milliseconds.</param>
    /// <returns>Delay in milliseconds to achieve target rate (0 = no delay needed).</returns>
    public int CalculateThrottleDelay(long bytesSent, double elapsedMs)
    {
        var limit = GetCurrentLimit();
        if (limit == 0) return 0; // Unlimited

        // How long should it have taken at the limit?
        var targetMs = (bytesSent * 1000.0) / limit;

        // If we were too fast, delay
        var delayMs = targetMs - elapsedMs;
        return delayMs > 0 ? (int)Math.Ceiling(delayMs) : 0;
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var data = new BandwidthScheduleData
            {
                IsEnabled = IsEnabled,
                DefaultBytesPerSecond = _defaultLimit,
                Periods = _periods.Select(p => new BandwidthPeriodData
                {
                    StartTimeMinutes = (int)p.StartTime.TotalMinutes,
                    EndTimeMinutes = (int)p.EndTime.TotalMinutes,
                    MaxBytesPerSecond = p.MaxBytesPerSecond,
                    DaysOfWeek = p.DaysOfWeek
                }).ToList()
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFile, json);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to save bandwidth schedule: {ex.Message}", "BandwidthScheduler");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_configFile)) return;

            var json = File.ReadAllText(_configFile);
            var data = JsonSerializer.Deserialize<BandwidthScheduleData>(json);

            if (data != null)
            {
                IsEnabled = data.IsEnabled;
                _defaultLimit = data.DefaultBytesPerSecond;
                _periods.Clear();

                foreach (var p in data.Periods)
                {
                    _periods.Add(new BandwidthPeriod
                    {
                        StartTime = TimeSpan.FromMinutes(p.StartTimeMinutes),
                        EndTime = TimeSpan.FromMinutes(p.EndTimeMinutes),
                        MaxBytesPerSecond = p.MaxBytesPerSecond,
                        DaysOfWeek = p.DaysOfWeek
                    });
                }

                LogService.Instance.Info($"Loaded bandwidth schedule with {_periods.Count} periods", "BandwidthScheduler");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to load bandwidth schedule: {ex.Message}", "BandwidthScheduler");
        }
    }

    private class BandwidthScheduleData
    {
        public bool IsEnabled { get; set; }
        public long DefaultBytesPerSecond { get; set; }
        public List<BandwidthPeriodData> Periods { get; set; } = new();
    }

    private class BandwidthPeriodData
    {
        public int StartTimeMinutes { get; set; }
        public int EndTimeMinutes { get; set; }
        public long MaxBytesPerSecond { get; set; }
        public DayOfWeek[]? DaysOfWeek { get; set; }
    }
}
