namespace SteamRoll.Models;

public class DiagnosticReport
{
    public string PackagePath { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string MainExecutable { get; set; } = "Unknown";
    public string Architecture { get; set; } = "Unknown";
    public List<HealthIssue> Issues { get; set; } = new();

    public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);

    public string StatusSummary
    {
        get
        {
            if (ErrorCount > 0) return $"Critical Issues Found ({ErrorCount})";
            if (WarningCount > 0) return $"Warnings Found ({WarningCount})";
            return "Healthy";
        }
    }
}

public class HealthIssue
{
    public IssueSeverity Severity { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public bool CanFix { get; set; }
    public string? FixAction { get; set; }

    public HealthIssue(IssueSeverity severity, string title, string description, bool canFix = false, string? fixAction = null)
    {
        Severity = severity;
        Title = title;
        Description = description;
        CanFix = canFix;
        FixAction = fixAction;
    }
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error
}
