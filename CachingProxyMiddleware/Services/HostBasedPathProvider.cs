using System.Text.RegularExpressions;
using CachingProxyMiddleware.Interfaces;
using CachingProxyMiddleware.Models;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Options;

namespace CachingProxyMiddleware.Services;

public partial class HostBasedPathProvider : IHostBasedPathProvider
{
    private readonly MediaCacheOptions _options;
    
    [GeneratedRegex(@"[<>:""\\|?*]", RegexOptions.Compiled)]
    private static partial Regex InvalidCharsRegex();

    public HostBasedPathProvider(IOptions<MediaCacheOptions> options)
    {
        _options = options.Value;
    }

    public Result<string> GetCacheFilePath(Uri url)
    {
        return GetHostDirectory(url)
            .Bind(hostDir => SanitizePath(url.AbsolutePath)
                .Map(sanitizedPath => Path.Combine(hostDir, sanitizedPath.TrimStart('/'))));
    }

    public Result<string> GetHostDirectory(Uri url)
    {
        if (string.IsNullOrWhiteSpace(url.Host))
            return Result.Failure<string>("URL must have a valid host");

        var hostWithPort = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";
        var sanitizedHost = SanitizeHost(hostWithPort);
        var hostDirectory = Path.Combine(_options.CacheDirectory, sanitizedHost);
        
        return Result.Success(hostDirectory);
    }

    private static Result<string> SanitizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure<string>("Path cannot be empty");

        // Replace only invalid characters, but preserve forward slashes for directory structure
        var sanitized = InvalidCharsRegex().Replace(path, "_");
        
        // Handle multiple consecutive slashes
        sanitized = Regex.Replace(sanitized, @"/+", "/");
        
        // Ensure path doesn't start with invalid characters after sanitization
        if (sanitized.StartsWith("_"))
            sanitized = sanitized.TrimStart('_');
            
        return Result.Success(sanitized);
    }

    private static string SanitizeHost(string host)
    {
        return host.Replace(":", "_").Replace(".", "_");
    }
}