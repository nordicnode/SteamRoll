using System.IO;

namespace SteamRoll.Services.Packaging;

/// <summary>
/// Detects game dependencies and creates installation scripts.
/// </summary>
public class PackageDependencyManager
{
    /// <summary>
    /// Detects common redistributables and creates a convenience installation script.
    /// </summary>
    public void DetectAndScriptDependencies(string packageDir)
    {
        try
        {
            var installers = new List<string>();
            var commonNames = new[] { "_CommonRedist", "CommonRedist", "Redist", "Dependencies", "Installers" };

            // Look for installer folders
            foreach (var name in commonNames)
            {
                var dir = Path.Combine(packageDir, name);
                if (Directory.Exists(dir))
                {
                    // Find executables recursively
                    var exes = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories)
                        .Where(f => !f.Contains("SteamInstall", StringComparison.OrdinalIgnoreCase)) // Skip Steam's internal helpers
                        .ToList();

                    foreach (var exe in exes)
                    {
                        var fileName = Path.GetFileName(exe).ToLowerInvariant();

                        // Filter for known redistributables
                        if (fileName.Contains("vcredist") ||
                            fileName.Contains("dxsetup") ||
                            fileName.Contains("dotNet") ||
                            fileName.Contains("physx") ||
                            fileName.Contains("openal"))
                        {
                            installers.Add(exe);
                        }
                    }
                }
            }

            if (installers.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("title Install Dependencies");
                sb.AppendLine("echo Installing required game dependencies...");
                sb.AppendLine("echo.");

                foreach (var installer in installers)
                {
                    var relPath = Path.GetRelativePath(packageDir, installer);
                    var fileName = Path.GetFileName(installer);

                    sb.AppendLine($"echo Installing {fileName}...");

                    // Add silent flags based on installer type
                    var args = "";
                    if (fileName.Contains("vcredist")) args = "/quiet /norestart";
                    else if (fileName.Contains("dxsetup")) args = "/silent";
                    else if (fileName.Contains("dotnet")) args = "/quiet /norestart";
                    else if (fileName.Contains("physx")) args = "/quiet";
                    else if (fileName.Contains("openal")) args = "/S"; // NSIS usually

                    sb.AppendLine($"start /wait \"\" \"{relPath}\" {args}");
                    sb.AppendLine("if %errorlevel% neq 0 echo Warning: Installation exited with code %errorlevel%");
                    sb.AppendLine("echo.");
                }

                sb.AppendLine("echo All dependencies installed.");
                sb.AppendLine("pause");

                File.WriteAllText(Path.Combine(packageDir, "install_deps.bat"), sb.ToString());
                LogService.Instance.Info($"Created dependency installer script for {installers.Count} items", "PackageDependencyManager");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to detect dependencies: {ex.Message}", "PackageDependencyManager");
        }
    }
}
