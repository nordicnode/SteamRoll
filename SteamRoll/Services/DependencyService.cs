using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SteamRoll.Services;

/// <summary>
/// Information about a runtime dependency.
/// </summary>
public class DependencyInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string InstallerUrl { get; set; } = string.Empty;
    public string LocalInstallerPath { get; set; } = string.Empty;
    public bool IsInstalled { get; set; }
    public bool IsRequired { get; set; }
    public DependencyType Type { get; set; }
}

public enum DependencyType
{
    VcRedist2015to2022_x64,
    VcRedist2015to2022_x86,
    VcRedist2013_x64,
    VcRedist2013_x86,
    VcRedist2012_x64,
    VcRedist2012_x86,
    VcRedist2010_x64,
    VcRedist2010_x86,
    VcRedist2008_x64,
    VcRedist2008_x86,
    DirectX,
    DotNet6,
    DotNet7,
    DotNet8,
    PhysX
}

/// <summary>
/// Service to detect and repair missing game dependencies like VC++ redistributables.
/// </summary>
public class DependencyService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _downloadPath;

    // Official Microsoft download URLs for VC++ redistributables
    private static readonly Dictionary<DependencyType, DependencyDefinition> DependencyDefinitions = new()
    {
        [DependencyType.VcRedist2015to2022_x64] = new DependencyDefinition
        {
            Name = "Visual C++ 2015-2022 Redistributable (x64)",
            Url = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
            RegistryPath = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64",
            MinVersion = new Version(14, 38)
        },
        [DependencyType.VcRedist2015to2022_x86] = new DependencyDefinition
        {
            Name = "Visual C++ 2015-2022 Redistributable (x86)",
            Url = "https://aka.ms/vs/17/release/vc_redist.x86.exe",
            RegistryPath = @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X86",
            MinVersion = new Version(14, 38)
        },
        [DependencyType.VcRedist2013_x64] = new DependencyDefinition
        {
            Name = "Visual C++ 2013 Redistributable (x64)",
            Url = "https://aka.ms/highdpimfc2013x64enu",
            RegistryPath = @"SOFTWARE\Microsoft\VisualStudio\12.0\VC\Runtimes\X64",
            MinVersion = new Version(12, 0)
        },
        [DependencyType.VcRedist2013_x86] = new DependencyDefinition
        {
            Name = "Visual C++ 2013 Redistributable (x86)",
            Url = "https://aka.ms/highdpimfc2013x86enu",
            RegistryPath = @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\12.0\VC\Runtimes\X86",
            MinVersion = new Version(12, 0)
        },
        [DependencyType.DirectX] = new DependencyDefinition
        {
            Name = "DirectX End-User Runtime",
            Url = "https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe",
            RegistryPath = "", // DirectX is harder to detect, usually present in Windows 10+
            MinVersion = new Version()
        }
    };

    public DependencyService()
    {
        _downloadPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamRoll", "Dependencies");
        Directory.CreateDirectory(_downloadPath);
    }

    /// <summary>
    /// Detects which dependencies are required for a game based on DLLs found in the game folder.
    /// Uses PE header parsing to detect x86 vs x64 architecture and only install required redistributables.
    /// </summary>
    public async Task<List<DependencyInfo>> DetectRequiredDependenciesAsync(string gamePath)
    {
        var dependencies = new HashSet<DependencyType>();

        try
        {
            // Get full paths for architecture detection
            var allDllPaths = Directory.GetFiles(gamePath, "*.dll", SearchOption.AllDirectories);
            
            // Create lookup sets for pattern matching
            var dllFileNames = allDllPaths
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Define patterns and their corresponding dependency types
            var vcpp2015PlusPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "vcruntime140.dll", "vcruntime140d.dll", 
                "vcruntime140_1.dll", "msvcp140.dll",
                "concrt140.dll", "vccorlib140.dll"
            };

            var vcpp2013Patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "msvcr120.dll", "msvcp120.dll", "vccorlib120.dll"
            };

            var vcpp2012Patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { 
                "msvcr110.dll", "msvcp110.dll" 
            };
            
            var vcpp2010Patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { 
                "msvcr100.dll", "msvcp100.dll" 
            };
            
            var vcpp2008Patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { 
                "msvcr90.dll", "msvcp90.dll" 
            };
            
            var physxPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { 
                "physxloader.dll", "physxcooking.dll", "physxcore.dll" 
            };

            // Process each DLL to detect required dependencies with correct architecture
            foreach (var dllPath in allDllPaths)
            {
                var fileName = Path.GetFileName(dllPath);
                if (string.IsNullOrEmpty(fileName)) continue;

                // Determine which VC++ version this DLL belongs to
                (DependencyType x64Type, DependencyType x86Type)? vcppTypes = null;

                if (vcpp2015PlusPatterns.Contains(fileName))
                    vcppTypes = (DependencyType.VcRedist2015to2022_x64, DependencyType.VcRedist2015to2022_x86);
                else if (vcpp2013Patterns.Contains(fileName))
                    vcppTypes = (DependencyType.VcRedist2013_x64, DependencyType.VcRedist2013_x86);
                else if (vcpp2012Patterns.Contains(fileName))
                    vcppTypes = (DependencyType.VcRedist2012_x64, DependencyType.VcRedist2012_x86);
                else if (vcpp2010Patterns.Contains(fileName))
                    vcppTypes = (DependencyType.VcRedist2010_x64, DependencyType.VcRedist2010_x86);
                else if (vcpp2008Patterns.Contains(fileName))
                    vcppTypes = (DependencyType.VcRedist2008_x64, DependencyType.VcRedist2008_x86);

                if (vcppTypes.HasValue)
                {
                    // Use PE parser to detect architecture
                    var is64Bit = DetectDllArchitecture(dllPath);
                    
                    if (is64Bit.HasValue)
                    {
                        dependencies.Add(is64Bit.Value ? vcppTypes.Value.x64Type : vcppTypes.Value.x86Type);
                    }
                    else
                    {
                        // Fallback: if we can't determine architecture, install both (safe)
                        dependencies.Add(vcppTypes.Value.x64Type);
                        dependencies.Add(vcppTypes.Value.x86Type);
                    }
                }
            }

            // Detect PhysX (doesn't need architecture-specific handling)
            if (physxPatterns.Any(p => dllFileNames.Contains(p)))
            {
                dependencies.Add(DependencyType.PhysX);
            }

            // Convert to DependencyInfo list and check installation status
            var result = new List<DependencyInfo>();
            foreach (var depType in dependencies)
            {
                var info = CreateDependencyInfo(depType);
                info.IsInstalled = await Task.Run(() => IsDependencyInstalled(depType));
                result.Add(info);
            }

            var archBreakdown = result.Count(d => d.Type.ToString().EndsWith("x64")) + " x64, " +
                               result.Count(d => d.Type.ToString().EndsWith("x86")) + " x86";
            LogService.Instance.Info($"Detected {result.Count} dependencies ({archBreakdown}) for game at {gamePath}", "DependencyService");

            return result;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to detect dependencies: {ex.Message}", ex, "DependencyService");
            return new List<DependencyInfo>();
        }
    }

    /// <summary>
    /// Detects whether a DLL is 64-bit or 32-bit by parsing its PE header.
    /// Returns null if unable to determine.
    /// </summary>
    private bool? DetectDllArchitecture(string dllPath)
    {
        try
        {
            var pe = Parsers.PeParser.Parse(dllPath);
            if (pe != null && pe.IsValid)
            {
                return pe.Is64Bit;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Failed to detect architecture for {dllPath}: {ex.Message}", "DependencyService");
        }
        return null;
    }

    /// <summary>
    /// Checks if a specific dependency is already installed on the system.
    /// </summary>
    public bool IsDependencyInstalled(DependencyType type)
    {
        if (!DependencyDefinitions.TryGetValue(type, out var definition))
            return true; // Unknown dependency, assume installed

        if (string.IsNullOrEmpty(definition.RegistryPath))
            return true; // No registry check available, assume installed (e.g., DirectX on modern Windows)

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(definition.RegistryPath);
            if (key == null) return false;

            var installedValue = key.GetValue("Installed");
            if (installedValue is int installed && installed != 1)
                return false;

            // Check version if available
            var majorValue = key.GetValue("Major");
            var minorValue = key.GetValue("Minor");
            if (majorValue is int major && minorValue is int minor)
            {
                var installedVersion = new Version(major, minor);
                return installedVersion >= definition.MinVersion;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Registry check failed for {type}: {ex.Message}", "DependencyService");
            return false;
        }
    }

    /// <summary>
    /// Downloads a dependency installer to the local cache.
    /// </summary>
    public async Task<string?> DownloadDependencyAsync(DependencyType type, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!DependencyDefinitions.TryGetValue(type, out var definition))
        {
            LogService.Instance.Warning($"No definition found for dependency type {type}", "DependencyService");
            return null;
        }

        var fileName = $"{type}.exe";
        var localPath = Path.Combine(_downloadPath, fileName);

        // Check if already downloaded
        if (File.Exists(localPath))
        {
            LogService.Instance.Debug($"Dependency {type} already cached at {localPath}", "DependencyService");
            return localPath;
        }

        try
        {
            LogService.Instance.Info($"Downloading {definition.Name}...", "DependencyService");
            
            using var response = await HttpClient.GetAsync(definition.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var buffer = new byte[8192];
            var bytesRead = 0L;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            int read;
            while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                bytesRead += read;

                if (totalBytes > 0 && progress != null)
                {
                    progress.Report((double)bytesRead / totalBytes);
                }
            }

            LogService.Instance.Info($"Downloaded {definition.Name} to {localPath}", "DependencyService");
            return localPath;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to download {definition.Name}: {ex.Message}", ex, "DependencyService");
            
            // Clean up partial download
            if (File.Exists(localPath))
            {
                try { File.Delete(localPath); } catch { }
            }
            
            return null;
        }
    }

    /// <summary>
    /// Installs a dependency silently.
    /// Uses different command-line arguments based on installer type:
    /// - Modern VC++ (2012+): Uses Burn engine with /install /quiet /norestart
    /// - Legacy VC++ (2008/2010): Uses older installer with /q /norestart
    /// - DirectX: Self-extracting archive that needs extract + DXSETUP.exe /silent
    /// </summary>
    public async Task<bool> InstallDependencyAsync(DependencyType type, IProgress<string>? status = null)
    {
        var installerPath = await DownloadDependencyAsync(type);
        if (string.IsNullOrEmpty(installerPath))
        {
            return false;
        }

        // Declare tempDir outside try block so finally can access it for cleanup
        string? tempDir = null;

        try
        {
            status?.Report($"Installing {DependencyDefinitions[type].Name}...");

            // Determine arguments based on installer type
            string args;
            string exeToRun = installerPath;
            string? workingDir = null;

            switch (type)
            {
                // Modern Burn Engine (2012+) - uses /install flag
                case DependencyType.VcRedist2012_x64:
                case DependencyType.VcRedist2012_x86:
                case DependencyType.VcRedist2013_x64:
                case DependencyType.VcRedist2013_x86:
                case DependencyType.VcRedist2015to2022_x64:
                case DependencyType.VcRedist2015to2022_x86:
                    args = "/install /quiet /norestart";
                    break;

                // Legacy Installers (2008/2010) - do NOT understand /install
                case DependencyType.VcRedist2008_x64:
                case DependencyType.VcRedist2008_x86:
                case DependencyType.VcRedist2010_x64:
                case DependencyType.VcRedist2010_x86:
                    args = "/q /norestart";
                    break;

                // DirectX (Special Case: Self-Extracting Archive -> Extract + Install)
                case DependencyType.DirectX:
                    tempDir = Path.Combine(Path.GetTempPath(), $"SteamRoll_DX_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(tempDir);

                    // Step A: Extract the self-extracting archive
                    status?.Report("Extracting DirectX files...");
                    var extractArgs = $"/Q /T:\"{tempDir}\"";
                    var extractSuccess = await RunProcessAsync(installerPath, extractArgs);
                    if (!extractSuccess)
                    {
                        LogService.Instance.Warning("Failed to extract DirectX archive", "DependencyService");
                        CleanupTempDir(tempDir);
                        return false;
                    }

                    // Step B: Run the actual DXSETUP.exe from extracted folder
                    exeToRun = Path.Combine(tempDir, "DXSETUP.exe");
                    if (!File.Exists(exeToRun))
                    {
                        LogService.Instance.Warning($"DXSETUP.exe not found in {tempDir}", "DependencyService");
                        CleanupTempDir(tempDir);
                        return false;
                    }
                    args = "/silent";
                    workingDir = tempDir;
                    break;

                // Default fallback for .NET, PhysX, etc.
                default:
                    args = "/quiet /norestart";
                    break;
            }

            // Run the installer process
            var success = await RunProcessAsync(exeToRun, args, workingDir);

            if (success)
            {
                LogService.Instance.Info($"Successfully installed {DependencyDefinitions[type].Name}", "DependencyService");
            }
            else
            {
                LogService.Instance.Warning($"Installer for {type} failed", "DependencyService");
            }

            return success;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to install {type}: {ex.Message}", ex, "DependencyService");
            return false;
        }
        finally
        {
            // Guaranteed cleanup of temp directory even on exceptions or cancellation
            if (tempDir != null)
            {
                CleanupTempDir(tempDir);
            }
        }
    }

    /// <summary>
    /// Runs an installer process and waits for completion.
    /// </summary>
    private async Task<bool> RunProcessAsync(string fileName, string args, string? workingDir = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? Path.GetDirectoryName(fileName) ?? ""
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                LogService.Instance.Error($"Failed to start process: {fileName}", null, "DependencyService");
                return false;
            }

            await process.WaitForExitAsync();

            // 0 = Success, 3010 = Success (Reboot Required)
            return process.ExitCode == 0 || process.ExitCode == 3010;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Process execution failed: {ex.Message}", ex, "DependencyService");
            return false;
        }
    }

    /// <summary>
    /// Safely cleans up a temporary directory.
    /// </summary>
    private static void CleanupTempDir(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Failed to cleanup temp dir {tempDir}: {ex.Message}", "DependencyService");
        }
    }

    /// <summary>
    /// Repairs all missing dependencies detected for a game.
    /// </summary>
    public async Task<(int Installed, int Failed)> RepairAllMissingAsync(
        string gamePath, 
        IProgress<(string Status, double Progress)>? progress = null,
        CancellationToken ct = default)
    {
        var dependencies = await DetectRequiredDependenciesAsync(gamePath);
        var missing = dependencies.Where(d => !d.IsInstalled).ToList();

        if (missing.Count == 0)
        {
            LogService.Instance.Info("No missing dependencies detected", "DependencyService");
            return (0, 0);
        }

        int installed = 0;
        int failed = 0;

        for (int i = 0; i < missing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var dep = missing[i];
            var statusProgress = new Progress<string>(s => 
                progress?.Report((s, (double)i / missing.Count)));

            progress?.Report(($"Installing {dep.Name} ({i + 1}/{missing.Count})...", (double)i / missing.Count));

            var success = await InstallDependencyAsync(dep.Type, statusProgress);
            if (success)
            {
                installed++;
            }
            else
            {
                failed++;
            }
        }

        progress?.Report(("Complete", 1.0));
        return (installed, failed);
    }

    /// <summary>
    /// Gets all common dependencies and their installation status.
    /// </summary>
    public List<DependencyInfo> GetAllDependencies()
    {
        return DependencyDefinitions.Keys
            .Select(type => CreateDependencyInfo(type, IsDependencyInstalled(type)))
            .ToList();
    }

    private DependencyInfo CreateDependencyInfo(DependencyType type, bool? isInstalled = null)
    {
        var definition = DependencyDefinitions.TryGetValue(type, out var def) ? def : null;
        
        return new DependencyInfo
        {
            Type = type,
            Name = definition?.Name ?? type.ToString(),
            InstallerUrl = definition?.Url ?? string.Empty,
            IsInstalled = isInstalled ?? false,
            IsRequired = true
        };
    }

    private class DependencyDefinition
    {
        public string Name { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public string RegistryPath { get; init; } = string.Empty;
        public Version MinVersion { get; init; } = new();
    }
}
