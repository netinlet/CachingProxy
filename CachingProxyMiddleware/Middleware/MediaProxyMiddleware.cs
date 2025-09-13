using CachingProxyMiddleware.Interfaces;
using CSharpFunctionalExtensions;

namespace CachingProxyMiddleware.Middleware;

public class MediaProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMediaCacheService _cacheService;
    private readonly ILogger<MediaProxyMiddleware> _logger;

    public MediaProxyMiddleware(
        RequestDelegate next,
        IMediaCacheService cacheService,
        ILogger<MediaProxyMiddleware> logger)
    {
        _next = next;
        _cacheService = cacheService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only handle GET requests to /media endpoint with url parameter
        if (context.Request.Method != HttpMethods.Get ||
            !context.Request.Path.StartsWithSegments("/media") ||
            !context.Request.Query.ContainsKey("url"))
        {
            await _next(context);
            return;
        }

        var urlParam = context.Request.Query["url"].ToString();
        if (string.IsNullOrEmpty(urlParam))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("URL parameter is required");
            return;
        }

        if (!Uri.TryCreate(urlParam, UriKind.Absolute, out var requestUri))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid URL format");
            return;
        }

        var result = await _cacheService.GetOrCacheAsync(requestUri, context.RequestAborted);

        await result.Match(
            async cachedMedia =>
            {
                _logger.LogDebug("Serving cached media: {Url} -> {FilePath}", requestUri, cachedMedia.FilePath);
                
                context.Response.ContentType = cachedMedia.ContentType;
                context.Response.ContentLength = cachedMedia.Size;
                
                // Add cache headers
                context.Response.Headers.Append("Cache-Control", "public, max-age=31536000"); // 1 year
                context.Response.Headers.Append("Last-Modified", cachedMedia.CachedAt.ToString("R"));
                
                await context.Response.SendFileAsync(cachedMedia.FilePath);
            },
            async error =>
            {
                _logger.LogWarning("Failed to serve media for URL {Url}: {Error}", requestUri, error);
                
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync($"Failed to retrieve media: {error}");
            }
        );
    }
}