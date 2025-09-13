using CachingProxyMiddleware.Models;
using CSharpFunctionalExtensions;

namespace CachingProxyMiddleware.Interfaces;

public interface IMediaCacheService
{
    Task<Result<CachedMedia>> GetOrCacheAsync(Uri url, CancellationToken cancellationToken = default);
    Task<Result> ClearCacheAsync();
    Task<Result<long>> GetCacheSizeAsync();
}