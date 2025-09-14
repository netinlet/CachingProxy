using System.Text.Json;

namespace CachingProxy.Server;

public class StaticFileProxyMiddleware
{
    private readonly ILogger<StaticFileProxyMiddleware> _logger;
    private readonly RequestDelegate _next;
    private readonly StaticFileProxyOptions _options;
    private readonly StaticFileProxyService _proxyService;

    public StaticFileProxyMiddleware(RequestDelegate next, StaticFileProxyService proxyService,
        StaticFileProxyOptions options, ILogger<StaticFileProxyMiddleware> logger)
    {
        _next = next;
        _proxyService = proxyService;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only handle GET requests to /static/*
        if (context.Request.Method != "GET" ||
            !context.Request.Path.StartsWithSegments("/static", out var remainingPath))
        {
            await _next(context);
            return;
        }

        // Try to serve from cache first
        var cacheFilePath = GetCacheFilePath(remainingPath);
        if (File.Exists(cacheFilePath))
        {
            await ServeFile(context, cacheFilePath);
            return;
        }

        // File not cached, try to download
        var originalPath = remainingPath.ToString();
        _logger.LogDebug("Static file not found, attempting download: {Path}", originalPath);

        try
        {
            var success = await _proxyService.DownloadAndCacheAsync(originalPath, context.RequestAborted);

            if (success && File.Exists(cacheFilePath))
            {
                _logger.LogDebug("Download successful, serving cached file: {Path}", originalPath);
                await ServeFile(context, cacheFilePath);
            }
            else
            {
                _logger.LogWarning("Failed to download file: {Path}", remainingPath);
                context.Response.StatusCode = 502; // Bad Gateway
                await context.Response.WriteAsync("Failed to fetch from origin");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Download cancelled: {Path}", remainingPath);
            context.Response.StatusCode = 408; // Request Timeout
            await context.Response.WriteAsync("Download timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading file: {Path}", remainingPath);
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Internal server error");
        }
    }

    private async Task ServeFile(HttpContext context, string cacheFilePath)
    {
        var fileInfo = new FileInfo(cacheFilePath);

        // Try to determine content type from file extension first
        var contentType = GetContentType(cacheFilePath);
        var metadataPath = cacheFilePath + ".meta";
        if (File.Exists(metadataPath))
            try
            {
                var json = await File.ReadAllTextAsync(metadataPath);
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                if (headers != null)
                {
                    // Override content type from metadata if available (more accurate than extension)
                    if (headers.TryGetValue("Content-Type", out var metaContentType))
                        contentType = metaContentType;

                    foreach (var header in headers)
                        if (header.Key != "Content-Type" && header.Key != "Content-Length")
                            context.Response.Headers.TryAdd(header.Key, header.Value);
                }
            }
            catch
            {
                // Ignore metadata errors
            }

        // Set the final content type and length
        context.Response.ContentType = contentType;
        context.Response.ContentLength = fileInfo.Length;

        // Stream the file
        await using var fileStream = File.OpenRead(cacheFilePath);
        await fileStream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private string GetCacheFilePath(PathString remainingPath)
    {
        var relativePath = remainingPath.ToString().TrimStart('/');
        return Path.Combine(_options.StaticCacheDirectory, relativePath);
    }

    private static string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }
}