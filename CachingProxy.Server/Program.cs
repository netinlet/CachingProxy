using CachingProxy.Server;

var builder = WebApplication.CreateBuilder(args);

// Configure options
builder.Services.Configure<CachingProxyOptions>(
    builder.Configuration.GetSection(CachingProxyOptions.SectionName));

// Register HttpClient with configuration
builder.Services.AddHttpClient<CachingProxy.Server.CachingProxy>(client =>
{
    var options = builder.Configuration.GetSection(CachingProxyOptions.SectionName).Get<CachingProxyOptions>() ??
                  new CachingProxyOptions();
    client.Timeout = options.HttpTimeout;
});

// Register CachingProxy as a singleton service
builder.Services.AddSingleton<CachingProxy.Server.CachingProxy>(provider =>
{
    var options = builder.Configuration.GetSection(CachingProxyOptions.SectionName).Get<CachingProxyOptions>() ??
                  new CachingProxyOptions();
    var httpClient = provider.GetRequiredService<HttpClient>();
    var logger = provider.GetRequiredService<ILogger<CachingProxy.Server.CachingProxy>>();
    return new CachingProxy.Server.CachingProxy(options.CacheDirectory, httpClient, logger);
});

var app = builder.Build();

// Caching proxy endpoint
app.MapGet("/proxy",
    async (string url, HttpResponse response, CancellationToken cancellationToken,
        CachingProxy.Server.CachingProxy cachingProxy) =>
    {
        if (string.IsNullOrEmpty(url))
            return Results.BadRequest("URL parameter is required");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return Results.BadRequest("Invalid URL format");

        // Set CORS headers before any potential errors
        response.Headers.Append("Access-Control-Allow-Origin", "*");
        response.Headers.Append("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
        response.Headers.Append("Access-Control-Allow-Headers", "*");

        // Validate and prepare response headers before streaming
        var validation = await cachingProxy.ValidateAndPrepareAsync(url, cancellationToken);
        if (!validation.Success)
        {
            // Return appropriate error status since response hasn't started
            if (validation.ErrorMessage?.Contains("timeout") == true)
                return Results.Problem("Request timeout", statusCode: 408);
            return Results.Problem($"Failed to fetch from origin: {validation.ErrorMessage}", statusCode: 502);
        }

        var proxyResponse = validation.Response;

        // Set headers before starting response stream
        response.ContentType = proxyResponse.ContentType ?? "application/octet-stream";

        if (proxyResponse.ContentLength.HasValue) response.ContentLength = proxyResponse.ContentLength.Value;

        // Set caching headers
        if (!string.IsNullOrEmpty(proxyResponse.ETag))
            response.Headers.Append("ETag", proxyResponse.ETag);
        if (proxyResponse.LastModified.HasValue)
            response.Headers.Append("Last-Modified", proxyResponse.LastModified.Value.ToString("R"));
        if (!string.IsNullOrEmpty(proxyResponse.CacheControl))
            response.Headers.Append("Cache-Control", proxyResponse.CacheControl);
        if (!string.IsNullOrEmpty(proxyResponse.ContentDisposition))
            response.Headers.Append("Content-Disposition", proxyResponse.ContentDisposition);

        // Set additional important headers
        foreach (var header in proxyResponse.AdditionalHeaders)
            if (!IsRestrictedHeader(header.Key))
                response.Headers.Append(header.Key, header.Value);

        // Now actually serve the content - this will start the response stream
        try
        {
            await cachingProxy.ServeAsync(url, response.Body, cancellationToken);
            return Results.Empty;
        }
        catch
        {
            // At this point response has started, so we can't return an error status
            // Just let the connection abort
            return Results.Empty;
        }

        static bool IsRestrictedHeader(string headerName)
        {
            return headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Server", StringComparison.OrdinalIgnoreCase);
        }
    });

// OPTIONS endpoint for CORS preflight
app.MapMethods("/proxy", new[] { "OPTIONS" }, () => Results.Ok());

// Health check endpoint
app.MapGet("/health", () => "OK");

// Configuration endpoint
app.MapGet("/config", (IConfiguration config) =>
{
    var options = config.GetSection(CachingProxyOptions.SectionName).Get<CachingProxyOptions>() ??
                  new CachingProxyOptions();
    return new
    {
        options.CacheDirectory,
        options.MaxCacheFileSizeMB,
        options.CacheRetentionDays,
        options.HttpTimeout
    };
});

// Info endpoint
app.MapGet("/", () => "CachingProxy API - Use /proxy?url=<your-url> to cache and serve content");

app.Run();