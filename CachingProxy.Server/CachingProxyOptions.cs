namespace CachingProxy.Server;

public class CachingProxyOptions
{
    public const string SectionName = "CachingProxy";

    public string CacheDirectory { get; set; } = "./cache";
    public int MaxCacheFileSizeMB { get; set; } = 100;
    public int CacheRetentionDays { get; set; } = 7;
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public int MaxConcurrentDownloads { get; set; } = 10;
    public TimeSpan InProgressRequestTimeout { get; set; } = TimeSpan.FromMinutes(5);
}