using System.IO;
using System.Security.Cryptography;
using SteamRoll.Models;
using SteamRoll.Parsers;

namespace SteamRoll.Services;

/// <summary>
/// Service for analyzing game package health and compatibility.
/// Performs checks for architecture mismatches, missing dependencies, and potential issues.
/// </summary>
public class DiagnosticService
{
    private static readonly Lazy<DiagnosticService> _instance = new(() => new DiagnosticService());
    public static DiagnosticService Instance => _instance.Value;

    public DiagnosticService() { }

    /// <summary>
    /// Analyzes a game package directory and returns a comprehensive health report.
    /// </summary>
    /// <param name="packagePath">The root directory of the game package.</param>
    public async Task<DiagnosticReport> AnalyzePackageAsync(string packagePath)
    {
        var report = new DiagnosticReport
        {
            PackagePath = packagePath,
            Timestamp = DateTime.UtcNow
        };

        await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(packagePath))
                {
                    report.Issues.Add(new HealthIssue(IssueSeverity.Error, "Package directory not found", "The specified path does not exist."));
                    return;
                }

                // 1. Find Executables
                var exes = Directory.GetFiles(packagePath, "*.exe", SearchOption.AllDirectories);
                if (exes.Length == 0)
                {
                    report.Issues.Add(new HealthIssue(IssueSeverity.Error, "No Executable Found", "No .exe files found in the package."));
                    return;
                }

                // Identify Main Executable (heuristic)
                var mainExe = FindMainExecutable(packagePath, exes);
                if (mainExe != null)
                {
                    report.MainExecutable = Path.GetFileName(mainExe);
                    AnalyzeExecutable(packagePath, mainExe, report);
                }
                else
                {
                    report.Issues.Add(new HealthIssue(IssueSeverity.Warning, "Main Executable Uncertain", "Could not definitively identify the main game executable."));
                }

                // 2. Check Steam AppID
                CheckSteamAppId(packagePath, report);

                // 3. Check for Junk/Redistributables
                CheckForJunkFiles(packagePath, report);

                // 4. Check for DRM Leftovers
                CheckForDrmLeftovers(packagePath, report);

            }
            catch (Exception ex)
            {
                report.Issues.Add(new HealthIssue(IssueSeverity.Error, "Analysis Failed", $"An error occurred during analysis: {ex.Message}"));
            }
        });

        return report;
    }

    private string? FindMainExecutable(string packagePath, string[] exes)
    {
        // Sort by size, descending
        var sorted = exes.OrderByDescending(e => new FileInfo(e).Length).ToList();

        // Filter out obvious non-game exes
        var candidates = sorted.Where(e =>
        {
            var name = Path.GetFileName(e).ToLowerInvariant();
            return !name.Contains("redist") &&
                   !name.Contains("setup") &&
                   !name.Contains("unins") &&
                   !name.Contains("crash") &&
                   !name.Contains("unitycrashhandler");
        }).ToList();

        return candidates.FirstOrDefault();
    }

    private void AnalyzeExecutable(string packagePath, string exePath, DiagnosticReport report)
    {
        try
        {
            var pe = PeParser.Parse(exePath);
            if (pe == null)
            {
                report.Issues.Add(new HealthIssue(IssueSeverity.Warning, "Executable Parse Failed", $"Could not parse {Path.GetFileName(exePath)}."));
                return;
            }

            bool is64Bit = pe.Is64Bit;
            report.Architecture = pe.GetArchitecture();

            // Check Steam API DLL architecture match
            var steamApiName = is64Bit ? "steam_api64.dll" : "steam_api.dll";
            var wrongApiName = is64Bit ? "steam_api.dll" : "steam_api64.dll";

            var steamApiPaths = Directory.GetFiles(packagePath, steamApiName, SearchOption.AllDirectories);
            var wrongApiPaths = Directory.GetFiles(packagePath, wrongApiName, SearchOption.AllDirectories);

            if (steamApiPaths.Length == 0 && pe.ImportsDll(steamApiName))
            {
                report.Issues.Add(new HealthIssue(IssueSeverity.Error, "Missing Steam API DLL",
                    $"The game imports {steamApiName} but it was not found in the package."));
            }

            // If the game imports the WRONG arch (unlikely but possible if we misidentified main exe or it's a launcher)
            if (pe.ImportsDll(wrongApiName))
            {
                 report.Issues.Add(new HealthIssue(IssueSeverity.Warning, "Architecture Mismatch",
                    $"The executable is {report.Architecture} but seems to import {wrongApiName}."));
            }

            // Check if found DLLs are valid PE files and match arch
            foreach (var dllPath in steamApiPaths)
            {
                var dllPe = PeParser.Parse(dllPath);
                if (dllPe != null && dllPe.Is64Bit != is64Bit)
                {
                     report.Issues.Add(new HealthIssue(IssueSeverity.Error, "DLL Architecture Mismatch",
                        $"Found {Path.GetFileName(dllPath)} which is {(dllPe.Is64Bit ? "64-bit" : "32-bit")}, but game is {report.Architecture}."));
                }
            }
        }
        catch (Exception ex)
        {
            report.Issues.Add(new HealthIssue(IssueSeverity.Warning, "Analysis Error", $"Failed to analyze executable: {ex.Message}"));
        }
    }

    private void CheckSteamAppId(string packagePath, DiagnosticReport report)
    {
        var appidPath = Path.Combine(packagePath, "steam_appid.txt");
        if (!File.Exists(appidPath))
        {
            report.Issues.Add(new HealthIssue(IssueSeverity.Error, "Missing steam_appid.txt",
                "Goldberg Emulator requires steam_appid.txt to function correctly.",
                canFix: true, fixAction: "Create steam_appid.txt"));
        }
        else
        {
            var content = File.ReadAllText(appidPath).Trim();
            if (!int.TryParse(content, out _))
            {
                 report.Issues.Add(new HealthIssue(IssueSeverity.Error, "Invalid steam_appid.txt",
                    "The file steam_appid.txt does not contain a valid numeric AppID."));
            }
        }
    }

    private void CheckForJunkFiles(string packagePath, DiagnosticReport report)
    {
        var junkPatterns = new[] { "CommonRedist", "_CommonRedist", "DirectX", "DotNet", "VCRedist" };

        foreach (var pattern in junkPatterns)
        {
            var dirs = Directory.GetDirectories(packagePath, pattern, SearchOption.AllDirectories);
            if (dirs.Length > 0)
            {
                report.Issues.Add(new HealthIssue(IssueSeverity.Info, "Redistributable Files Found",
                    $"Found installer folders ({pattern}) which take up space. The package script likely already created an install script.",
                    canFix: false)); // We could make this fixable (delete) but risky
            }
        }
    }

    private void CheckForDrmLeftovers(string packagePath, DiagnosticReport report)
    {
        var leftovers = new[] { "steam_api.dll.original", "steam_api64.dll.original", "steam_api.dll.bak", "steam_api64.dll.bak" };

        foreach (var file in leftovers)
        {
             var paths = Directory.GetFiles(packagePath, file, SearchOption.AllDirectories);
             if (paths.Length > 0)
             {
                 report.Issues.Add(new HealthIssue(IssueSeverity.Info, "Backup Files Found",
                     $"Found backup file {file}. It is safe to delete to save space.",
                     canFix: true, fixAction: "Delete backup files"));
             }
        }
    }
}
