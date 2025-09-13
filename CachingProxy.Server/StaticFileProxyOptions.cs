namespace CachingProxy.Server;

public class StaticFileProxyOptions
{
    public const string SectionName = "StaticFileProxy";

    public string BaseUrl { get; set; } = "https://example.com";
    public string StaticCacheDirectory { get; set; } = "./static-cache";
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public int MaxConcurrentDownloads { get; set; } = 10;
    public string[] AllowedExtensions { get; set; } = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg"];
}