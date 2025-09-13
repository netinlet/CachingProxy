using CSharpFunctionalExtensions;

namespace CachingProxyMiddleware.Interfaces;

public interface IUrlResolver
{
    Task<Result<Uri>> ResolveAsync(Uri requestedUrl, CancellationToken cancellationToken = default);
}