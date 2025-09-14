using CachingProxyMiddleware.Interfaces;
using CachingProxyMiddleware.Models;
using CachingProxyMiddleware.Services;

namespace CachingProxyMiddleware.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMediaCache(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MediaCacheOptions>(configuration.GetSection(MediaCacheOptions.SectionName));

        services.AddHttpClient<MediaCacheService>(client =>
        {
            var options = configuration.GetSection(MediaCacheOptions.SectionName).Get<MediaCacheOptions>() ??
                          new MediaCacheOptions();
            client.Timeout = options.HttpTimeout;
            client.DefaultRequestHeaders.Add("User-Agent", "MediaCacheProxy/1.0");
        });

        services.AddSingleton<IHostBasedPathProvider, HostBasedPathProvider>();
        services.AddSingleton<IUrlResolver, DefaultUrlResolver>();
        services.AddSingleton<IMediaCacheService, MediaCacheService>();

        return services;
    }

    public static IServiceCollection AddMediaCache<TUrlResolver>(this IServiceCollection services,
        IConfiguration configuration)
        where TUrlResolver : class, IUrlResolver
    {
        services.Configure<MediaCacheOptions>(configuration.GetSection(MediaCacheOptions.SectionName));

        services.AddHttpClient<MediaCacheService>(client =>
        {
            var options = configuration.GetSection(MediaCacheOptions.SectionName).Get<MediaCacheOptions>() ??
                          new MediaCacheOptions();
            client.Timeout = options.HttpTimeout;
            client.DefaultRequestHeaders.Add("User-Agent", "MediaCacheProxy/1.0");
        });

        services.AddSingleton<IHostBasedPathProvider, HostBasedPathProvider>();
        services.AddSingleton<IUrlResolver, TUrlResolver>();
        services.AddSingleton<IMediaCacheService, MediaCacheService>();

        return services;
    }
}