using System.IO;
using System.Text.Json;
using SteamRoll.Models;

namespace SteamRoll.Services.Packaging;

/// <summary>
/// Generates package metadata and README files.
/// </summary>
public class PackageMetadataGenerator
{
    /// <summary>
    /// Creates package metadata and README.
    /// </summary>
    public void CreatePackageMetadata(string packageDir, InstalledGame game, bool emulatorApplied,
        PackageMode mode = PackageMode.Goldberg, string? emulatorVersion = null, SteamGameDetails? storeDetails = null,
        FileHashMode hashMode = FileHashMode.CriticalOnly)
    {
        var modeName = mode == PackageMode.CreamApi ? "CreamAPI" : "Goldberg";
        var versionDisplay = !string.IsNullOrEmpty(emulatorVersion) ? emulatorVersion : "unknown";

        var emulatorStatus = emulatorApplied
            ? $"✓ Ready to play ({modeName} {versionDisplay})"
            : $"⚠ Manual setup required ({modeName} not applied)";

        // Build the README with richer game info
        var sb = new System.Text.StringBuilder();

        // Header
        sb.AppendLine("╔══════════════════════════════════════════════════════════════════════════════╗");
        sb.AppendLine($"║  {game.Name,-76} ║");
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════╣");
        sb.AppendLine($"║  Packaged by SteamRoll • {DateTime.Now:MMMM d, yyyy,-50} ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════════════════════╝");
        sb.AppendLine();

        // Game description from Steam
        if (storeDetails != null && !string.IsNullOrEmpty(storeDetails.Description))
        {
            sb.AppendLine("ABOUT THIS GAME");
            sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");
            // Word wrap the description to ~78 chars
            var desc = System.Net.WebUtility.HtmlDecode(storeDetails.Description);
            desc = System.Text.RegularExpressions.Regex.Replace(desc, "<[^>]+>", ""); // Strip HTML
            sb.AppendLine(WordWrap(desc, 78));
            sb.AppendLine();
        }

        // Game details section
        sb.AppendLine("GAME DETAILS");
        sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");
        sb.AppendLine($"  Steam AppID:    {game.AppId}");
        sb.AppendLine($"  Build:          {game.Version}");
        sb.AppendLine($"  Size:           {game.FormattedSize}");

        if (storeDetails != null)
        {
            if (!string.IsNullOrEmpty(storeDetails.ReleaseDate))
                sb.AppendLine($"  Released:       {storeDetails.ReleaseDate}");

            if (storeDetails.Developers.Count > 0)
                sb.AppendLine($"  Developer:      {storeDetails.DevelopersDisplay}");

            if (storeDetails.Publishers.Count > 0 && storeDetails.Publishers[0] != storeDetails.Developers.FirstOrDefault())
                sb.AppendLine($"  Publisher:      {storeDetails.PublishersDisplay}");

            if (storeDetails.Genres.Count > 0)
                sb.AppendLine($"  Genres:         {storeDetails.GenresDisplay}");
        }
        sb.AppendLine();

        // Reviews & Ratings section
        if (storeDetails != null && (storeDetails.ReviewPositivePercent.HasValue || storeDetails.MetacriticScore.HasValue))
        {
            sb.AppendLine("RATINGS");
            sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");

            if (storeDetails.ReviewPositivePercent.HasValue)
            {
                sb.AppendLine($"  Steam Reviews:  {storeDetails.ReviewDescription} ({storeDetails.ReviewPositivePercent}% positive)");
                if (storeDetails.ReviewTotalCount.HasValue)
                    sb.AppendLine($"                  Based on {storeDetails.ReviewTotalCount:N0} reviews");
            }

            if (storeDetails.MetacriticScore.HasValue)
                sb.AppendLine($"  Metacritic:     {storeDetails.MetacriticScore}/100");

            sb.AppendLine();
        }

        // Package status
        sb.AppendLine("PACKAGE STATUS");
        sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");
        sb.AppendLine($"  {emulatorStatus}");
        if (game.HasDlc && game.AvailableDlc.Count > 0)
            sb.AppendLine($"  {game.AvailableDlc.Count} DLC unlocked");
        sb.AppendLine();

        // How to play section
        if (emulatorApplied)
        {
            sb.AppendLine("HOW TO PLAY");
            sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine("  1. Copy this entire folder to the target PC");
            sb.AppendLine("  2. Run the game executable directly (see launcher.json for which exe)");
            sb.AppendLine("  3. No Steam required!");
            sb.AppendLine();
            sb.AppendLine("  For LAN multiplayer:");
            sb.AppendLine("  • Both PCs must be on the same network");
            sb.AppendLine("  • Run the game on both PCs");
            sb.AppendLine("  • Use in-game LAN/Local multiplayer options");
            sb.AppendLine();
            sb.AppendLine("  Linux users: Run ./launch.sh (requires Wine or Proton)");
            sb.AppendLine();
            sb.AppendLine("  Customization:");
            sb.AppendLine("  • Edit steam_settings/force_account_name.txt to change your player name");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("MANUAL SETUP REQUIRED");
            sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine("  Emulator DLLs were not applied automatically. To complete setup:");
            sb.AppendLine();
            sb.AppendLine("  1. Download Goldberg Emulator from:");
            sb.AppendLine("     https://gitlab.com/Mr_Goldberg/goldberg_emulator/-/releases");
            sb.AppendLine();
            sb.AppendLine("  2. Find steam_api.dll / steam_api64.dll in this folder");
            sb.AppendLine("  3. Replace them with the Goldberg versions");
            sb.AppendLine("  4. Run the game executable directly");
            sb.AppendLine();
        }

        // Footer
        sb.AppendLine("═══════════════════════════════════════════════════════════════════════════════");
        sb.AppendLine("  This package is for personal household LAN use only.");
        sb.AppendLine("  Created with SteamRoll • https://github.com/steamroll");
        sb.AppendLine("═══════════════════════════════════════════════════════════════════════════════");

        File.WriteAllText(Path.Combine(packageDir, "README.txt"), sb.ToString());

        // Create machine-readable metadata
        try
        {
            var metadata = new PackageMetadata
            {
                AppId = game.AppId,
                Name = game.Name,
                BuildId = game.BuildId,
                CreatedDate = DateTime.Now,
                CreatorVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
                EmulatorMode = mode.ToString(),
                EmulatorVersion = versionDisplay,
                OriginalSize = game.SizeOnDisk,
                FileHashes = GenerateFileHashes(packageDir, hashMode)
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(metadata, options);
            File.WriteAllText(Path.Combine(packageDir, "steamroll.json"), json);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Failed to create steamroll.json metadata: {ex.Message}", "PackageMetadataGenerator");
        }
    }

    /// <summary>
    /// Generates SHA256 hashes for key files in a package directory.
    /// </summary>
    private Dictionary<string, string> GenerateFileHashes(string packageDir, FileHashMode mode)
    {
        return PackageVerifier.GenerateFileHashes(packageDir, mode);
    }

    private static string WordWrap(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentLine = "";

        foreach (var word in words)
        {
            if (currentLine.Length + word.Length + 1 <= width)
            {
                currentLine += (currentLine.Length > 0 ? " " : "") + word;
            }
            else
            {
                if (currentLine.Length > 0)
                    lines.Add(currentLine);
                currentLine = word;
            }
        }
        if (currentLine.Length > 0)
            lines.Add(currentLine);

        return string.Join(Environment.NewLine, lines);
    }
}
