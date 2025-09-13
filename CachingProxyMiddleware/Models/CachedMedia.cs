namespace CachingProxyMiddleware.Models;

public record CachedMedia(
    string FilePath,
    string ContentType,
    long Size,
    DateTime CachedAt
);