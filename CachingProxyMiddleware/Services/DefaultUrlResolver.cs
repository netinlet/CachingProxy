using CachingProxyMiddleware.Interfaces;
using CSharpFunctionalExtensions;

namespace CachingProxyMiddleware.Services;

public class DefaultUrlResolver : IUrlResolver
{
    public Task<Result<Uri>> ResolveAsync(Uri requestedUrl, CancellationToken cancellationToken = default)
    {
        if (requestedUrl.Scheme != Uri.UriSchemeHttp && requestedUrl.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult(Result.Failure<Uri>("URL must use HTTP or HTTPS scheme"));

        return Task.FromResult(Result.Success(requestedUrl));
    }
}