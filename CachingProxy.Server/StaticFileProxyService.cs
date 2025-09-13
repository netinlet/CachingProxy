using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace CachingProxy.Server;

public class StaticFileProxyService : IAsyncDisposable
{
    private readonly StaticFileProxyOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<StaticFileProxyService> _logger;
    private readonly ConcurrentDictionary<string, Task<bool>> _inProgressRequests;
    private readonly SemaphoreSlim _downloadSemaphore;
    private volatile bool _disposed;

    public StaticFileProxyService(StaticFileProxyOptions options, HttpClient httpClient, 
        ILogger<StaticFileProxyService>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger<StaticFileProxyService>.Instance;
        _inProgressRequests = new ConcurrentDictionary<string, Task<bool>>();
        _downloadSemaphore = new SemaphoreSlim(_options.MaxConcurrentDownloads, _options.MaxConcurrentDownloads);

        if (!Directory.Exists(_options.StaticCacheDirectory))
            Directory.CreateDirectory(_options.StaticCacheDirectory);
    }

    public virtual async Task<bool> DownloadAndCacheAsync(string requestPath, CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;

        var sanitizedPath = SanitizePath(requestPath);
        if (sanitizedPath == null)
        {
            _logger.LogWarning("Invalid or unsafe path: {Path}", requestPath);
            return false;
        }

        var cacheFilePath = GetCacheFilePath(sanitizedPath);
        
        // If file already exists, no need to download
        if (File.Exists(cacheFilePath))
        {
            _logger.LogDebug("File already exists in cache: {Path}", sanitizedPath);
            return true;
        }

        // Check if download is already in progress
        if (_inProgressRequests.TryGetValue(sanitizedPath, out var existingTask))
        {
            _logger.LogDebug("Download already in progress for: {Path}", sanitizedPath);
            return await existingTask;
        }

        // Start new download
        var downloadTask = PerformDownloadAsync(sanitizedPath, cacheFilePath, cancellationToken);
        var actualTask = _inProgressRequests.GetOrAdd(sanitizedPath, downloadTask);

        if (actualTask != downloadTask)
        {
            // Another download started, wait for it
            return await actualTask;
        }

        try
        {
            return await downloadTask;
        }
        finally
        {
            _inProgressRequests.TryRemove(sanitizedPath, out _);
        }
    }

    private async Task<bool> PerformDownloadAsync(string sanitizedPath, string cacheFilePath, 
        CancellationToken cancellationToken)
    {
        await _downloadSemaphore.WaitAsync(cancellationToken);
        
        try
        {
            // Use the sanitized path for origin URL construction
            var originUrl = _options.BaseUrl.TrimEnd('/') + sanitizedPath;
            _logger.LogInformation("Downloading from origin: {Url} -> {CacheFile}", originUrl, cacheFilePath);

            var tempFilePath = cacheFilePath + ".tmp";
            
            // Ensure cache directory exists
            var cacheDir = Path.GetDirectoryName(cacheFilePath);
            if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
                Directory.CreateDirectory(cacheDir);

            using var response = await _httpClient.GetAsync(originUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            // Save headers metadata
            var headers = new Dictionary<string, string>();
            var contentType = response.Content.Headers.ContentType?.ToString();
            if (contentType != null) headers["Content-Type"] = contentType;
            if (response.Headers.ETag != null) headers["ETag"] = response.Headers.ETag.ToString();
            if (response.Content.Headers.LastModified.HasValue)
                headers["Last-Modified"] = response.Content.Headers.LastModified.Value.ToString("R");
            if (response.Headers.CacheControl != null)
                headers["Cache-Control"] = response.Headers.CacheControl.ToString();

            // Download to temporary file
            await using var originStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var tempFileStream = File.Create(tempFilePath);
            await originStream.CopyToAsync(tempFileStream, cancellationToken);
            await tempFileStream.FlushAsync(cancellationToken);
            await tempFileStream.DisposeAsync();

            // Atomic move to final location
            File.Move(tempFilePath, cacheFilePath);

            // Save metadata
            if (headers.Count > 0)
            {
                var metadataPath = cacheFilePath + ".meta";
                var json = JsonSerializer.Serialize(headers);
                await File.WriteAllTextAsync(metadataPath, json, cancellationToken);
            }

            var fileInfo = new FileInfo(cacheFilePath);
            _logger.LogInformation("Cached file: {Path} ({Size} bytes, {ContentType})", 
                sanitizedPath, fileInfo.Length, contentType ?? "unknown");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download and cache: {Path}", sanitizedPath);
            
            // Cleanup on failure
            var tempFilePath = cacheFilePath + ".tmp";
            try
            {
                if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
                if (File.Exists(cacheFilePath)) File.Delete(cacheFilePath);
                if (File.Exists(cacheFilePath + ".meta")) File.Delete(cacheFilePath + ".meta");
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to cleanup partial files for: {Path}", sanitizedPath);
            }

            return false;
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private string? SanitizePath(string requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath)) return null;

        // Remove leading slash and normalize
        var path = requestPath.TrimStart('/').Replace('\\', '/');
        
        // Check for directory traversal attempts
        if (path.Contains("..") || path.Contains("//")) return null;
        
        // Check file extension if specified
        if (_options.AllowedExtensions.Length > 0)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            
            // Allow certain API endpoints even without traditional file extensions
            bool isSpecialApiEndpoint = path.StartsWith("image/") || 
                                      path.StartsWith("api/") ||
                                      path.Contains("/image/");
                                      
            if (!isSpecialApiEndpoint && !_options.AllowedExtensions.Contains(extension)) 
                return null;
        }

        return "/" + path;
    }

    private string GetCacheFilePath(string sanitizedPath)
    {
        var relativePath = sanitizedPath.TrimStart('/');
        return Path.Combine(_options.StaticCacheDirectory, relativePath);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogInformation("Disposing StaticFileProxyService - waiting for {Count} downloads to complete", 
            _inProgressRequests.Count);

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
                _logger.LogWarning("Timed out waiting for downloads to complete during disposal");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error waiting for downloads during disposal");
            }
        }

        _downloadSemaphore.Dispose();
        _inProgressRequests.Clear();
        _logger.LogInformation("StaticFileProxyService disposed");
    }
}