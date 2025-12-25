namespace SteamRoll.Services;

/// <summary>
/// Types of DRM/protection that can be detected.
/// </summary>
public enum DrmType
{
    None,
    SteamStub,          // Basic Steam API (Goldberg compatible)
    SteamCEG,           // Custom Executable Generation (may work with Goldberg)
    Denuvo,             // Heavy anti-tamper (NOT compatible)
    VMProtect,          // VM-based protection (often NOT compatible)
    Themida,            // Themida/WinLicense (often NOT compatible)
    SecuROM,            // Legacy disc DRM (sometimes removable)
    EpicOnlineServices, // Epic Games integration
    EAOrigin,           // EA Origin/App requirement
    UbisoftConnect,     // Ubisoft Connect requirement
    Custom              // Unknown/custom protection
}

/// <summary>
/// Confidence level for a DRM detection.
/// </summary>
public enum DrmConfidence
{
    Low = 1,      // Heuristic match, may be false positive
    Medium = 2,   // Pattern match with some evidence
    High = 3      // Definitive detection (signature, import, etc.)
}

/// <summary>
/// Recommended bypass/emulation approach for DRM.
/// </summary>
public enum BypassRecommendation
{
    None,               // No bypass needed
    Goldberg,           // Standard Goldberg Emulator
    GoldbergExperimental, // Goldberg with experimental features
    CreamAPI,           // CreamAPI for DLC unlock only
    ManualPatch,        // Requires manual patches/cracks
    NotPossible         // No known bypass method
}

/// <summary>
/// Complete result of DRM analysis.
/// </summary>
public class DrmAnalysisResult
{
    public List<DetectedDrm> DetectedDrmList { get; set; } = new();
    public List<string> SteamApiPaths { get; set; } = new();
    public List<string> AnalysisNotes { get; set; } = new();
    public List<string> AnalysisErrors { get; set; } = new();
    
    public bool HasSteamworksIntegration { get; set; }
    public bool RequiresLauncher { get; set; }
    public bool RequiresOnline { get; set; }
    public int ExecutablesAnalyzed { get; set; }

    // Compatibility assessment
    public bool IsGoldbergCompatible { get; private set; }
    public float CompatibilityScore { get; private set; }
    public string CompatibilityReason { get; private set; } = "";
    public PackageRecommendation Recommendation { get; private set; }

    /// <summary>
    /// Primary DRM type (most restrictive).
    /// </summary>
    public DrmType PrimaryDrm => DetectedDrmList.Count > 0 ? 
        DetectedDrmList.OrderByDescending(d => (int)d.Type).First().Type : 
        DrmType.None;

    public void AddDrm(DrmType type, string evidence)
    {
        if (!DetectedDrmList.Any(d => d.Type == type))
        {
            DetectedDrmList.Add(new DetectedDrm { Type = type, Evidence = evidence });
        }
    }

    /// <summary>
    /// Calculates overall compatibility based on detected DRM.
    /// </summary>
    public void CalculateCompatibility()
    {
        // Start with perfect score
        CompatibilityScore = 1.0f;
        IsGoldbergCompatible = true;
        Recommendation = PackageRecommendation.Goldberg;

        // Check for blocking DRM
        if (DetectedDrmList.Any(d => d.Type == DrmType.Denuvo))
        {
            IsGoldbergCompatible = false;
            CompatibilityScore = 0.0f;
            CompatibilityReason = "Denuvo anti-tamper protection detected - not packageable";
            Recommendation = PackageRecommendation.NotPackageable;
            return;
        }

        if (DetectedDrmList.Any(d => d.Type == DrmType.VMProtect))
        {
            CompatibilityScore = 0.2f;
            CompatibilityReason = "VMProtect detected - unlikely to work with Goldberg";
            Recommendation = PackageRecommendation.ManualReview;
        }

        if (DetectedDrmList.Any(d => d.Type == DrmType.Themida))
        {
            CompatibilityScore = 0.1f;
            CompatibilityReason = "Themida/WinLicense detected - unlikely to work";
            Recommendation = PackageRecommendation.NotPackageable;
            IsGoldbergCompatible = false;
            return;
        }

        // Third-party platforms reduce compatibility
        if (DetectedDrmList.Any(d => d.Type == DrmType.EpicOnlineServices))
        {
            CompatibilityScore -= 0.3f;
            CompatibilityReason = "Epic Online Services integration detected";
        }

        if (DetectedDrmList.Any(d => d.Type == DrmType.EAOrigin))
        {
            CompatibilityScore = 0.1f;
            CompatibilityReason = "EA Origin/App required - not packageable";
            Recommendation = PackageRecommendation.NotPackageable;
            IsGoldbergCompatible = false;
            return;
        }

        if (DetectedDrmList.Any(d => d.Type == DrmType.UbisoftConnect))
        {
            CompatibilityScore = 0.1f;
            CompatibilityReason = "Ubisoft Connect required - not packageable";
            Recommendation = PackageRecommendation.NotPackageable;
            IsGoldbergCompatible = false;
            return;
        }

        // Positive indicators
        if (HasSteamworksIntegration && 
            DetectedDrmList.All(d => d.Type == DrmType.SteamStub || d.Type == DrmType.SteamCEG))
        {
            CompatibilityScore = 0.95f;
            CompatibilityReason = "Standard Steamworks game - should work with Goldberg";
            Recommendation = PackageRecommendation.Goldberg;
        }

        // CEG is slightly lower confidence
        if (DetectedDrmList.Any(d => d.Type == DrmType.SteamCEG))
        {
            CompatibilityScore = 0.7f;
            CompatibilityReason = "Steam CEG detected - may work with Goldberg";
        }

        // Online requirement warning
        if (RequiresOnline)
        {
            CompatibilityScore -= 0.2f;
            if (string.IsNullOrEmpty(CompatibilityReason))
                CompatibilityReason = "Game may require online connection";
        }

        // Launcher warning
        if (RequiresLauncher)
        {
            CompatibilityScore -= 0.1f;
            CompatibilityReason += " May require launcher bypass.";
        }

        // No DRM detected
        if (DetectedDrmList.Count == 0)
        {
            CompatibilityScore = 1.0f;
            CompatibilityReason = "No DRM detected - likely compatible";
            Recommendation = HasSteamworksIntegration ? 
                PackageRecommendation.Goldberg : 
                PackageRecommendation.DirectCopy;
        }

        CompatibilityScore = Math.Clamp(CompatibilityScore, 0f, 1f);
    }
}

/// <summary>
/// A single detected DRM type with evidence and confidence.
/// </summary>
public class DetectedDrm
{
    public DrmType Type { get; set; }
    public string Evidence { get; set; } = "";
    public DrmConfidence Confidence { get; set; } = DrmConfidence.Medium;
    
    /// <summary>
    /// Recommended bypass method for this specific DRM.
    /// </summary>
    public BypassRecommendation RecommendedBypass => GetBypassRecommendation(Type);
    
    /// <summary>
    /// Display string for the DRM type.
    /// </summary>
    public string TypeDisplay => GetDrmDisplayName(Type);
    
    /// <summary>
    /// Display string for confidence level.
    /// </summary>
    public string ConfidenceDisplay => Confidence switch
    {
        DrmConfidence.Low => "🟡 Low",
        DrmConfidence.Medium => "🟠 Medium",
        DrmConfidence.High => "🔴 High",
        _ => "Unknown"
    };
    
    /// <summary>
    /// Display string for bypass recommendation.
    /// </summary>
    public string BypassDisplay => RecommendedBypass switch
    {
        BypassRecommendation.None => "✅ Not needed",
        BypassRecommendation.Goldberg => "🟢 Goldberg",
        BypassRecommendation.GoldbergExperimental => "🟡 Goldberg (Exp)",
        BypassRecommendation.CreamAPI => "🔵 CreamAPI",
        BypassRecommendation.ManualPatch => "🟠 Manual",
        BypassRecommendation.NotPossible => "🔴 Not possible",
        _ => "Unknown"
    };
    
    /// <summary>
    /// Gets the recommended bypass for a DRM type.
    /// </summary>
    public static BypassRecommendation GetBypassRecommendation(DrmType type) => type switch
    {
        DrmType.None => BypassRecommendation.None,
        DrmType.SteamStub => BypassRecommendation.Goldberg,
        DrmType.SteamCEG => BypassRecommendation.GoldbergExperimental,
        DrmType.Denuvo => BypassRecommendation.NotPossible,
        DrmType.VMProtect => BypassRecommendation.ManualPatch,
        DrmType.Themida => BypassRecommendation.NotPossible,
        DrmType.SecuROM => BypassRecommendation.ManualPatch,
        DrmType.EpicOnlineServices => BypassRecommendation.ManualPatch,
        DrmType.EAOrigin => BypassRecommendation.NotPossible,
        DrmType.UbisoftConnect => BypassRecommendation.NotPossible,
        DrmType.Custom => BypassRecommendation.ManualPatch,
        _ => BypassRecommendation.ManualPatch
    };
    
    /// <summary>
    /// Gets a human-readable name for a DRM type.
    /// </summary>
    public static string GetDrmDisplayName(DrmType type) => type switch
    {
        DrmType.None => "None",
        DrmType.SteamStub => "Steam API",
        DrmType.SteamCEG => "Steam CEG",
        DrmType.Denuvo => "⚠️ Denuvo",
        DrmType.VMProtect => "VMProtect",
        DrmType.Themida => "Themida",
        DrmType.SecuROM => "SecuROM",
        DrmType.EpicOnlineServices => "Epic Online Services",
        DrmType.EAOrigin => "EA App/Origin",
        DrmType.UbisoftConnect => "Ubisoft Connect",
        DrmType.Custom => "Custom DRM",
        _ => "Unknown"
    };
}

/// <summary>
/// Recommended packaging approach.
/// </summary>
public enum PackageRecommendation
{
    Goldberg,           // Standard Goldberg replacement
    GoldbergWithConfig, // Goldberg with special configuration
    DirectCopy,         // No DRM - just copy files
    ManualReview,       // Needs manual testing
    NotPackageable      // Cannot be packaged
}
