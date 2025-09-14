using CachingProxyMiddleware.Services;

namespace CachingProxyMiddleware.Models;

public class MediaCacheOptions
{
    public const string SectionName = "MediaCache";

    public string CacheDirectory { get; set; } = "./media-cache";
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public int MaxConcurrentDownloads { get; set; } = 10;
    public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB

    public string[] AllowedExtensions { get; set; } = ContentTypeResolver.GetSupportedExtensions();
}