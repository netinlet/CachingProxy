using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CachingProxy.Server.Tests;

[TestClass]
public class StaticFileProxyMiddlewareTests
{
    private StaticFileProxyMiddleware _middleware = null!;
    private ILogger<StaticFileProxyMiddleware> _mockLogger = null!;
    private RequestDelegate _mockNext = null!;
    private StaticFileProxyService _mockService = null!;
    private StaticFileProxyOptions _options = null!;
    private string _tempCacheDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempCacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempCacheDir);

        _options = new StaticFileProxyOptions
        {
            BaseUrl = "https://example.com",
            StaticCacheDirectory = _tempCacheDir,
            AllowedExtensions = [".jpg", ".png", ".gif"]
        };

        _mockService = Substitute.For<StaticFileProxyService>(_options, Substitute.For<HttpClient>(), null);
        _mockLogger = Substitute.For<ILogger<StaticFileProxyMiddleware>>();
        _mockNext = Substitute.For<RequestDelegate>();

        _middleware = new StaticFileProxyMiddleware(_mockNext, _mockService, _options, _mockLogger);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempCacheDir))
            Directory.Delete(_tempCacheDir, true);
    }

    #region Content Type Tests

    [TestMethod]
    [DataRow(".jpg", "image/jpeg")]
    [DataRow(".jpeg", "image/jpeg")]
    [DataRow(".png", "image/png")]
    [DataRow(".gif", "image/gif")]
    [DataRow(".webp", "image/webp")]
    [DataRow(".svg", "image/svg+xml")]
    [DataRow(".unknown", "application/octet-stream")]
    public async Task InvokeAsync_DifferentExtensions_SetsCorrectContentType(string extension,
        string expectedContentType)
    {
        var context = CreateHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = $"/static/test{extension}";

        var testContent = "test content"u8.ToArray();
        var cacheFilePath = Path.Combine(_tempCacheDir, $"test{extension}");

        await File.WriteAllBytesAsync(cacheFilePath, testContent);

        await _middleware.InvokeAsync(context);

        Assert.AreEqual(expectedContentType, context.Response.ContentType);
    }

    #endregion

    #region Non-Static Requests Tests

    [TestMethod]
    public async Task InvokeAsync_NonGetRequest_CallsNextMiddleware()
    {
        var context = CreateHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/static/test.jpg";

        await _middleware.InvokeAsync(context);

        await _mockNext.Received(1).Invoke(context);
        await _mockService.DidNotReceive().DownloadAndCacheAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task InvokeAsync_NonStaticPath_CallsNextMiddleware()
    {
        var context = CreateHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/test";

        await _middleware.InvokeAsync(context);

        await _mockNext.Received(1).Invoke(context);
        await _mockService.DidNotReceive().DownloadAndCacheAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Cache Hit Tests

    [TestMethod]
    public async Task InvokeAsync_CacheHit_ServesFileDirectly()
    {
        var context = CreateHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/static/images/test.jpg";

        var testContent = "test image content"u8.ToArray();
        var cacheFilePath = Path.Combine(_tempCacheDir, "images", "test.jpg");
        var metadataPath = cacheFilePath + ".meta";

        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllBytesAsync(cacheFilePath, testContent);

        var metadata = new Dictionary<string, string>
        {
            ["Content-Type"] = "image/jpeg",
            ["ETag"] = "\"test-etag\"",
            ["Cache-Control"] = "max-age=3600"
        };
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata));

        await _middleware.InvokeAsync(context);

        await _mockNext.DidNotReceive().Invoke(Arg.Any<HttpContext>());
        await _mockService.DidNotReceive().DownloadAndCacheAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        Assert.AreEqual("image/jpeg", context.Response.ContentType);
        Assert.AreEqual(testContent.Length, context.Response.ContentLength);
        Assert.AreEqual("\"test-etag\"", context.Response.Headers.ETag.ToString());
        Assert.AreEqual("max-age=3600", context.Response.Headers.CacheControl.ToString());

        var responseContent = GetResponseContent(context);
        CollectionAssert.AreEqual(testContent, responseContent);
    }

    [TestMethod]
    public async Task InvokeAsync_CacheHitNoMetadata_ServesFileWithDefaultContentType()
    {
        var context = CreateHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/static/images/test.jpg";

        var testContent = "test image content"u8.ToArray();
        var cacheFilePath = Path.Combine(_tempCacheDir, "images", "test.jpg");

        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllBytesAsync(cacheFilePath, testContent);

        await _middleware.InvokeAsync(context);

        Assert.AreEqual("image/jpeg", context.Response.ContentType); // Inferred from .jpg extension
        Assert.AreEqual(testContent.Length, context.Response.ContentLength);

        var responseContent = GetResponseContent(context);
        CollectionAssert.AreEqual(testContent, responseContent);
    }

    #endregion

    #region Cache Miss and Download Tests

    [TestMethod]
    public async Task InvokeAsync_CacheMissSuccessfulDownload_ServesDownloadedFile()
    {
        var context = CreateHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/static/images/download.jpg";

        var testContent = "downloaded content"u8.ToArray();
        var cacheFilePath = Path.Combine(_tempCacheDir, "images", "download.jpg");

        _mockService.DownloadAndCacheAsync("/images/download.jpg", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true))
            .AndDoes(callInfo =>
            {
                // Simulate successful download by creating the cache file
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
                File.WriteAllBytes(cacheFilePath, testContent);

                var metadata = new Dictionary<string, string> { ["Content-Type"] = "image/jpeg" };
                File.WriteAllText(cacheFilePath + ".meta", JsonSerializer.Serialize(metadata));
            });

        await _middleware.InvokeAsync(context);

        await _mockService.Received(1).DownloadAndCacheAsync("/images/download.jpg", Arg.Any<CancellationToken>());

        Assert.AreEqual("image/jpeg", context.Response.ContentType);
        Assert.AreEqual(testContent.Length, context.Response.ContentLength);

        var responseContent = GetResponseContent(context);
        CollectionAssert.AreEqual(testContent, responseContent);
    }

    [TestMethod]
    public async Task InvokeAsync_CacheMissFailedDownload_Returns502()
    {
        var context = CreateHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/static/images/failed.jpg";

        _mockService.DownloadAndCacheAsync("/images/failed.jpg", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        await _middleware.InvokeAsync(context);

        await _mockService.Received(1).DownloadAndCacheAsync("/images/failed.jpg", Arg.Any<CancellationToken>());

        Assert.AreEqual(502, context.Response.StatusCode);

        var responseContent = Encoding.UTF8.GetString(GetResponseContent(context));
        Assert.AreEqual("Failed to fetch from origin", responseContent);
    }

    [TestMethod]
    public async Task InvokeAsync_DownloadCancelled_Returns408()
    {
        var context = CreateHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/static/images/cancelled.jpg";

        _mockService.DownloadAndCacheAsync("/images/cancelled.jpg", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new OperationCanceledException()));

        await _middleware.InvokeAsync(context);

        Assert.AreEqual(408, context.Response.StatusCode);

        var responseContent = Encoding.UTF8.GetString(GetResponseContent(context));
        Assert.AreEqual("Download timeout", responseContent);
    }

    [TestMethod]
    public async Task InvokeAsync_DownloadException_Returns500()
    {
        var context = CreateHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/static/images/error.jpg";

        _mockService.DownloadAndCacheAsync("/images/error.jpg", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("Test error")));

        await _middleware.InvokeAsync(context);

        Assert.AreEqual(500, context.Response.StatusCode);

        var responseContent = Encoding.UTF8.GetString(GetResponseContent(context));
        Assert.AreEqual("Internal server error", responseContent);
    }

    #endregion

    #region Helper Methods

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() }
        };
        return context;
    }

    private static byte[] GetResponseContent(HttpContext context)
    {
        if (context.Response.Body is MemoryStream stream) return stream.ToArray();
        return [];
    }

    #endregion
}