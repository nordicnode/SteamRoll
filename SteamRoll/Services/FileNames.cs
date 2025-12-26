namespace SteamRoll.Services;

/// <summary>
/// Centralized file and directory name constants.
/// Prevents typos and ensures consistency across the codebase.
/// </summary>
public static class FileNames
{
    // Package metadata files
    public const string STEAMROLL_JSON = "steamroll.json";
    public const string STEAM_APPID_TXT = "steam_appid.txt";
    public const string LAUNCH_BAT = "LAUNCH.bat";
    
    // Steam API files
    public const string STEAM_API_DLL = "steam_api.dll";
    public const string STEAM_API64_DLL = "steam_api64.dll";
    
    // Goldberg directories
    public const string STEAM_SETTINGS_DIR = "steam_settings";
    
    // Transfer markers
    public const string RECEIVED_MARKER = ".steamroll_received";
    public const string TRANSFER_HISTORY_JSON = "transfer_history.json";
    
    // Cache files
    public const string GAME_CACHE_JSON = "game_cache.json";
    public const string HASH_CACHE_DIR = "hash_cache";
}
