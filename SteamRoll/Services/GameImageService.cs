using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SteamRoll.Services;

/// <summary>
/// Service for resolving game header images from multiple sources with fallback chain.
/// Registered in ServiceContainer for dependency injection.
/// </summary>
public class GameImageService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SteamStoreService _storeService;
    private readonly ConcurrentDictionary<int, string> _imageUrlCache = new();
    private readonly ConcurrentDictionary<int, bool> _failedAppIds = new();
    
    /// <summary>
    /// Image source URLs to try in order.
    /// </summary>
    private static readonly string[] ImageUrlTemplates = new[]
    {
        // Steam CDN (primary) - the original source
        "https://steamcdn-a.akamaihd.net/steam/apps/{0}/header.jpg",
        
        // Steam Store CDN (alternate endpoint)
        "https://cdn.akamai.steamstatic.com/steam/apps/{0}/header.jpg",
        
        // Steam Community CDN
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/header.jpg",
        
        // Steam Library capsule (different aspect ratio but often works)
        "https://steamcdn-a.akamaihd.net/steam/apps/{0}/library_600x900.jpg",
    };

    public GameImageService(SteamStoreService storeService)
    {
        _storeService = storeService;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SteamRoll/1.0");
    }

    /// <summary>
    /// Gets a working header image URL for the specified app ID.
    /// Tries multiple sources in order and caches the result.
    /// </summary>
    /// <param name="appId">Steam App ID</param>
    /// <param name="localHeaderPath">Optional local header path to check first</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Working image URL, or null if all sources fail</returns>
    public async Task<string?> GetHeaderImageUrlAsync(int appId, string? localHeaderPath = null, CancellationToken ct = default)
    {
        // Check if already cached
        if (_imageUrlCache.TryGetValue(appId, out var cachedUrl))
        {
            return cachedUrl;
        }
        
        // Skip if we already know this app has no working images
        if (_failedAppIds.ContainsKey(appId))
        {
            return null;
        }
        
        // Check local path first
        if (!string.IsNullOrEmpty(localHeaderPath) && System.IO.File.Exists(localHeaderPath))
        {
            _imageUrlCache[appId] = localHeaderPath;
            return localHeaderPath;
        }
        
        // Try each source in order
        foreach (var template in ImageUrlTemplates)
        {
            if (ct.IsCancellationRequested) break;
            
            var url = string.Format(template, appId);
            
            try
            {
                if (await IsImageAccessibleAsync(url, ct))
                {
                    _imageUrlCache[appId] = url;
                    LogService.Instance.Debug($"Found working image for AppId {appId}: {url}", "GameImageService");
                    return url;
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Debug($"Failed to check image URL {url}: {ex.Message}", "GameImageService");
            }
        }
        
        // Final fallback: Try Steam Store API to get the actual header_image URL
        // Steam uses hash-based URLs for many games now, which we can only get from the API
        try
        {
            var details = await _storeService.GetGameDetailsAsync(appId, ct);
            if (details != null && !string.IsNullOrEmpty(details.HeaderImage))
            {
                // Verify the API URL is accessible
                if (await IsImageAccessibleAsync(details.HeaderImage, ct))
                {
                    _imageUrlCache[appId] = details.HeaderImage;
                    LogService.Instance.Debug($"Found working image from API for AppId {appId}: {details.HeaderImage}", "GameImageService");
                    return details.HeaderImage;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Debug($"Failed to get image from Steam API for AppId {appId}: {ex.Message}", "GameImageService");
        }
        
        // All sources failed - mark as failed to avoid repeated checks
        _failedAppIds[appId] = true;
        LogService.Instance.Warning($"No working image found for AppId {appId}", "GameImageService");
        
        return null;
    }

    /// <summary>
    /// Checks if an image URL is accessible using a HEAD request.
    /// </summary>
    private async Task<bool> IsImageAccessibleAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            
            // Check for success and valid content type
            if (response.IsSuccessStatusCode)
            {
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Clears the image URL cache for a specific app or all apps.
    /// </summary>
    /// <param name="appId">Specific app ID to clear, or null to clear all</param>
    public void ClearCache(int? appId = null)
    {
        if (appId.HasValue)
        {
            _imageUrlCache.TryRemove(appId.Value, out _);
            _failedAppIds.TryRemove(appId.Value, out _);
        }
        else
        {
            _imageUrlCache.Clear();
            _failedAppIds.Clear();
        }
    }

    /// <summary>
    /// Gets count of cached image URLs.
    /// </summary>
    public int CachedCount => _imageUrlCache.Count;

    /// <summary>
    /// Gets count of apps with no working images.
    /// </summary>
    public int FailedCount => _failedAppIds.Count;

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
