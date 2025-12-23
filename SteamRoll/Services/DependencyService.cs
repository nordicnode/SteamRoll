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
    /// </summary>
    public async Task<List<DependencyInfo>> DetectRequiredDependenciesAsync(string gamePath)
    {
        var dependencies = new List<DependencyInfo>();

        try
        {
            var dllFiles = Directory.GetFiles(gamePath, "*.dll", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Check for VC++ runtime DLLs
            var vcpp2015PlusPatterns = new[]
            {
                "vcruntime140.dll", "vcruntime140d.dll", 
                "vcruntime140_1.dll", "msvcp140.dll",
                "concrt140.dll", "vccorlib140.dll"
            };

            var vcpp2013Patterns = new[]
            {
                "msvcr120.dll", "msvcp120.dll", "vccorlib120.dll"
            };

            var vcpp2012Patterns = new[] { "msvcr110.dll", "msvcp110.dll" };
            var vcpp2010Patterns = new[] { "msvcr100.dll", "msvcp100.dll" };
            var vcpp2008Patterns = new[] { "msvcr90.dll", "msvcp90.dll" };
            
            // Check for physx
            var physxPatterns = new[] { "physxloader.dll", "physxcooking.dll", "physxcore.dll" };

            // Detect VC++ 2015-2022
            if (vcpp2015PlusPatterns.Any(p => dllFiles.Contains(p)))
            {
                dependencies.Add(CreateDependencyInfo(DependencyType.VcRedist2015to2022_x64));
                dependencies.Add(CreateDependencyInfo(DependencyType.VcRedist2015to2022_x86));
            }

            // Detect VC++ 2013
            if (vcpp2013Patterns.Any(p => dllFiles.Contains(p)))
            {
                dependencies.Add(CreateDependencyInfo(DependencyType.VcRedist2013_x64));
                dependencies.Add(CreateDependencyInfo(DependencyType.VcRedist2013_x86));
            }

            // Detect VC++ 2012
            if (vcpp2012Patterns.Any(p => dllFiles.Contains(p)))
            {
                dependencies.Add(CreateDependencyInfo(DependencyType.VcRedist2012_x64));
                dependencies.Add(CreateDependencyInfo(DependencyType.VcRedist2012_x86));
            }

            // Detect VC++ 2010
            if (vcpp2010Patterns.Any(p => dllFiles.Contains(p)))
            {
                dependencies.Add(CreateDependencyInfo(DependencyType.VcRedist2010_x64));
                dependencies.Add(CreateDependencyInfo(DependencyType.VcRedist2010_x86));
            }

            // Detect VC++ 2008
            if (vcpp2008Patterns.Any(p => dllFiles.Contains(p)))
            {
                dependencies.Add(CreateDependencyInfo(DependencyType.VcRedist2008_x64));
                dependencies.Add(CreateDependencyInfo(DependencyType.VcRedist2008_x86));
            }

            // Detect PhysX
            if (physxPatterns.Any(p => dllFiles.Contains(p)))
            {
                dependencies.Add(CreateDependencyInfo(DependencyType.PhysX));
            }

            // Check installation status for each dependency
            foreach (var dep in dependencies)
            {
                dep.IsInstalled = await Task.Run(() => IsDependencyInstalled(dep.Type));
            }

            LogService.Instance.Info($"Detected {dependencies.Count} dependencies for game at {gamePath}", "DependencyService");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to detect dependencies: {ex.Message}", ex, "DependencyService");
        }

        return dependencies;
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
    /// </summary>
    public async Task<bool> InstallDependencyAsync(DependencyType type, IProgress<string>? status = null)
    {
        var installerPath = await DownloadDependencyAsync(type);
        if (string.IsNullOrEmpty(installerPath))
        {
            return false;
        }

        try
        {
            status?.Report($"Installing {DependencyDefinitions[type].Name}...");
            
            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/install /quiet /norestart",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                LogService.Instance.Error($"Failed to start installer for {type}", null, "DependencyService");
                return false;
            }

            await process.WaitForExitAsync();

            var success = process.ExitCode == 0 || process.ExitCode == 3010; // 3010 = success, reboot required
            if (success)
            {
                LogService.Instance.Info($"Successfully installed {DependencyDefinitions[type].Name}", "DependencyService");
            }
            else
            {
                LogService.Instance.Warning($"Installer for {type} exited with code {process.ExitCode}", "DependencyService");
            }

            return success;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Failed to install {type}: {ex.Message}", ex, "DependencyService");
            return false;
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
