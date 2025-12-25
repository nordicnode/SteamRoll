using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamRoll.Services;

/// <summary>
/// Connection quality levels.
/// </summary>
public enum ConnectionQuality
{
    Excellent,  // < 50ms
    Good,       // 50-150ms
    Fair,       // 150-300ms
    Poor,       // > 300ms
    Disconnected
}

/// <summary>
/// Health information for a peer connection.
/// </summary>
public class PeerHealthInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// IP address of the peer.
    /// </summary>
    public required string IpAddress { get; init; }

    private long _latencyMs;
    /// <summary>
    /// Last measured latency in milliseconds.
    /// </summary>
    public long LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (_latencyMs != value)
            {
                _latencyMs = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LatencyDisplay));
                OnPropertyChanged(nameof(Quality));
                OnPropertyChanged(nameof(QualityDisplay));
            }
        }
    }

    private bool _isReachable = true;
    /// <summary>
    /// Whether the peer responded to last ping.
    /// </summary>
    public bool IsReachable
    {
        get => _isReachable;
        set
        {
            if (_isReachable != value)
            {
                _isReachable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Quality));
                OnPropertyChanged(nameof(QualityDisplay));
            }
        }
    }

    /// <summary>
    /// When the peer was last checked.
    /// </summary>
    public DateTime LastChecked { get; set; }

    /// <summary>
    /// Number of consecutive failures.
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// Connection quality based on latency.
    /// </summary>
    public ConnectionQuality Quality
    {
        get
        {
            if (!IsReachable) return ConnectionQuality.Disconnected;
            return LatencyMs switch
            {
                < 50 => ConnectionQuality.Excellent,
                < 150 => ConnectionQuality.Good,
                < 300 => ConnectionQuality.Fair,
                _ => ConnectionQuality.Poor
            };
        }
    }

    /// <summary>
    /// Display string for latency.
    /// </summary>
    public string LatencyDisplay => IsReachable ? $"{LatencyMs}ms" : "N/A";

    /// <summary>
    /// Display string for quality.
    /// </summary>
    public string QualityDisplay => Quality switch
    {
        ConnectionQuality.Excellent => "🟢 Excellent",
        ConnectionQuality.Good => "🟢 Good",
        ConnectionQuality.Fair => "🟡 Fair",
        ConnectionQuality.Poor => "🟠 Poor",
        ConnectionQuality.Disconnected => "🔴 Offline",
        _ => "Unknown"
    };
}

/// <summary>
/// Monitors connection health to peers via periodic pings.
/// </summary>
public class ConnectionHealthService : IDisposable
{
    private static readonly Lazy<ConnectionHealthService> _instance = new(() => new ConnectionHealthService());
    public static ConnectionHealthService Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, PeerHealthInfo> _healthInfo = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    private bool _isRunning;

    /// <summary>
    /// Ping interval in seconds.
    /// </summary>
    public int PingIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Ping timeout in milliseconds.
    /// </summary>
    public int PingTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Event raised when health status changes.
    /// </summary>
    public event EventHandler<PeerHealthInfo>? HealthChanged;

    /// <summary>
    /// Gets health info for a specific peer.
    /// </summary>
    public PeerHealthInfo? GetHealth(string ipAddress)
    {
        return _healthInfo.TryGetValue(ipAddress, out var info) ? info : null;
    }

    /// <summary>
    /// Gets all monitored peers.
    /// </summary>
    public IReadOnlyCollection<PeerHealthInfo> GetAllHealth()
    {
        return _healthInfo.Values.ToList();
    }

    /// <summary>
    /// Adds a peer to monitor.
    /// </summary>
    public void AddPeer(string ipAddress)
    {
        _healthInfo.TryAdd(ipAddress, new PeerHealthInfo { IpAddress = ipAddress });
    }

    /// <summary>
    /// Removes a peer from monitoring.
    /// </summary>
    public void RemovePeer(string ipAddress)
    {
        _healthInfo.TryRemove(ipAddress, out _);
    }

    /// <summary>
    /// Starts the health monitoring loop.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;

        _monitorTask = Task.Run(MonitorLoopAsync);
        LogService.Instance.Info("Connection health monitoring started", "ConnectionHealthService");
    }

    /// <summary>
    /// Stops the health monitoring loop.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _cts.Cancel();
        LogService.Instance.Info("Connection health monitoring stopped", "ConnectionHealthService");
    }

    /// <summary>
    /// Pings a specific peer immediately.
    /// </summary>
    public async Task<PeerHealthInfo> PingAsync(string ipAddress)
    {
        var info = _healthInfo.GetOrAdd(ipAddress, _ => new PeerHealthInfo { IpAddress = ipAddress });

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ipAddress, PingTimeoutMs);

            info.IsReachable = reply.Status == IPStatus.Success;
            info.LatencyMs = reply.RoundtripTime;
            info.LastChecked = DateTime.Now;

            if (info.IsReachable)
            {
                info.FailureCount = 0;
            }
            else
            {
                info.FailureCount++;
            }
        }
        catch (Exception)
        {
            info.IsReachable = false;
            info.FailureCount++;
            info.LastChecked = DateTime.Now;
        }

        HealthChanged?.Invoke(this, info);
        return info;
    }

    private async Task MonitorLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var peers = _healthInfo.Keys.ToList();

                // Ping all peers concurrently
                var tasks = peers.Select(ip => PingAsync(ip));
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning($"Health monitor error: {ex.Message}", "ConnectionHealthService");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PingIntervalSeconds), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
