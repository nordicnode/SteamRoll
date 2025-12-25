using System.IO;
using System.Text.Json;

namespace SteamRoll.Services;

/// <summary>
/// Represents a single play session.
/// </summary>
public class PlaySession
{
    /// <summary>
    /// When the session started.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Duration of the session in minutes.
    /// </summary>
    public int DurationMinutes { get; set; }

    /// <summary>
    /// Display string for the session.
    /// </summary>
    public string Display => $"{StartTime:g} - {DurationMinutes} min";
}

/// <summary>
/// Playtime data for a single game.
/// </summary>
public class GamePlaytime
{
    /// <summary>
    /// Steam App ID of the game.
    /// </summary>
    public int AppId { get; set; }

    /// <summary>
    /// Name of the game.
    /// </summary>
    public string GameName { get; set; } = "";

    /// <summary>
    /// Total playtime in minutes.
    /// </summary>
    public int TotalMinutes { get; set; }

    /// <summary>
    /// When the game was last played.
    /// </summary>
    public DateTime? LastPlayed { get; set; }

    /// <summary>
    /// List of recent play sessions.
    /// </summary>
    public List<PlaySession> Sessions { get; set; } = new();

    /// <summary>
    /// Display string for total playtime.
    /// </summary>
    public string TotalPlaytimeDisplay
    {
        get
        {
            if (TotalMinutes < 60) return $"{TotalMinutes} min";
            var hours = TotalMinutes / 60;
            var mins = TotalMinutes % 60;
            return mins > 0 ? $"{hours}h {mins}m" : $"{hours}h";
        }
    }

    /// <summary>
    /// Display string for last played.
    /// </summary>
    public string LastPlayedDisplay => LastPlayed?.ToString("g") ?? "Never";
}

/// <summary>
/// Service for tracking game launch and playtime.
/// </summary>
public class PlaytimeService
{
    private static readonly Lazy<PlaytimeService> _instance = new(() => new PlaytimeService());
    public static PlaytimeService Instance => _instance.Value;

    private readonly string _dataFile;
    private readonly Dictionary<int, GamePlaytime> _playtimes = new();
    private readonly Dictionary<int, DateTime> _activeSessions = new();
    private readonly object _lock = new();

    /// <summary>
    /// Event raised when playtime data is updated.
    /// </summary>
    public event EventHandler? PlaytimeUpdated;

    /// <summary>
    /// Maximum sessions to keep per game.
    /// </summary>
    public int MaxSessionsPerGame { get; set; } = 50;

    private PlaytimeService()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamRoll");
        _dataFile = Path.Combine(cacheDir, "playtime_data.json");

        Load();
    }

    /// <summary>
    /// Called when a game is launched.
    /// </summary>
    /// <param name="appId">Steam App ID.</param>
    /// <param name="gameName">Name of the game.</param>
    public void TrackLaunch(int appId, string gameName)
    {
        lock (_lock)
        {
            _activeSessions[appId] = DateTime.Now;

            // Ensure game exists in playtime data
            if (!_playtimes.ContainsKey(appId))
            {
                _playtimes[appId] = new GamePlaytime
                {
                    AppId = appId,
                    GameName = gameName
                };
            }

            LogService.Instance.Info($"Game launched: {gameName} (AppId: {appId})", "PlaytimeService");
        }
    }

    /// <summary>
    /// Called when a game is closed.
    /// </summary>
    /// <param name="appId">Steam App ID.</param>
    public void TrackClose(int appId)
    {
        lock (_lock)
        {
            if (!_activeSessions.TryGetValue(appId, out var startTime))
            {
                LogService.Instance.Warning($"TrackClose called but no active session for AppId: {appId}", "PlaytimeService");
                return;
            }

            var duration = DateTime.Now - startTime;
            var durationMinutes = (int)Math.Round(duration.TotalMinutes);

            // Minimum 1 minute if played at all
            if (duration.TotalSeconds > 30)
            {
                durationMinutes = Math.Max(1, durationMinutes);

                if (_playtimes.TryGetValue(appId, out var playtime))
                {
                    playtime.TotalMinutes += durationMinutes;
                    playtime.LastPlayed = DateTime.Now;

                    // Add session
                    playtime.Sessions.Insert(0, new PlaySession
                    {
                        StartTime = startTime,
                        DurationMinutes = durationMinutes
                    });

                    // Limit session history
                    while (playtime.Sessions.Count > MaxSessionsPerGame)
                    {
                        playtime.Sessions.RemoveAt(playtime.Sessions.Count - 1);
                    }

                    LogService.Instance.Info($"Game closed: {playtime.GameName} - played {durationMinutes} min", "PlaytimeService");
                }
            }

            _activeSessions.Remove(appId);
            Save();
            PlaytimeUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets playtime data for a specific game.
    /// </summary>
    public GamePlaytime? GetPlaytime(int appId)
    {
        lock (_lock)
        {
            return _playtimes.TryGetValue(appId, out var playtime) ? playtime : null;
        }
    }

    /// <summary>
    /// Gets all playtime data.
    /// </summary>
    public IReadOnlyList<GamePlaytime> GetAllPlaytimes()
    {
        lock (_lock)
        {
            return _playtimes.Values.ToList();
        }
    }

    /// <summary>
    /// Gets recently played games, sorted by last played time.
    /// </summary>
    public IReadOnlyList<GamePlaytime> GetRecentGames(int count = 10)
    {
        lock (_lock)
        {
            return _playtimes.Values
                .Where(p => p.LastPlayed.HasValue)
                .OrderByDescending(p => p.LastPlayed)
                .Take(count)
                .ToList();
        }
    }

    /// <summary>
    /// Gets top games by playtime.
    /// </summary>
    public IReadOnlyList<GamePlaytime> GetTopGames(int count = 10)
    {
        lock (_lock)
        {
            return _playtimes.Values
                .OrderByDescending(p => p.TotalMinutes)
                .Take(count)
                .ToList();
        }
    }

    /// <summary>
    /// Gets total playtime across all games.
    /// </summary>
    public int GetTotalPlaytimeMinutes()
    {
        lock (_lock)
        {
            return _playtimes.Values.Sum(p => p.TotalMinutes);
        }
    }

    /// <summary>
    /// Manually adds playtime (for imports or corrections).
    /// </summary>
    public void AddPlaytime(int appId, string gameName, int minutes)
    {
        lock (_lock)
        {
            if (!_playtimes.TryGetValue(appId, out var playtime))
            {
                playtime = new GamePlaytime
                {
                    AppId = appId,
                    GameName = gameName
                };
                _playtimes[appId] = playtime;
            }

            playtime.TotalMinutes += minutes;
            playtime.LastPlayed = DateTime.Now;

            Save();
            PlaytimeUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Clears all playtime data.
    /// </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _playtimes.Clear();
            _activeSessions.Clear();
            Save();
            PlaytimeUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_dataFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var data = _playtimes.Values.ToList();
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_dataFile, json);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to save playtime data: {ex.Message}", "PlaytimeService");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_dataFile)) return;

            var json = File.ReadAllText(_dataFile);
            var data = JsonSerializer.Deserialize<List<GamePlaytime>>(json);

            if (data != null)
            {
                foreach (var playtime in data)
                {
                    _playtimes[playtime.AppId] = playtime;
                }

                LogService.Instance.Info($"Loaded playtime data for {_playtimes.Count} games", "PlaytimeService");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to load playtime data: {ex.Message}", "PlaytimeService");
        }
    }
}
