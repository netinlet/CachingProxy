using CSharpFunctionalExtensions;

namespace CachingProxyMiddleware.Interfaces;

public interface IHostBasedPathProvider
{
    Result<string> GetCacheFilePath(Uri url);
    Result<string> GetHostDirectory(Uri url);
}