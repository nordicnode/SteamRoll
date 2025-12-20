using System.Net.Http;

namespace SteamRoll.Services;

/// <summary>
/// Provides HTTP request utilities with retry logic.
/// </summary>
public static class HttpRetryHelper
{
    /// <summary>
    /// Default number of retry attempts for failed requests.
    /// </summary>
    public const int DefaultMaxRetries = 3;
    
    /// <summary>
    /// Default delay between retries in milliseconds.
    /// </summary>
    public const int DefaultRetryDelayMs = 1000;
    
    /// <summary>
    /// Executes an HTTP request with exponential backoff retry logic.
    /// </summary>
    /// <typeparam name="T">The type of result to return.</typeparam>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="operationName">Name of the operation for logging.</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="initialDelayMs">Initial delay between retries in milliseconds.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        int maxRetries = DefaultMaxRetries,
        int initialDelayMs = DefaultRetryDelayMs,
        CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;
        
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await operation();
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // This is a timeout (HttpClient uses TaskCanceledException for timeouts), not a cancellation - retry
                lastException = ex;
                
                if (attempt < maxRetries)
                {
                    var delay = initialDelayMs * (int)Math.Pow(2, attempt);
                    LogService.Instance.Debug(
                        $"{operationName} timed out (attempt {attempt + 1}/{maxRetries + 1}). Retrying in {delay}ms...", "HttpRetryHelper");
                    
                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // Don't retry on actual cancellation
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                
                if (attempt < maxRetries)
                {
                    // Exponential backoff: 1s, 2s, 4s...
                    var delay = initialDelayMs * (int)Math.Pow(2, attempt);
                    LogService.Instance.Debug(
                        $"{operationName} failed (attempt {attempt + 1}/{maxRetries + 1}): {ex.Message}. Retrying in {delay}ms...", "HttpRetryHelper");
                    
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        
        LogService.Instance.Error($"{operationName} failed after {maxRetries + 1} attempts.", category: "HttpRetryHelper");
        throw lastException ?? new HttpRequestException($"{operationName} failed after retries");
    }

    
    /// <summary>
    /// Executes an HTTP request with retry logic, returning a default value on failure.
    /// </summary>
    public static async Task<T?> ExecuteWithRetryOrDefaultAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        T? defaultValue = default,
        int maxRetries = DefaultMaxRetries,
        int initialDelayMs = DefaultRetryDelayMs,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ExecuteWithRetryAsync(operation, operationName, maxRetries, initialDelayMs, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"{operationName} failed with default fallback: {ex.Message}", "HttpRetryHelper");
            return defaultValue;
        }
    }

    
    /// <summary>
    /// Executes a void HTTP operation with retry logic.
    /// </summary>
    public static async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        string operationName,
        int maxRetries = DefaultMaxRetries,
        int initialDelayMs = DefaultRetryDelayMs,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(
            async () => { await operation(); return true; },
            operationName,
            maxRetries,
            initialDelayMs,
            cancellationToken);
    }
}
