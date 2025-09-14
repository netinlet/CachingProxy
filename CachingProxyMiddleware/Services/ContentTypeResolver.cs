using CSharpFunctionalExtensions;

namespace CachingProxyMiddleware.Services;

public static class ContentTypeResolver
{
    private static readonly Dictionary<string, string> _contentTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".avi"] = "video/x-msvideo",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".flv"] = "video/x-flv",
        [".wmv"] = "video/x-ms-wmv"
    };

    /// <summary>
    ///     Gets the content type for a file extension, returning None if unknown.
    ///     This makes unknown content types explicit rather than falling back to application/octet-stream.
    /// </summary>
    private static Maybe<string> GetContentType(string extension)
    {
        return string.IsNullOrWhiteSpace(extension)
            ? Maybe<string>.None
            : _contentTypeMap.TryGetValue(extension, out var contentType)
                ? Maybe<string>.From(contentType)
                : Maybe<string>.None;
    }

    /// <summary>
    ///     Gets the content type for a file extension with a fallback for unknown types.
    /// </summary>
    public static string GetContentTypeWithFallback(string extension, string fallback = "application/octet-stream")
    {
        return GetContentType(extension)
            .GetValueOrDefault(fallback);
    }

    /// <summary>
    ///     Gets all supported file extensions that have known content types.
    /// </summary>
    public static string[] GetSupportedExtensions()
    {
        return _contentTypeMap.Keys.ToArray();
    }
}