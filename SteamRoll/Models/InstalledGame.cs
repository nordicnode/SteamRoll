using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SteamRoll.Services;

namespace SteamRoll.Models;

/// <summary>
/// Represents an installed Steam game with metadata from ACF manifest files.
/// </summary>
public class InstalledGame : INotifyPropertyChanged
{
    // Static cached brushes to avoid creating new instances on every property access
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush YellowBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
    
    static InstalledGame()
    {
        // Freeze brushes for thread-safety and performance
        GreenBrush.Freeze();
        YellowBrush.Freeze();
        RedBrush.Freeze();
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Steam App ID (unique identifier).
    /// </summary>
    public int AppId { get; set; }

    /// <summary>
    /// Display name of the game.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Installation directory name (relative to steamapps/common).
    /// </summary>
    public string InstallDir { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the game installation.
    /// </summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the Steam library containing this game.
    /// </summary>
    public string LibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Size of the game installation in bytes.
    /// </summary>
    public long SizeOnDisk { get; set; }

    /// <summary>
    /// StateFlags from the ACF manifest. 4 = fully installed.
    /// </summary>
    public int StateFlags { get; set; }

    /// <summary>
    /// Whether the game is fully installed (StateFlags & 4).
    /// </summary>
    public bool IsFullyInstalled => (StateFlags & 4) == 4;

    /// <summary>
    /// Last update timestamp.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// Build ID from Steam.
    /// </summary>
    public int BuildId { get; set; }

    /// <summary>
    /// Build ID of the created package (if any).
    /// </summary>
    public int? PackageBuildId { get; set; }
    
    /// <summary>
    /// Whether an update is available (Package is older than Steam version).
    /// </summary>
    public bool UpdateAvailable => IsPackaged && PackageBuildId.HasValue && BuildId > PackageBuildId.Value;

    /// <summary>
    /// Formatted version string for display (Build ID based).
    /// </summary>
    public string Version => BuildId > 0 ? $"Build {BuildId}" : "Unknown";

    /// <summary>
    /// Path to the locally cached header image (if found).
    /// </summary>
    public string? LocalHeaderPath { get; set; }

    private string? _resolvedHeaderImageUrl;
    /// <summary>
    /// Resolved header image URL after checking multiple sources.
    /// Set by GameImageService when a working URL is found.
    /// </summary>
    public string? ResolvedHeaderImageUrl
    {
        get => _resolvedHeaderImageUrl;
        set
        {
            if (_resolvedHeaderImageUrl != value)
            {
                _resolvedHeaderImageUrl = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HeaderImageUrl));
            }
        }
    }

    /// <summary>
    /// URL to the game's header image. Uses resolved URL if available, 
    /// otherwise falls back to local cache or Steam CDN.
    /// </summary>
    public string HeaderImageUrl => 
        !string.IsNullOrEmpty(LocalHeaderPath) ? LocalHeaderPath :
        !string.IsNullOrEmpty(ResolvedHeaderImageUrl) ? ResolvedHeaderImageUrl :
        $"https://steamcdn-a.akamaihd.net/steam/apps/{AppId}/header.jpg";
    
    /// <summary>
    /// URL to the game's capsule image (smaller) from Steam CDN.
    /// </summary>
    public string CapsuleImageUrl => $"https://steamcdn-a.akamaihd.net/steam/apps/{AppId}/capsule_231x87.jpg";


    /// <summary>
    /// Timestamp when the game was last played (Unix timestamp).
    /// </summary>
    public long LastPlayed { get; set; }

    /// <summary>
    /// DateTime representation of LastPlayed.
    /// </summary>
    public DateTime LastPlayedTime => LastPlayed > 0 ? DateTimeOffset.FromUnixTimeSeconds(LastPlayed).LocalDateTime : DateTime.MinValue;

    /// <summary>
    /// Formatted relative time string for Last Played.
    /// </summary>
    public string FormattedLastPlayed
    {
        get
        {
            if (LastPlayed <= 0) return "Never played";
            var span = DateTime.Now - LastPlayedTime;
            if (span.TotalDays < 1) return "Played today";
            if (span.TotalDays < 2) return "Played yesterday";
            if (span.TotalDays < 30) return $"Played {span.Days} days ago";
            if (span.TotalDays < 365) return $"Played {span.Days / 30} months ago";
            return $"Played {span.Days / 365} years ago";
        }
    }

    private bool _isFavorite;
    /// <summary>
    /// Whether the game is marked as favorite.
    /// </summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite != value)
            {
                _isFavorite = value;
                OnPropertyChanged();
            }
        }
    }

    // ============================================
    // Package Status Tracking
    // ============================================

    /// <summary>
    /// Whether this game has been packaged (exists in output directory).
    /// </summary>
    public bool IsPackaged { get; set; }

    /// <summary>
    /// Path to the packaged version if it exists.
    /// </summary>
    public string? PackagePath { get; set; }

    /// <summary>
    /// Timestamp when the game was last packaged.
    /// </summary>
    public DateTime? LastPackaged { get; set; }

    /// <summary>
    /// Whether this package was received from another SteamRoll client over the network.
    /// </summary>
    public bool IsReceivedPackage { get; set; }

    // ============================================
    // UI State (not persisted)
    // ============================================

    /// <summary>
    /// Whether this game is selected for batch operations.
    /// </summary>
    public bool IsSelected { get; set; }

    // ============================================
    // DLC Information
    // ============================================


    /// <summary>
    /// List of all available DLC for this game.
    /// </summary>
    public List<DlcInfo> AvailableDlc { get; set; } = new();

    /// <summary>
    /// Whether DLC info has been fetched from Steam.
    /// </summary>
    public bool DlcFetched { get; set; }

    /// <summary>
    /// Count of installed DLC.
    /// </summary>
    public int InstalledDlcCount => AvailableDlc.Count(d => d.IsInstalled);

    /// <summary>
    /// Total count of available DLC.
    /// </summary>
    public int TotalDlcCount => AvailableDlc.Count;

    /// <summary>
    /// Whether this game has any DLC.
    /// </summary>
    public bool HasDlc => AvailableDlc.Count > 0;

    /// <summary>
    /// DLC summary for UI display.
    /// </summary>
    public string DlcSummary => HasDlc 
        ? $"{InstalledDlcCount}/{TotalDlcCount} DLC installed" 
        : "No DLC";

    // ============================================
    // Review Scores (Not persisted, fetched async)
    // ============================================

    private int? _reviewPositivePercent;
    public int? ReviewPositivePercent
    {
        get => _reviewPositivePercent;
        set
        {
            if (_reviewPositivePercent != value)
            {
                _reviewPositivePercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ReviewDescription));
                OnPropertyChanged(nameof(ReviewScoreColor));
                OnPropertyChanged(nameof(HasReviewScore));
            }
        }
    }

    private string? _reviewDescription;
    public string? ReviewDescription
    {
        get => _reviewDescription;
        set
        {
            if (_reviewDescription != value)
            {
                _reviewDescription = value;
                OnPropertyChanged();
            }
        }
    }
    
    public bool HasReviewScore => ReviewPositivePercent.HasValue;

    public Brush ReviewScoreColor => ReviewPositivePercent switch
    {
        >= 70 => GreenBrush,
        >= 40 => YellowBrush,
        _ => RedBrush
    };

    private int? _metacriticScore;
    public int? MetacriticScore
    {
        get => _metacriticScore;
        set
        {
            if (_metacriticScore != value)
            {
                _metacriticScore = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MetacriticScoreColor));
                OnPropertyChanged(nameof(HasMetacriticScore));
            }
        }
    }
    
    public bool HasMetacriticScore => MetacriticScore.HasValue;

    public Brush MetacriticScoreColor => MetacriticScore switch
    {
        >= 75 => GreenBrush,
        >= 50 => YellowBrush,
        _ => RedBrush
    };

    // ============================================
    // DRM & Compatibility Analysis
    // ============================================

    /// <summary>
    /// Complete DRM analysis result for this game.
    /// </summary>
    public DrmAnalysisResult? DrmAnalysis { get; set; }

    /// <summary>
    /// Primary detected DRM type.
    /// </summary>
    public DrmType PrimaryDrm => DrmAnalysis?.PrimaryDrm ?? DrmType.None;

    /// <summary>
    /// Whether the game has Steamworks integration.
    /// </summary>
    public bool HasSteamworksIntegration => DrmAnalysis?.HasSteamworksIntegration ?? false;

    /// <summary>
    /// Compatibility score (0.0 - 1.0) for Goldberg Emulator.
    /// Already-packaged games (including received) are considered fully compatible.
    /// </summary>
    public float CompatibilityScore => IsPackaged ? 1.0f : (DrmAnalysis?.CompatibilityScore ?? 0.5f);

    /// <summary>
    /// Whether this game is compatible with Goldberg Emulator.
    /// </summary>
    public bool IsGoldbergCompatible => DrmAnalysis?.IsGoldbergCompatible ?? false;

    /// <summary>
    /// Recommended packaging method for this game.
    /// </summary>
    public PackageRecommendation Recommendation => 
        DrmAnalysis?.Recommendation ?? PackageRecommendation.ManualReview;

    /// <summary>
    /// Human-readable explanation of compatibility.
    /// </summary>
    public string CompatibilityReason =>
        IsPackaged ? (IsReceivedPackage ? "Received from peer - ready to play" : "Already packaged") : 
        (DrmAnalysis?.CompatibilityReason ?? "Not yet analyzed");

    /// <summary>
    /// Whether this game can be packaged with current implementation.
    /// Already-packaged games are considered packageable (they're ready to use).
    /// </summary>
    public bool IsPackageable => IsPackaged || 
        (IsFullyInstalled && (IsGoldbergCompatible || Recommendation == PackageRecommendation.DirectCopy));

    /// <summary>
    /// Whether DRM analysis has been performed.
    /// </summary>
    public bool IsAnalyzed => DrmAnalysis != null;

    /// <summary>
    /// Timestamp of last DRM analysis.
    /// </summary>
    public DateTime? LastAnalyzed { get; set; }

    // ============================================
    // UI Helpers
    // ============================================

    /// <summary>
    /// Compatibility badge for UI display.
    /// </summary>
    public string CompatibilityBadge
    {
        get
        {
            if (!IsAnalyzed) return "⚪"; // Not analyzed
            if (CompatibilityScore >= 0.8f) return "🟢"; // High compatibility
            if (CompatibilityScore >= 0.5f) return "🟡"; // Moderate
            if (CompatibilityScore >= 0.2f) return "🟠"; // Low
            return "🔴"; // Not compatible
        }
    }

    /// <summary>
    /// Short status text for UI.
    /// </summary>
    public string StatusText
    {
        get
        {
            if (!IsFullyInstalled) return "Not installed";
            if (!IsAnalyzed) return "Ready to analyze";
            return Recommendation switch
            {
                PackageRecommendation.Goldberg => "Ready to package",
                PackageRecommendation.GoldbergWithConfig => "Needs config",
                PackageRecommendation.DirectCopy => "No DRM",
                PackageRecommendation.ManualReview => "Needs review",
                PackageRecommendation.NotPackageable => "Not packageable",
                _ => "Unknown"
            };
        }
    }

    /// <summary>
    /// Detailed tooltip for UI.
    /// </summary>
    public string DetailedTooltip
    {
        get
        {
            if (!IsAnalyzed)
                return $"AppID: {AppId}\nSize: {FormattedSize}\n\nClick to analyze DRM";

            var lines = new List<string>
            {
                $"AppID: {AppId}",
                $"Size: {FormattedSize}",
                "",
                $"DRM: {PrimaryDrm}",
                $"Compatibility: {CompatibilityScore:P0}",
                "",
                CompatibilityReason
            };

            if (DrmAnalysis?.DetectedDrmList.Count > 1)
            {
                lines.Add("");
                lines.Add("All detected protection:");
                foreach (var drm in DrmAnalysis.DetectedDrmList)
                {
                    lines.Add($"  • {drm.Type}");
                }
            }
            
            if (ReviewPositivePercent.HasValue)
            {
                lines.Add("");
                lines.Add($"Reviews: {ReviewDescription} ({ReviewPositivePercent}%)");
            }

            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Formatted size string (e.g., "15.2 GB").
    /// </summary>
    public string FormattedSize => FormatUtils.FormatBytes(SizeOnDisk);


    // ============================================
    // Analysis Methods
    // ============================================

    /// <summary>
    /// Performs DRM analysis on this game.
    /// </summary>
    public void Analyze()
    {
        if (string.IsNullOrEmpty(FullPath) || !System.IO.Directory.Exists(FullPath))
            return;

        DrmAnalysis = DrmDetector.Analyze(FullPath);
        LastAnalyzed = DateTime.Now;
    }

    /// <summary>
    /// Performs DRM analysis asynchronously.
    /// </summary>
    public Task AnalyzeAsync()
    {
        return Task.Run(Analyze);
    }
}
