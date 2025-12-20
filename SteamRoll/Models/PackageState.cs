namespace SteamRoll.Models;

/// <summary>
/// Represents the current step in the packaging process for resumable packaging.
/// </summary>
public enum PackagingStep
{
    /// <summary>Initial state - packaging not started.</summary>
    NotStarted = 0,
    
    /// <summary>Copying game files to package directory.</summary>
    CopyingFiles = 1,
    
    /// <summary>Detecting Steam interfaces from original DLLs.</summary>
    DetectingInterfaces = 2,
    
    /// <summary>Applying emulator (Goldberg or CreamAPI).</summary>
    ApplyingEmulator = 3,
    
    /// <summary>Configuring DLC unlock.</summary>
    ConfiguringDlc = 4,
    
    /// <summary>Creating package metadata and README.</summary>
    CreatingMetadata = 5,
    
    /// <summary>Creating launcher script.</summary>
    CreatingLauncher = 6,
    
    /// <summary>Creating ZIP archive.</summary>
    CreatingZip = 7,
    
    /// <summary>Package complete.</summary>
    Complete = 8
}

/// <summary>
/// Tracks the state of an in-progress or incomplete package for resumable packaging.
/// Saved to .steamroll_progress.json in the package directory.
/// </summary>
public class PackageState
{
    /// <summary>Steam App ID of the game being packaged.</summary>
    public int AppId { get; set; }
    
    /// <summary>Name of the game.</summary>
    public string GameName { get; set; } = "";
    
    /// <summary>Full path to the source game directory.</summary>
    public string SourcePath { get; set; } = "";
    
    /// <summary>Full path to the package directory.</summary>
    public string PackagePath { get; set; } = "";
    
    /// <summary>Current step in the packaging process.</summary>
    public PackagingStep CurrentStep { get; set; } = PackagingStep.NotStarted;
    
    /// <summary>Packaging mode (Goldberg or CreamAPI).</summary>
    public string Mode { get; set; } = "Goldberg";
    
    /// <summary>Timestamp when packaging was started.</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>Timestamp of last progress update.</summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>Number of files successfully copied (for file copy resumption).</summary>
    public int FilesCopied { get; set; }
    
    /// <summary>Total files to copy (for progress calculation).</summary>
    public int TotalFiles { get; set; }
    
    /// <summary>Whether the package state is considered expired (older than 7 days).</summary>
    public bool IsExpired => (DateTime.UtcNow - LastUpdatedAt).TotalDays > 7;
    
    /// <summary>Progress file name constant.</summary>
    public const string ProgressFileName = ".steamroll_progress.json";
}
