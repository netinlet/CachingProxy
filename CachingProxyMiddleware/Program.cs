using CachingProxyMiddleware.Extensions;
using CachingProxyMiddleware.Interfaces;
using CachingProxyMiddleware.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Add media cache services
builder.Services.AddMediaCache(builder.Configuration);

var app = builder.Build();

// Add media proxy middleware
app.UseMiddleware<MediaProxyMiddleware>();

// Direct API endpoint for media proxy
app.MapGet("/proxy", async (string url, IMediaCacheService cacheService) =>
{
    if (string.IsNullOrEmpty(url))
        return Results.BadRequest("URL parameter is required");

    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        return Results.BadRequest("Invalid URL format");

    var result = await cacheService.GetOrCacheAsync(uri);

    if (result.IsSuccess)
        return Results.File(result.Value.FilePath, result.Value.ContentType);

    return Results.Problem($"Failed to retrieve media: {result.Error}", statusCode: 502);
});

// Cache management endpoints
app.MapPost("/cache/clear", async (IMediaCacheService cacheService) =>
{
    var result = await cacheService.ClearCacheAsync();

    if (result.IsSuccess)
        return Results.Ok(new { Message = "Cache cleared successfully" });

    return Results.Problem($"Failed to clear cache: {result.Error}");
});

app.MapGet("/cache/size", async (IMediaCacheService cacheService) =>
{
    var result = await cacheService.GetCacheSizeAsync();

    if (result.IsSuccess)
        return Results.Ok(new { SizeBytes = result.Value, SizeMB = result.Value / (1024.0 * 1024.0) });

    return Results.Problem($"Failed to get cache size: {result.Error}");
});

// Health check
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow }));

// Info endpoint
app.MapGet("/", () => Results.Ok(new
{
    Service = "Media Cache Proxy",
    Endpoints = new[]
    {
        "GET /proxy?url=<media-url> - Proxy and cache media",
        "GET /media?url=<media-url> - Alternative proxy endpoint (middleware)",
        "POST /cache/clear - Clear all cached media",
        "GET /cache/size - Get total cache size",
        "GET /health - Health check"
    }
}));

app.Run();