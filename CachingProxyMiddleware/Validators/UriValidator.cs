using CSharpFunctionalExtensions;

namespace CachingProxyMiddleware.Validators;

public static class UriValidator
{
    public static Result<Uri> ValidateProxyUri(Uri url)
    {
        return ValidateNotNull(url)
            .Bind(ValidateAbsoluteUri)
            .Bind(ValidateHttpScheme)
            .Bind(ValidateHost);
    }

    public static Result<Uri> ValidateProxyUriWithFileExtension(Uri url)
    {
        return ValidateProxyUri(url)
            .Bind(ValidateHasFileExtension);
    }

    private static Result<Uri> ValidateNotNull(Uri url)
    {
        return url == null
            ? Result.Failure<Uri>("URL cannot be null")
            : Result.Success(url);
    }

    private static Result<Uri> ValidateAbsoluteUri(Uri url)
    {
        return !url.IsAbsoluteUri
            ? Result.Failure<Uri>("URL must be absolute, not relative. Only full HTTP/HTTPS URLs are accepted for proxying")
            : Result.Success(url);
    }

    private static Result<Uri> ValidateHttpScheme(Uri url)
    {
        return url.Scheme != "http" && url.Scheme != "https"
            ? Result.Failure<Uri>($"Only HTTP and HTTPS schemes are supported for proxying. Received: {url.Scheme}")
            : Result.Success(url);
    }

    private static Result<Uri> ValidateHost(Uri url)
    {
        return string.IsNullOrWhiteSpace(url.Host)
            ? Result.Failure<Uri>("URL must have a valid host")
            : Result.Success(url);
    }

    private static Result<Uri> ValidateHasFileExtension(Uri url)
    {
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
}