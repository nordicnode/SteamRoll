using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace SteamRoll.Services;

/// <summary>
/// Service for fetching game details from the Steam Store API.
/// Registered in ServiceContainer for dependency injection.
/// </summary>
public class SteamStoreService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<int, CachedStoreEntry> _cache = new();
    private bool _disposed;

    public SteamStoreService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SteamRoll/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(AppConstants.HTTP_TIMEOUT_SECONDS);
    }



    /// <summary>
    /// Fetches game details from Steam Store API with caching.
    /// </summary>
    public async Task<SteamGameDetails?> GetGameDetailsAsync(int appId, CancellationToken ct = default)
    {
        // Check cache first - validate TTL
        if (_cache.TryGetValue(appId, out var cached))
        {
            if ((DateTime.UtcNow - cached.CachedAt).TotalDays < AppConstants.CACHE_EXPIRY_DAYS)
            {
                return cached.Details;
            }
            // Expired - remove from cache (thread-safe)
            _cache.TryRemove(appId, out _);
        }


        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await HttpRetryHelper.ExecuteWithRetryAsync(
                () => _httpClient.GetStringAsync(url, ct),
                $"Steam Store API (AppId: {appId})",
                maxRetries: 2,
                cancellationToken: ct);

            
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty(appId.ToString(), out var appData))
                return null;
                
            if (!appData.TryGetProperty("success", out var success) || !success.GetBoolean())
                return null;
                
            if (!appData.TryGetProperty("data", out var data))
                return null;

            var details = new SteamGameDetails
            {
                AppId = appId,
                Name = GetStringProperty(data, "name"),
                Description = GetStringProperty(data, "short_description"),
                DetailedDescription = GetStringProperty(data, "detailed_description"),
                HeaderImage = GetStringProperty(data, "header_image"),
                BackgroundImage = GetStringProperty(data, "background"),
                Website = GetStringProperty(data, "website"),
                Developers = GetStringArray(data, "developers"),
                Publishers = GetStringArray(data, "publishers"),
                IsFree = data.TryGetProperty("is_free", out var isFree) && isFree.GetBoolean()
            };

            // Get genres
            if (data.TryGetProperty("genres", out var genres))
            {
                foreach (var genre in genres.EnumerateArray())
                {
                    if (genre.TryGetProperty("description", out var desc))
                        details.Genres.Add(desc.GetString() ?? "");
                }
            }

            // Get categories (features)
            if (data.TryGetProperty("categories", out var categories))
            {
                foreach (var cat in categories.EnumerateArray())
                {
                    if (cat.TryGetProperty("description", out var desc))
                        details.Features.Add(desc.GetString() ?? "");
                }
            }

            // Get screenshots
            if (data.TryGetProperty("screenshots", out var screenshots))
            {
                foreach (var ss in screenshots.EnumerateArray())
                {
                    if (ss.TryGetProperty("path_full", out var path))
                        details.Screenshots.Add(path.GetString() ?? "");
                }
            }

            // Get movies/trailers
            if (data.TryGetProperty("movies", out var movies))
            {
                foreach (var movie in movies.EnumerateArray())
                {
                    var trailer = new SteamTrailer
                    {
                        Name = GetStringProperty(movie, "name"),
                        Thumbnail = GetStringProperty(movie, "thumbnail")
                    };
                    
                    if (movie.TryGetProperty("webm", out var webm))
                    {
                        if (webm.TryGetProperty("480", out var url480))
                            trailer.VideoUrl = url480.GetString() ?? "";
                        else if (webm.TryGetProperty("max", out var urlMax))
                            trailer.VideoUrl = urlMax.GetString() ?? "";
                    }
                    
                    details.Trailers.Add(trailer);
                }
            }

            // Get release date
            if (data.TryGetProperty("release_date", out var releaseDate))
            {
                details.ReleaseDate = GetStringProperty(releaseDate, "date");
            }

            // Get metacritic
            if (data.TryGetProperty("metacritic", out var metacritic))
            {
                if (metacritic.TryGetProperty("score", out var score))
                    details.MetacriticScore = score.GetInt32();
            }

            // Evict oldest entries if cache is full (thread-safe eviction)
            if (_cache.Count >= AppConstants.MAX_STORE_CACHE_ENTRIES)
            {
                var oldest = _cache.OrderBy(kvp => kvp.Value.CachedAt).FirstOrDefault();
                if (oldest.Key != 0)
                {
                    _cache.TryRemove(oldest.Key, out _);
                }
            }
            
            // Fetch user review scores (best effort, don't fail if this doesn't work)
            try
            {
                await FetchReviewScoreAsync(details);
            }
            catch
            {
                // Ignore review fetch errors
            }
            
            // Cache the result with timestamp
            _cache[appId] = new CachedStoreEntry
            {
                Details = details,
                CachedAt = DateTime.UtcNow
            };
            return details;

        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Steam API error for {appId}: {ex.Message}", "SteamStoreService");
            return null;
        }
    }
    
    /// <summary>
    /// Fetches user review scores from Steam's appreviews API.
    /// </summary>
    private async Task FetchReviewScoreAsync(SteamGameDetails details)
    {
        var url = $"https://store.steampowered.com/appreviews/{details.AppId}?json=1&language=all&purchase_type=all&num_per_page=0";
        var response = await _httpClient.GetStringAsync(url);
        
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        
        if (root.TryGetProperty("query_summary", out var summary))
        {
            if (summary.TryGetProperty("total_positive", out var positive) &&
                summary.TryGetProperty("total_reviews", out var total))
            {
                var positiveCount = positive.GetInt32();
                var totalCount = total.GetInt32();
                
                if (totalCount > 0)
                {
                    details.ReviewTotalCount = totalCount;
                    details.ReviewPositivePercent = (int)Math.Round(100.0 * positiveCount / totalCount);
                    
                    // Generate review description
                    details.ReviewDescription = details.ReviewPositivePercent switch
                    {
                        >= 95 when details.ReviewTotalCount > 500 => "Overwhelmingly Positive",
                        >= 85 when details.ReviewTotalCount > 50 => "Very Positive",
                        >= 80 => "Positive",
                        >= 70 => "Mostly Positive",
                        >= 40 => "Mixed",
                        >= 20 => "Mostly Negative",
                        _ => "Very Negative"
                    };
                }
            }
        }
    }

    private string GetStringProperty(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
    }

    private List<string> GetStringArray(JsonElement element, string property)
    {
        var list = new List<string>();
        if (element.TryGetProperty(property, out var array))
        {
            foreach (var item in array.EnumerateArray())
            {
                var str = item.GetString();
                if (!string.IsNullOrEmpty(str))
                    list.Add(str);
            }
        }
        return list;
    }

    /// <summary>
    /// Disposes of the HttpClient and clears the cache.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _cache.Clear();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}


/// <summary>
/// Detailed game information from Steam Store.
/// </summary>
public class SteamGameDetails
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string DetailedDescription { get; set; } = "";
    public string HeaderImage { get; set; } = "";
    public string BackgroundImage { get; set; } = "";
    public string Website { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public int? MetacriticScore { get; set; }
    public bool IsFree { get; set; }
    public List<string> Developers { get; set; } = new();
    public List<string> Publishers { get; set; } = new();
    public List<string> Genres { get; set; } = new();
    public List<string> Features { get; set; } = new();
    public List<string> Screenshots { get; set; } = new();
    public List<SteamTrailer> Trailers { get; set; } = new();
    
    // User review scores
    public int? ReviewPositivePercent { get; set; }
    public int? ReviewTotalCount { get; set; }
    public string? ReviewDescription { get; set; }
    
    public string DevelopersDisplay => string.Join(", ", Developers);
    public string PublishersDisplay => string.Join(", ", Publishers);
    public string GenresDisplay => string.Join(" • ", Genres);
    
    public string ReviewDisplay => ReviewPositivePercent.HasValue 
        ? $"{ReviewPositivePercent}% positive ({ReviewTotalCount?.ToString("N0") ?? "?"} reviews)"
        : "";
}

/// <summary>
/// Trailer/video information from Steam Store.
/// </summary>
public class SteamTrailer
{
    public string Name { get; set; } = "";
    public string Thumbnail { get; set; } = "";
    public string VideoUrl { get; set; } = "";
}

/// <summary>
/// Cache entry wrapper for Steam Store data with timestamp.
/// </summary>
internal class CachedStoreEntry
{
    public SteamGameDetails Details { get; set; } = new();
    public DateTime CachedAt { get; set; }
}

