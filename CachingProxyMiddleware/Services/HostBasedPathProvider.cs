using System.Text.RegularExpressions;
using CachingProxyMiddleware.Interfaces;
using CachingProxyMiddleware.Models;
using CachingProxyMiddleware.Validators;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Options;

namespace CachingProxyMiddleware.Services;

public partial class HostBasedPathProvider : IHostBasedPathProvider
{
    private readonly MediaCacheOptions _options;

    public HostBasedPathProvider(IOptions<MediaCacheOptions> options)
    {
        _options = options.Value;
    }

    public Result<string> GetCacheFilePath(Uri url)
    {
        return UriValidator.ValidateProxyUriWithFileExtension(url)
            .Bind(_ => GetHostDirectory(url))
            .Bind(hostDir => SanitizePath(url.AbsolutePath)
                .Map(sanitizedPath => Path.Combine(hostDir, sanitizedPath.TrimStart('/'))));
    }

    public Result<string> GetHostDirectory(Uri url)
    {
        return UriValidator.ValidateProxyUri(url)
            .Bind(_ =>
            {
                var hostWithPort = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";
                var sanitizedHost = SanitizeHost(hostWithPort);
                var hostDirectory = Path.Combine(_options.CacheDirectory, sanitizedHost);

                return Result.Success(hostDirectory);
            });
    }

    [GeneratedRegex(@"[<>:""\\|?* ]", RegexOptions.Compiled)]
    private static partial Regex InvalidCharsRegex();


    private static Result<string> SanitizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure<string>("Path cannot be empty");

        // SECURITY: URL-decode first to prevent encoding bypass attacks
        string decodedPath;
        try
        {
            decodedPath = Uri.UnescapeDataString(path);
        }
        catch (Exception)
        {
            return Result.Failure<string>("Invalid URL-encoded path");
        }

        // Check for directory traversal attempts after decoding
        if (decodedPath.Contains(".."))
            return Result.Failure<string>("Path traversal attempt detected");

        // Check for null bytes (can cause security issues)
        if (decodedPath.Contains('\0'))
            return Result.Failure<string>("Null byte detected in path");

        // Replace only invalid characters, but preserve forward slashes for directory structure
        var sanitized = InvalidCharsRegex().Replace(decodedPath, "_");

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