using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace SteamRoll.Services;

/// <summary>
/// Manages Windows Defender exclusions for the SteamRoll folders.
/// 
/// WHY ARE DEFENDER EXCLUSIONS NEEDED?
/// ====================================
/// SteamRoll packages games with Steam emulators (Goldberg, CreamAPI) that modify
/// DLL files. Windows Defender may detect these modified DLLs as threats because:
/// 
/// 1. Steam emulators replace/patch steam_api.dll files
/// 2. The emulator DLLs are often flagged as "HackTool" by antivirus software
/// 3. False positives can delete or quarantine essential game files
/// 
/// WHAT IS EXCLUDED?
/// ==================
/// Only the following SteamRoll-specific folders are excluded:
/// - %LOCALAPPDATA%\SteamRoll (emulator downloads and settings)
/// - %TEMP%\goldberg_extract (temporary extraction folder)
/// - %MYDOCUMENTS%\SteamRoll Packages (packaged game output)
/// 
/// Your actual Steam library and other folders are NOT affected.
/// 
/// IS THIS SAFE?
/// ==============
/// Yes. We only exclude SteamRoll's own folders, not system-wide directories.
/// The exclusions can be removed at any time via Windows Security settings.
/// 
/// Requires administrator privileges to add exclusions.
/// </summary>
public static class DefenderExclusionHelper
{
    /// <summary>
    /// Checks if the current process is running with administrator privileges.
    /// </summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds folder exclusions to Windows Defender.
    /// </summary>
    /// <param name="paths">Folder paths to exclude.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public static bool AddExclusions(params string[] paths)
    {
        if (!IsRunningAsAdmin())
        {
            LogService.Instance.Warning("Cannot add Defender exclusions - not running as admin", "Defender");
            return false;
        }

        try
        {
            foreach (var path in paths)
            {
                // Ensure directory exists
                Directory.CreateDirectory(path);
                
                // Add exclusion using PowerShell
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"Add-MpPreference -ExclusionPath '{path}'\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                process?.WaitForExit(10000);
                
                LogService.Instance.Info($"Added Defender exclusion: {path}", "Defender");
            }
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("Error adding Defender exclusions", ex, "Defender");
            return false;
        }
    }

    /// <summary>
    /// Checks if a folder is already excluded in Windows Defender.
    /// </summary>
    public static bool IsExcluded(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"(Get-MpPreference).ExclusionPath -contains '{path}'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(psi);
            var output = process?.StandardOutput.ReadToEnd().Trim();
            process?.WaitForExit(5000);
            
            return output?.Equals("True", StringComparison.OrdinalIgnoreCase) ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the paths that should be excluded for SteamRoll.
    /// </summary>
    public static string[] GetSteamRollExclusionPaths()
    {
        return new[]
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamRoll"),
            System.IO.Path.Combine(Path.GetTempPath(), "goldberg_extract"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamRoll Packages")
        };
    }
    
    /// <summary>
    /// Gets a user-friendly explanation of why Defender exclusions are needed.
    /// </summary>
    public static string GetExplanation()
    {
        return """
            Windows Defender may flag SteamRoll's packaged games as threats.

            WHY?
            • Steam emulators modify DLL files that antivirus software often flags as "HackTool"
            • These are false positives - the emulators are safe tools used for offline gaming

            WHAT GETS EXCLUDED?
            • %LOCALAPPDATA%\SteamRoll  (emulator downloads)
            • %TEMP%\goldberg_extract   (temporary files)
            • %MYDOCUMENTS%\SteamRoll Packages  (packaged games)

            Your Steam library and other folders are NOT affected.
            You can remove these exclusions anytime via Windows Security settings.
            """;
    }
    
    /// <summary>
    /// Checks whether Defender exclusions are needed and not yet applied.
    /// </summary>
    public static bool NeedsExclusions()
    {
        var exclusionPaths = GetSteamRollExclusionPaths();
        return !exclusionPaths.All(IsExcluded);
    }
    
    /// <summary>
    /// Prompts the user with an explanation and asks if they want to add exclusions.
    /// Returns true if user wants to proceed with exclusions.
    /// This method should be called by UI code to get user consent.
    /// </summary>
    public static (bool ShouldProceed, bool NeverAskAgain) GetUserConfirmation(System.Windows.Window owner)
    {
        var result = System.Windows.MessageBox.Show(
            owner,
            $"""
            SteamRoll needs to add Windows Defender exclusions to prevent false positive detections.

            {GetExplanation()}

            Would you like to add these exclusions now?
            (Requires administrator privileges)

            Click 'Yes' to add exclusions.
            Click 'No' to skip (packages may be quarantined by Defender).
            Click 'Cancel' to skip and never ask again.
            """,
            "Windows Defender Exclusions",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Information
        );
        
        return result switch
        {
            System.Windows.MessageBoxResult.Yes => (true, false),
            System.Windows.MessageBoxResult.No => (false, false),
            System.Windows.MessageBoxResult.Cancel => (false, true),
            _ => (false, false)
        };
    }

    /// <summary>
    /// Requests elevation and restarts the application as admin.
    /// </summary>
    /// <returns>True if elevation was initiated.</returns>
    public static bool RequestElevation()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"  // Request UAC elevation
            };

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("Failed to request elevation", ex, "Defender");
            return false;
        }
    }
}
