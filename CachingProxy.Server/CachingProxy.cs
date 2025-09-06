using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace CachingProxy.Server;

public record ProxyResponse(
    string? ContentType,
    long? ContentLength,
    string? ETag,
    DateTimeOffset? LastModified,
    string? CacheControl,
    string? ContentDisposition,
    Dictionary<string, string> AdditionalHeaders
);

public class CachingProxy : IAsyncDisposable
{
    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<CachingProxy> _logger;
    private readonly ConcurrentDictionary<string, Task<ProxyResponse>> _inProgressRequests;
    private readonly SemaphoreSlim _cacheSemaphore;
    private volatile bool _disposed;

    public CachingProxy(string cacheDirectory, HttpClient? httpClient = null, ILogger<CachingProxy>? logger = null, int maxConcurrentDownloads = 10)
    {
        _cacheDirectory = cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory));
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<CachingProxy>.Instance;
        _inProgressRequests = new ConcurrentDictionary<string, Task<ProxyResponse>>();
        _cacheSemaphore = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);

        if (!Directory.Exists(_cacheDirectory))
            Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<(bool Success, ProxyResponse Response, string? ErrorMessage)> ValidateAndPrepareAsync(string url,
        CancellationToken cancellationToken = default)
    {
        var cacheFilePath = GetCacheFilePath(url);

        // If a cached file exists, return success with cached headers
        if (File.Exists(cacheFilePath))
        {
            _logger.LogInformation("Cache HIT for URL: {Url} (file: {CacheFile})", url,
                Path.GetFileName(cacheFilePath));

            var cachedHeaders = GetCachedHeaders(cacheFilePath);
            var fileInfo = new FileInfo(cacheFilePath);
            var response = new ProxyResponse(
                cachedHeaders.GetValueOrDefault("Content-Type"),
                fileInfo.Length,
                cachedHeaders.GetValueOrDefault("ETag"),
                cachedHeaders.ContainsKey("Last-Modified") &&
                DateTimeOffset.TryParse(cachedHeaders["Last-Modified"], out var lastMod)
                    ? lastMod
                    : null,
                cachedHeaders.GetValueOrDefault("Cache-Control"),
                cachedHeaders.GetValueOrDefault("Content-Disposition"),
                cachedHeaders
            );

            _logger.LogDebug("Cache hit: {ContentType}, {Size} bytes", response.ContentType, response.ContentLength);
            return (true, response, null);
        }

        // Check if there's a download in progress for this URL
        if (_inProgressRequests.ContainsKey(url))
        {
            _logger.LogDebug("Download in progress for URL: {Url} - will be served when complete", url);
            // Return success since we know the download is happening and will complete
            // The actual serving will wait for the download in ServeAsync
            return (true, new ProxyResponse(null, null, null, null, null, null, new Dictionary<string, string>()), null);
        }

        // Test origin connectivity without downloading
        _logger.LogInformation("Cache MISS for URL: {Url} - fetching from origin", url);

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await _httpClient.SendAsync(headRequest, cancellationToken);

            // For caching proxy, 304 Not Modified is also considered success
            if (!headResponse.IsSuccessStatusCode && headResponse.StatusCode != HttpStatusCode.NotModified)
                throw new HttpRequestException(
                    $"Response status code does not indicate success: {(int)headResponse.StatusCode} ({headResponse.ReasonPhrase}).");

            _logger.LogDebug("HEAD request successful for {Url}, Status: {Status}", url, headResponse.StatusCode);

            // Prepare response metadata from HEAD request
            var headers = new Dictionary<string, string>();
            var contentType = headResponse.Content.Headers.ContentType?.ToString();
            var contentLength = headResponse.Content.Headers.ContentLength;

            if (contentType != null) headers["Content-Type"] = contentType;
            if (headResponse.Headers.ETag != null) headers["ETag"] = headResponse.Headers.ETag.ToString();
            if (headResponse.Content.Headers.LastModified.HasValue)
                headers["Last-Modified"] = headResponse.Content.Headers.LastModified.Value.ToString("R");
            if (headResponse.Headers.CacheControl != null)
                headers["Cache-Control"] = headResponse.Headers.CacheControl.ToString();
            if (headResponse.Content.Headers.ContentDisposition != null)
                headers["Content-Disposition"] = headResponse.Content.Headers.ContentDisposition.ToString();

            // Add other important headers
            foreach (var header in headResponse.Headers.Concat(headResponse.Content.Headers))
                if (IsImportantHeader(header.Key) && !headers.ContainsKey(header.Key))
                    headers[header.Key] = string.Join(", ", header.Value);

            var response = new ProxyResponse(
                contentType,
                contentLength,
                headers.GetValueOrDefault("ETag"),
                headers.ContainsKey("Last-Modified") &&
                DateTimeOffset.TryParse(headers["Last-Modified"], out var lastMod2)
                    ? lastMod2
                    : null,
                headers.GetValueOrDefault("Cache-Control"),
                headers.GetValueOrDefault("Content-Disposition"),
                headers
            );

            return (true, response, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed for URL: {Url}", url);
            return (false, new ProxyResponse(null, null, null, null, null, null, new Dictionary<string, string>()),
                ex.Message);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Request timeout for URL: {Url}", url);
            return (false, new ProxyResponse(null, null, null, null, null, null, new Dictionary<string, string>()),
                "Request timeout");
        }
    }

    public async Task<ProxyResponse> ServeAsync(string url, Stream responseStream,
        CancellationToken cancellationToken = default)
    {
        var cacheFilePath = GetCacheFilePath(url);

        // If cached file exists, serve it directly
        if (File.Exists(cacheFilePath))
        {
            await using var cachedFileStream = File.OpenRead(cacheFilePath);
            await cachedFileStream.CopyToAsync(responseStream, cancellationToken);

            // Try to get cached headers from metadata file
            var cachedHeaders = GetCachedHeaders(cacheFilePath);
            return new ProxyResponse(
                cachedHeaders.GetValueOrDefault("Content-Type"),
                cachedFileStream.Length,
                cachedHeaders.GetValueOrDefault("ETag"),
                cachedHeaders.ContainsKey("Last-Modified") &&
                DateTimeOffset.TryParse(cachedHeaders["Last-Modified"], out var lastMod)
                    ? lastMod
                    : null,
                cachedHeaders.GetValueOrDefault("Cache-Control"),
                cachedHeaders.GetValueOrDefault("Content-Disposition"),
                cachedHeaders
            );
        }

        // Check if there's already a request in progress for this URL
        if (_inProgressRequests.TryGetValue(url, out var existingTask))
        {
            _logger.LogDebug("Request coalescing: Waiting for in-progress download of URL: {Url}", url);
            var existingResponse = await existingTask;
            
            // Now serve from the cache that was just created by the first request
            if (File.Exists(cacheFilePath))
            {
                await using var cachedFileStream = File.OpenRead(cacheFilePath);
                await cachedFileStream.CopyToAsync(responseStream, cancellationToken);
                return existingResponse;
            }
            
            // If cache file doesn't exist, something went wrong with the first request
            _logger.LogWarning("Cache file missing after coalesced request completed for URL: {Url}", url);
        }

        // File doesn't exist and no request in progress - start a new download
        var downloadTask = DownloadAndCacheAsync(url, cacheFilePath, cancellationToken);
        
        // Add to in-progress requests (or get existing if someone beat us to it)
        var actualTask = _inProgressRequests.GetOrAdd(url, downloadTask);
        
        if (actualTask != downloadTask)
        {
            // Someone else started the download, wait for theirs
            _logger.LogDebug("Request coalescing: Another download started while we were preparing for URL: {Url}", url);
            var otherResponse = await actualTask;
            
            // Serve from cache
            if (File.Exists(cacheFilePath))
            {
                await using var cachedFileStream = File.OpenRead(cacheFilePath);
                await cachedFileStream.CopyToAsync(responseStream, cancellationToken);
                return otherResponse;
            }
        }
        
        // We're the first request - do the actual download and tee to response stream
        try
        {
            var response = await actualTask;
            
            // Serve the content that was just downloaded
            if (File.Exists(cacheFilePath))
            {
                await using var cachedFileStream = File.OpenRead(cacheFilePath);
                await cachedFileStream.CopyToAsync(responseStream, cancellationToken);
            }
            
            return response;
        }
        finally
        {
            // Clean up the in-progress request
            _inProgressRequests.TryRemove(url, out _);
        }
    }

    private async Task<ProxyResponse> DownloadAndCacheAsync(string url, string cacheFilePath, CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CachingProxy));

        // Acquire semaphore to limit concurrent downloads
        await _cacheSemaphore.WaitAsync(cancellationToken);
        
        // Use temporary file to avoid race conditions
        var tempFilePath = cacheFilePath + ".tmp";
        
        try
        {
        
            using var originResponse =
                await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            originResponse.EnsureSuccessStatusCode();

            // Collect relevant headers
            var headers = new Dictionary<string, string>();

            var contentType = originResponse.Content.Headers.ContentType?.ToString();
            var contentLength = originResponse.Content.Headers.ContentLength;

            if (contentType != null) headers["Content-Type"] = contentType;
            if (originResponse.Headers.ETag != null) headers["ETag"] = originResponse.Headers.ETag.ToString();
            if (originResponse.Content.Headers.LastModified.HasValue)
                headers["Last-Modified"] = originResponse.Content.Headers.LastModified.Value.ToString("R");
            if (originResponse.Headers.CacheControl != null)
                headers["Cache-Control"] = originResponse.Headers.CacheControl.ToString();
            if (originResponse.Content.Headers.ContentDisposition != null)
                headers["Content-Disposition"] = originResponse.Content.Headers.ContentDisposition.ToString();

            // Add other important headers
            foreach (var header in originResponse.Headers.Concat(originResponse.Content.Headers))
                if (IsImportantHeader(header.Key) && !headers.ContainsKey(header.Key))
                    headers[header.Key] = string.Join(", ", header.Value);

            await using var originStream = await originResponse.Content.ReadAsStreamAsync(cancellationToken);
            await using var tempFileStream = File.Create(tempFilePath);

            // Download to temporary file
            await originStream.CopyToAsync(tempFileStream, cancellationToken);
            await tempFileStream.FlushAsync(cancellationToken);
            
            // Ensure temporary file is closed before moving
            await tempFileStream.DisposeAsync();

            // Atomic move to final cache file location
            File.Move(tempFilePath, cacheFilePath);
            
            // Save headers metadata
            SaveHeadersMetadata(cacheFilePath, headers);

            var fileSize = new FileInfo(cacheFilePath).Length;
            _logger.LogInformation("Cached content for URL: {Url} ({Size} bytes, {ContentType})",
                url, fileSize, contentType ?? "unknown");

            return new ProxyResponse(
                contentType,
                contentLength,
                headers.GetValueOrDefault("ETag"),
                headers.ContainsKey("Last-Modified") &&
                DateTimeOffset.TryParse(headers["Last-Modified"], out var lastMod)
                    ? lastMod
                    : null,
                headers.GetValueOrDefault("Cache-Control"),
                headers.GetValueOrDefault("Content-Disposition"),
                headers
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cache content for URL: {Url}", url);

            // If something goes wrong, clean up the partial files
            try
            {
                if (File.Exists(tempFilePath))
                    File.Delete(tempFilePath);
                if (File.Exists(cacheFilePath))
                    File.Delete(cacheFilePath);
                CleanupMetadataFile(cacheFilePath);

                _logger.LogDebug("Cleaned up partial cache files for URL: {Url}", url);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to cleanup partial cache files for URL: {Url}", url);
            }

            throw;
        }
        finally
        {
            _cacheSemaphore.Release();
        }
    }

    private string GetCacheFilePath(string url)
    {
        // Simple cache key generation - you might want something more sophisticated
        var uri = new Uri(url);
        var fileName = $"{uri.Host}_{uri.PathAndQuery}".Replace('/', '_').Replace('?', '_').Replace(':', '_');

        // Limit filename length and add hash for uniqueness
        if (fileName.Length > 100)
        {
            var hash = url.GetHashCode().ToString("X");
            fileName = fileName.Substring(0, 90) + "_" + hash;
        }

        return Path.Combine(_cacheDirectory, fileName);
    }

    private string GetMetadataFilePath(string cacheFilePath)
    {
        return cacheFilePath + ".meta";
    }

    private void SaveHeadersMetadata(string cacheFilePath, Dictionary<string, string> headers)
    {
        if (headers.Count > 0)
        {
            var json = JsonSerializer.Serialize(headers);
            File.WriteAllText(GetMetadataFilePath(cacheFilePath), json);
        }
    }

    private Dictionary<string, string> GetCachedHeaders(string cacheFilePath)
    {
        var metadataPath = GetMetadataFilePath(cacheFilePath);
        if (!File.Exists(metadataPath)) return new Dictionary<string, string>();

        try
        {
            var json = File.ReadAllText(metadataPath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch
        {
            // Fall back to old format (just content type)
            var contentType = File.ReadAllText(metadataPath);
            return new Dictionary<string, string> { ["Content-Type"] = contentType };
        }
    }

    private static bool IsImportantHeader(string headerName)
    {
        return headerName.Equals("Accept-Ranges", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Content-Language", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Expires", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Vary", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("X-Content-Type-Options", StringComparison.OrdinalIgnoreCase);
    }

    private void CleanupMetadataFile(string cacheFilePath)
    {
        try
        {
            var metadataPath = GetMetadataFilePath(cacheFilePath);
            if (File.Exists(metadataPath))
                File.Delete(metadataPath);
        }
        catch
        {
            /* Best effort cleanup */
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _logger.LogInformation("Disposing CachingProxy - waiting for {Count} in-progress downloads to complete", 
            _inProgressRequests.Count);

        // Wait for all in-progress downloads to complete (with a timeout)
        var inProgressTasks = _inProgressRequests.Values.ToArray();
        if (inProgressTasks.Length > 0)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await Task.WhenAll(inProgressTasks).WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timed out waiting for in-progress downloads to complete during disposal");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error waiting for in-progress downloads during disposal");
            }
        }

        _cacheSemaphore.Dispose();
        _inProgressRequests.Clear();

        _logger.LogInformation("CachingProxy disposed");
    }
}