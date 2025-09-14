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
        return ValidateProxyUri(url)
            .Bind(_ => GetHostDirectory(url))
            .Bind(hostDir => SanitizePath(url.AbsolutePath)
                .Map(sanitizedPath => Path.Combine(hostDir, sanitizedPath.TrimStart('/'))));
    }

    public Result<string> GetHostDirectory(Uri url)
    {
        return ValidateProxyUri(url)
            .Bind(_ => {
                var hostWithPort = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";
                var sanitizedHost = SanitizeHost(hostWithPort);
                var hostDirectory = Path.Combine(_options.CacheDirectory, sanitizedHost);
                
                return Result.Success(hostDirectory);
            });
    }

    private static Result<Uri> ValidateProxyUri(Uri url)
    {
        if (url == null)
            return Result.Failure<Uri>("URL cannot be null");

        if (!url.IsAbsoluteUri)
            return Result.Failure<Uri>("URL must be absolute, not relative. Only full HTTP/HTTPS URLs are accepted for proxying");

        if (url.Scheme != "http" && url.Scheme != "https")
            return Result.Failure<Uri>($"Only HTTP and HTTPS schemes are supported for proxying. Received: {url.Scheme}");

        if (string.IsNullOrWhiteSpace(url.Host))
            return Result.Failure<Uri>("URL must have a valid host");

        // Validate that the path has a file extension (required for media proxy)
        var path = url.AbsolutePath;
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return Result.Failure<Uri>("URL must contain a file path with extension. Root paths are not valid for media proxying");

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
            return Result.Failure<Uri>("URL must contain a file name with extension");

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
            return Result.Failure<Uri>("URL must contain a file with a valid extension. Files without extensions are not supported");

        return Result.Success(url);
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