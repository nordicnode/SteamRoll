using System.IO;
using SteamRoll.Parsers;

namespace SteamRoll.Services;

/// <summary>
/// Detects DRM protection types in game executables.
/// Uses PE analysis, import scanning, section detection, and string matching.
/// </summary>
public class DrmDetector
{
    /// <summary>
    /// Analyzes a game installation and detects all DRM types present.
    /// </summary>
    /// <param name="gamePath">Path to the game installation folder.</param>
    /// <param name="mainExe">Optional specific executable to analyze.</param>
    /// <returns>Complete DRM analysis result.</returns>
    public static DrmAnalysisResult Analyze(string gamePath, string? mainExe = null)
    {
        var result = new DrmAnalysisResult();
        
        try
        {
            // Find executables to analyze
            var executables = FindGameExecutables(gamePath);
            if (mainExe != null && File.Exists(mainExe))
            {
                executables.Insert(0, mainExe);
            }

            result.ExecutablesAnalyzed = executables.Count;

            // Analyze each executable
            foreach (var exePath in executables)
            {
                AnalyzeExecutable(exePath, result);
            }

            // Check for Steam API DLLs
            CheckSteamApiPresence(gamePath, result);

            // Check for known DRM-related files
            CheckDrmFiles(gamePath, result);

            // Calculate final compatibility
            result.CalculateCompatibility();
        }
        catch (Exception ex)
        {
            result.AnalysisErrors.Add($"Analysis failed: {ex.Message}");
        }

        return result;
    }

    private static List<string> FindGameExecutables(string gamePath)
    {
        var executables = new List<string>();
        
        try
        {
            var exeFiles = Directory.GetFiles(gamePath, "*.exe", SearchOption.AllDirectories);
            
            // Prioritize likely game executables, exclude installers/helpers
            foreach (var exe in exeFiles)
            {
                var fileName = System.IO.Path.GetFileName(exe).ToLowerInvariant();
                
                // Skip common non-game executables
                if (fileName.Contains("unins") ||
                    fileName.Contains("redist") ||
                    fileName.Contains("setup") ||
                    fileName.Contains("vcredist") ||
                    fileName.Contains("dxsetup") ||
                    fileName.Contains("directx") ||
                    fileName.StartsWith("crashhandler") ||
                    fileName.StartsWith("unitycrashhandler") ||
                    fileName.Contains("reporter"))
                {
                    continue;
                }

                executables.Add(exe);
            }

            // Sort by size (larger exes are usually the main game)
            executables.Sort((a, b) => 
                new FileInfo(b).Length.CompareTo(new FileInfo(a).Length));

            // Identify potential launchers that might be small but important
            var launchers = executables.Where(e => 
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(e).ToLowerInvariant();
                return name.Contains("launcher") || name.Contains("start") || name.Equals("game");
            }).ToList();

            // Take top 5 largest + any identified launchers
            var topLargest = executables.Take(5).ToList();
            executables = topLargest.Union(launchers).Distinct().ToList();
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("Error finding game executables", ex);
        }

        return executables;
    }

    private static void AnalyzeExecutable(string exePath, DrmAnalysisResult result)
    {
        var pe = PeParser.Parse(exePath);
        if (pe == null) return;

        var fileName = System.IO.Path.GetFileName(exePath);

        // Check for Steam DRM
        if (pe.ImportsDll("steam_api.dll") || pe.ImportsDll("steam_api64.dll"))
        {
            result.AddDrm(DrmType.SteamStub, $"Steam API imports in {fileName}");
            result.HasSteamworksIntegration = true;

            // Check for Steam CEG (Custom Executable Generation)
            // CEG has encrypted sections and specific patterns
            if (HasSteamCegPattern(pe))
            {
                result.AddDrm(DrmType.SteamCEG, $"Steam CEG detected in {fileName}");
            }
        }

        // Check for Denuvo
        if (HasDenuvoPattern(pe, exePath))
        {
            result.AddDrm(DrmType.Denuvo, $"Denuvo Anti-Tamper in {fileName}");
        }

        // Check for VMProtect
        if (HasVmProtectPattern(pe))
        {
            result.AddDrm(DrmType.VMProtect, $"VMProtect in {fileName}");
        }

        // Check for Themida/WinLicense
        if (HasThemidaPattern(pe))
        {
            result.AddDrm(DrmType.Themida, $"Themida/WinLicense in {fileName}");
        }

        // Check for SecuROM (legacy)
        if (HasSecuRomPattern(pe, exePath))
        {
            result.AddDrm(DrmType.SecuROM, $"SecuROM in {fileName}");
        }

        // Check for Epic Online Services
        if (pe.ImportsDll("eossdk-win64-shipping.dll") || 
            pe.ImportsDll("eossdk-win32-shipping.dll"))
        {
            result.AddDrm(DrmType.EpicOnlineServices, $"Epic Online Services in {fileName}");
        }

        // Check for EA Origin/App
        if (pe.ImportsDll("origin.dll") || pe.ImportsDll("eadesktopbridge.dll"))
        {
            result.AddDrm(DrmType.EAOrigin, $"EA Origin/App in {fileName}");
        }

        // Check for Ubisoft Connect
        if (pe.ImportsDll("uplay_r1_loader.dll") || pe.ImportsDll("upc.dll"))
        {
            result.AddDrm(DrmType.UbisoftConnect, $"Ubisoft Connect in {fileName}");
        }

        // Check for online-only patterns (always-online DRM)
        if (HasOnlineOnlyPattern(pe))
        {
            result.RequiresOnline = true;
        }

        // Large overlay can indicate packed/protected exe
        if (pe.OverlaySize > 10_000_000) // > 10MB overlay
        {
            result.AnalysisNotes.Add($"{fileName} has large overlay ({pe.OverlaySize / 1024 / 1024}MB) - may be packed");
        }
    }

    private static bool HasSteamCegPattern(PeParser pe)
    {
        // Steam CEG typically:
        // 1. Has encrypted .bind section
        // 2. Has specific import patterns
        // 3. Entry point in unusual section

        foreach (var section in pe.Sections)
        {
            if (section.Name.Equals(".bind", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool HasDenuvoPattern(PeParser pe, string exePath)
    {
        // Denuvo indicators:
        // 1. Section named ".arch" or other Denuvo-specific sections
        // 2. Very large executable (adds 50-100MB)
        // 3. Strings containing "denuvo" or "irdeto"
        // 4. Imports from steam_api but with heavy obfuscation

        foreach (var section in pe.Sections)
        {
            var name = section.Name.ToLowerInvariant();
            if (name.Contains(".arch") || name.Contains("denuvo"))
                return true;
        }

        // Check for Denuvo strings (case insensitive)
        if (pe.ContainsString("denuvo", true) || 
            pe.ContainsString("irdeto", true))
        {
            return true;
        }

        // Large Steam-using exe might indicate Denuvo (heuristic)
        if (pe.FileSize > 100_000_000 && // > 100MB
            (pe.ImportsDll("steam_api.dll") || pe.ImportsDll("steam_api64.dll")))
        {
            // Not definitive, just a note
        }

        return false;
    }

    private static bool HasVmProtectPattern(PeParser pe)
    {
        // VMProtect patterns:
        // 1. Sections named ".vmp0", ".vmp1", ".vmp2"
        // 2. Section with high entropy (packed/encrypted code)

        foreach (var section in pe.Sections)
        {
            var name = section.Name.ToLowerInvariant();
            if (name.StartsWith(".vmp") || name.Contains("vmp"))
                return true;
        }

        // Check for VMProtect import
        if (pe.ImportsDll("vmprotectsdk32.dll") || pe.ImportsDll("vmprotectsdk64.dll"))
            return true;

        return false;
    }

    private static bool HasThemidaPattern(PeParser pe)
    {
        // Themida/WinLicense patterns
        foreach (var section in pe.Sections)
        {
            var name = section.Name.ToLowerInvariant();
            if (name.Contains("themida") || 
                name.Contains("winlicen") ||
                name.Contains(".taggant"))
                return true;
        }

        return pe.ContainsString("themida", true) || 
               pe.ContainsString("winlicense", true);
    }

    private static bool HasSecuRomPattern(PeParser pe, string exePath)
    {
        // SecuROM (legacy, mostly pre-2015 games)
        // Look for CMS files or specific patterns
        var gameDir = System.IO.Path.GetDirectoryName(exePath);
        if (gameDir != null)
        {
            if (Directory.GetFiles(gameDir, "*.cms").Length > 0)
                return true;
        }

        return pe.ContainsString("securom", true) ||
               pe.ContainsString("sony dadc", true);
    }

    private static bool HasOnlineOnlyPattern(PeParser pe)
    {
        // Check for patterns that suggest online-only requirement
        // (This is heuristic and may have false positives)

        var onlineIndicators = new[]
        {
            "onlineauthentication",
            "connectionrequired",
            "offlineprohibited"
        };

        return pe.ImportedFunctions.Any(f => 
            onlineIndicators.Any(i => f.Contains(i, StringComparison.OrdinalIgnoreCase)));
    }

    private static void CheckSteamApiPresence(string gamePath, DrmAnalysisResult result)
    {
        var steamApiFiles = new[]
        {
            "steam_api.dll",
            "steam_api64.dll"
        };

        foreach (var fileName in steamApiFiles)
        {
            try
            {
                var files = Directory.GetFiles(gamePath, fileName, SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    result.SteamApiPaths.AddRange(files);
                    result.HasSteamworksIntegration = true;
                    
                    // Add SteamStub DRM type when steam_api DLLs are present
                    // (Most games load these dynamically, not via PE imports)
                    result.AddDrm(DrmType.SteamStub, $"Steam API found: {fileName}");
                }
            }
            catch (Exception ex)
            {
                // Permission issues on some folders are expected
                LogService.Instance.Debug($"Could not scan for {fileName}: {ex.Message}", "DrmDetector");
            }
        }
        
        // Also check for steamclient DLLs (additional indicator)
        var clientFiles = new[] { "steamclient.dll", "steamclient64.dll" };
        foreach (var fileName in clientFiles)
        {
            try
            {
                var files = Directory.GetFiles(gamePath, fileName, SearchOption.AllDirectories);
                result.SteamApiPaths.AddRange(files);
            }
            catch (Exception ex)
            {
                LogService.Instance.Debug($"Could not scan for {fileName}: {ex.Message}", "DrmDetector");
            }
        }
    }

    private static void CheckDrmFiles(string gamePath, DrmAnalysisResult result)
    {
        try
        {
            // Check for common DRM-related files using lazy enumeration for memory efficiency
            foreach (var file in Directory.EnumerateFiles(gamePath, "*", SearchOption.AllDirectories))
            {
                var fileName = System.IO.Path.GetFileName(file).ToLowerInvariant();

                // Denuvo trigger file
                if (fileName == "denuvo.dll" || fileName.Contains("denuvo"))
                {
                    result.AddDrm(DrmType.Denuvo, $"Denuvo file: {fileName}");
                }

                // Custom launcher indicators
                if (fileName.Contains("launcher") && fileName.EndsWith(".exe"))
                {
                    result.RequiresLauncher = true;
                    result.AnalysisNotes.Add($"Game may require launcher: {fileName}");
                }
            }
        }
        catch (Exception ex)
        {
            // Best effort - log but continue
            LogService.Instance.Debug($"Could not scan DRM files in {gamePath}: {ex.Message}", "DrmDetector");
        }
    }
}
