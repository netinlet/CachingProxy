using CSharpFunctionalExtensions;

namespace CachingProxyMiddleware.Validators;

public static class UriValidator
{
    public static Result<Uri> ValidateProxyUri(Uri url)
    {
        return Maybe.From(url)
            .ToResult("URL cannot be null")
            .Bind(ValidateAbsoluteUri)
            .Bind(ValidateHttpScheme)
            .Bind(ValidateHost);
    }

    public static Result<Uri> ValidateProxyUriWithFileExtension(Uri url)
    {
        return ValidateProxyUri(url)
            .Bind(ValidateHasFileExtension)
            .Bind(ValidateNoPathTraversal);
    }

    private static Result<Uri> ValidateAbsoluteUri(Uri url)
    {
        return Validate(url, u => u.IsAbsoluteUri,
            "URL must be absolute, not relative. Only full HTTP/HTTPS URLs are accepted for proxying");
    }

    private static Result<Uri> ValidateHttpScheme(Uri url)
    {
        return Validate(url, u => u.Scheme is "http" or "https",
            $"Only HTTP and HTTPS schemes are supported for proxying. Received: {url?.Scheme}");
    }

    private static Result<Uri> ValidateHost(Uri url)
    {
        return Validate(url, u => !string.IsNullOrWhiteSpace(u.Host), "URL must have a valid host");
    }

    private static Result<Uri> ValidateHasFileExtension(Uri url)
    {
        var path = url.AbsolutePath;
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return Result.Failure<Uri>(
                "URL must contain a file path with extension. Root paths are not valid for media proxying");

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
            return Result.Failure<Uri>("URL must contain a file name with extension");

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
            return Result.Failure<Uri>(
                "URL must contain a file with a valid extension. Files without extensions are not supported");

        return Result.Success(url);
    }

    private static Result<Uri> ValidateNoPathTraversal(Uri url)
    {
        var path = url.AbsolutePath;

        // Check for path traversal sequences in the original URL (before normalization)
        var originalUrl = url.OriginalString;
        if (originalUrl.Contains("../") || originalUrl.Contains("..\\") || originalUrl.Contains("%2e%2e"))
            return Result.Failure<Uri>("Path traversal attempts are not allowed for security reasons");

        // Check for suspicious system paths that might indicate traversal attempts
        var suspiciousPaths = new[]
            { "/etc/", "/usr/", "/var/", "/bin/", "/sbin/", "/root/", "/home/", "/tmp/", "/proc/", "/sys/" };
        if (suspiciousPaths.Any(suspiciousPath => path.StartsWith(suspiciousPath, StringComparison.OrdinalIgnoreCase)))
            return Result.Failure<Uri>("Access to system paths is not allowed for security reasons");

        return Result.Success(url);
    }

    // helper to wrap simple validation logic in a Result
    private static Result<Uri> Validate(Uri url, Func<Uri, bool> predicate, string error)
    {
        return Maybe.From(url)
            .ToResult("URL cannot be null")
            .Ensure(predicate, error);
    }
}