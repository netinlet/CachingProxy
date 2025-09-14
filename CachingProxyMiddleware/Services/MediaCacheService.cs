using System.Collections.Concurrent;
using System.Text.Json;
using CachingProxyMiddleware.Extensions;
using CachingProxyMiddleware.Interfaces;
using CachingProxyMiddleware.Models;
using CachingProxyMiddleware.Validators;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Options;

namespace CachingProxyMiddleware.Services;

public class MediaCacheService : IMediaCacheService, IAsyncDisposable
{
    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, Task<Result<CachedMedia>>> _inProgressDownloads;
    private readonly ILogger<MediaCacheService> _logger;
    private readonly MediaCacheOptions _options;
    private readonly IHostBasedPathProvider _pathProvider;
    private readonly IUrlResolver _urlResolver;
    private volatile bool _disposed;

    public MediaCacheService(
        IOptions<MediaCacheOptions> options,
        HttpClient httpClient,
        IHostBasedPathProvider pathProvider,
        IUrlResolver urlResolver,
        ILogger<MediaCacheService> logger)
    {
        _options = options.Value;
        _httpClient = httpClient;
        _pathProvider = pathProvider;
        _urlResolver = urlResolver;
        _logger = logger;
        _downloadSemaphore = new SemaphoreSlim(_options.MaxConcurrentDownloads, _options.MaxConcurrentDownloads);
        _inProgressDownloads = new ConcurrentDictionary<string, Task<Result<CachedMedia>>>();

        EnsureCacheDirectoryExists();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _logger.LogInformation("Disposing MediaCacheService - waiting for {InProgressCount} in-progress downloads",
            _inProgressDownloads.Count);

        // Wait for in-progress downloads with timeout
        var inProgressTasks = _inProgressDownloads.Values.ToArray();
        if (inProgressTasks.Length > 0)
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await Task.WhenAll(inProgressTasks).WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timed out waiting for in-progress downloads during disposal");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error waiting for in-progress downloads during disposal");
            }

        _downloadSemaphore.Dispose();
        _inProgressDownloads.Clear();
        _logger.LogInformation("MediaCacheService disposed successfully");
    }

    public async Task<Result<CachedMedia>> GetOrCacheAsync(Uri url, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return Result.Failure<CachedMedia>("Service has been disposed");

        return await ValidateMediaUrl(url)
            .Bind(validUrl => _urlResolver.ResolveAsync(validUrl, cancellationToken))
            .Bind(async resolvedUrl => await GetOrDownloadMedia(resolvedUrl, cancellationToken));
    }

    public async Task<Result> ClearCacheAsync()
    {
        try
        {
            if (!Directory.Exists(_options.CacheDirectory))
                return Result.Success();

            await Task.Run(() =>
            {
                var files = Directory.GetFiles(_options.CacheDirectory, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete cache file at path: {FilePath}", file);
                    }

                // Remove empty directories
                var directories = Directory.GetDirectories(_options.CacheDirectory, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length); // Delete deepest first

                foreach (var directory in directories)
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(directory).Any())
                            Directory.Delete(directory);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete empty directory at path: {DirectoryPath}", directory);
                    }
            });

            _inProgressDownloads.Clear();
            _logger.LogInformation("Cache cleared successfully from directory: {CacheDirectory}", _options.CacheDirectory);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache from directory: {CacheDirectory}", _options.CacheDirectory);
            return Result.Failure($"Failed to clear cache: {ex.Message}");
        }
    }

    public async Task<Result<long>> GetCacheSizeAsync()
    {
        try
        {
            if (!Directory.Exists(_options.CacheDirectory))
                return Result.Success(0L);

            var totalSize = await Task.Run(() =>
                Directory.GetFiles(_options.CacheDirectory, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length));

            return Result.Success(totalSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate cache size for directory: {CacheDirectory}", _options.CacheDirectory);
            return Result.Failure<long>($"Failed to calculate cache size: {ex.Message}");
        }
    }

    private Result<Uri> ValidateMediaUrl(Uri url)
    {
        return UriValidator.ValidateProxyUri(url)
            .Bind(ValidateMediaExtension);
    }

    private Result<Uri> ValidateMediaExtension(Uri url)
    {
        return !IsValidMediaExtension(url.AbsolutePath)
            ? Result.Failure<Uri>($"URL does not have a supported media extension: {url}")
            : Result.Success(url);
    }

    private async Task<Result<CachedMedia>> GetOrDownloadMedia(Uri resolvedUrl, CancellationToken cancellationToken)
    {
        var cachePathResult = _pathProvider.GetCacheFilePath(resolvedUrl);
        if (cachePathResult.IsFailure)
            return Result.Failure<CachedMedia>(cachePathResult.Error);

        var cacheFilePath = cachePathResult.Value;
        var urlKey = resolvedUrl.ToString();

        // Check if file already exists in cache
        if (File.Exists(cacheFilePath))
        {
            _logger.LogDebug("Cache hit for URL: {Url} at path: {CacheFilePath}", resolvedUrl, cacheFilePath);
            return CreateCachedMediaFromFile(cacheFilePath);
        }

        // Check for in-progress download using Maybe pattern
        var existingDownload = _inProgressDownloads.GetMaybe(urlKey);
        if (existingDownload.HasValue)
        {
            _logger.LogDebug("Waiting for in-progress download of URL: {Url}", resolvedUrl);
            return await existingDownload.Value;
        }

        // Start new download
        var downloadTask = DownloadAndCacheMedia(resolvedUrl, cacheFilePath, cancellationToken);
        var actualTask = _inProgressDownloads.GetOrAdd(urlKey, downloadTask);

        if (actualTask != downloadTask)
        {
            _logger.LogDebug("Another download started while preparing URL: {Url}", resolvedUrl);
            return await actualTask;
        }

        try
        {
            return await actualTask;
        }
        finally
        {
            _inProgressDownloads.TryRemove(urlKey, out _);
        }
    }

    private async Task<Result<CachedMedia>> DownloadAndCacheMedia(Uri url, string cacheFilePath,
        CancellationToken cancellationToken)
    {
        await _downloadSemaphore.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("Starting download of media from URL: {Url}", url);

            var tempFilePath = cacheFilePath + ".tmp";
            EnsureDirectoryExists(Path.GetDirectoryName(cacheFilePath)!);

            using var response =
                await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Result.Failure<CachedMedia>(
                    $"HTTP request failed with status {response.StatusCode}: {response.ReasonPhrase}");

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var contentLength = response.Content.Headers.ContentLength;
            
            // Collect headers for metadata
            var headers = new Dictionary<string, string>();
            
            // Add Content-Type with full value (including charset)
            if (response.Content.Headers.ContentType != null)
                headers["Content-Type"] = response.Content.Headers.ContentType.ToString();
            
            // Add other important headers
            foreach (var header in response.Headers)
            {
                if (IsImportantHeader(header.Key))
                    headers[header.Key] = string.Join(", ", header.Value);
            }
            
            foreach (var header in response.Content.Headers)
            {
                if (IsImportantContentHeader(header.Key) && !headers.ContainsKey(header.Key))
                    headers[header.Key] = string.Join(", ", header.Value);
            }

            if (contentLength.HasValue && contentLength.Value > _options.MaxFileSizeBytes)
                return Result.Failure<CachedMedia>(
                    $"File size ({contentLength.Value} bytes) exceeds maximum allowed size ({_options.MaxFileSizeBytes} bytes)");

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = File.Create(tempFilePath);

            await contentStream.CopyToAsync(fileStream, cancellationToken);
            await fileStream.FlushAsync(cancellationToken);

            // Get file size before disposing the stream
            var fileSize = fileStream.Length;

            // Ensure stream is closed before moving
            await fileStream.DisposeAsync();

            // Atomic rename to prevent race conditions
            File.Move(tempFilePath, cacheFilePath);
            
            // Save headers metadata
            SaveHeadersMetadata(cacheFilePath, headers);

            var absolutePath = Path.GetFullPath(cacheFilePath);
            var cachedMedia = new CachedMedia(absolutePath, contentType, fileSize, DateTime.UtcNow);

            _logger.LogInformation("Successfully cached media from URL: {Url} with size: {FileSize} bytes at path: {CacheFilePath}", url, fileSize, cacheFilePath);
            return Result.Success(cachedMedia);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download and cache media from URL: {Url}", url);

            // Cleanup on failure
            try
            {
                var tempFilePath = cacheFilePath + ".tmp";
                if (File.Exists(tempFilePath))
                    File.Delete(tempFilePath);
                if (File.Exists(cacheFilePath))
                    File.Delete(cacheFilePath);
                CleanupMetadataFile(cacheFilePath);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to cleanup failed download files for URL: {Url}", url);
            }

            return Result.Failure<CachedMedia>($"Failed to download media: {ex.Message}");
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private Result<CachedMedia> CreateCachedMediaFromFile(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var contentType = GetContentTypeFromExtension(Path.GetExtension(filePath));
            var absolutePath = Path.GetFullPath(filePath);
            return Result.Success(new CachedMedia(absolutePath, contentType, fileInfo.Length,
                fileInfo.CreationTimeUtc));
        }
        catch (Exception ex)
        {
            return Result.Failure<CachedMedia>($"Failed to read cached file: {ex.Message}");
        }
    }

    private bool IsValidMediaExtension(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return _options.AllowedExtensions.Contains(extension);
    }

    private static string GetContentTypeFromExtension(string extension)
    {
        return ContentTypeResolver.GetContentTypeWithFallback(extension);
    }

    private void EnsureCacheDirectoryExists()
    {
        EnsureDirectoryExists(_options.CacheDirectory);
    }

    private static void EnsureDirectoryExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            Directory.CreateDirectory(directoryPath);
    }
    
    private void SaveHeadersMetadata(string cacheFilePath, Dictionary<string, string> headers)
    {
        if (headers.Count > 0)
        {
            var json = JsonSerializer.Serialize(headers);
            File.WriteAllText(GetMetadataFilePath(cacheFilePath), json);
        }
    }
    
    private string GetMetadataFilePath(string cacheFilePath)
    {
        return cacheFilePath + ".meta";
    }
    
    private void CleanupMetadataFile(string cacheFilePath)
    {
        try
        {
            var metadataPath = GetMetadataFilePath(cacheFilePath);
            if (File.Exists(metadataPath))
                File.Delete(metadataPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup metadata file for cache path: {CacheFilePath}", cacheFilePath);
        }
    }
    
    private static bool IsImportantHeader(string headerName)
    {
        return headerName.Equals("Accept-Ranges", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Content-Language", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Expires", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Vary", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("X-Content-Type-Options", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("ETag", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
               headerName.StartsWith("X-", StringComparison.OrdinalIgnoreCase);
    }
    
    private static bool IsImportantContentHeader(string headerName)
    {
        return headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Content-Language", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase);
    }
}