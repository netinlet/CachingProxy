using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CachingProxy.Server.Tests;

[TestClass]
public class StaticFileProxyMiddlewareSimpleTests
{
    private StaticFileProxyMiddleware _middleware = null!;
    private StaticFileProxyService _realService = null!;
    private StaticFileProxyOptions _options = null!;
    private ILogger<StaticFileProxyMiddleware> _mockLogger = null!;
    private RequestDelegate _mockNext = null!;
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

        // Use a real service for basic functionality testing
        var httpClient = new HttpClient();
        _realService = new StaticFileProxyService(_options, httpClient, null);
        
        _mockLogger = Substitute.For<ILogger<StaticFileProxyMiddleware>>();
        _mockNext = Substitute.For<RequestDelegate>();

        _middleware = new StaticFileProxyMiddleware(_mockNext, _realService, _options, _mockLogger);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _realService.DisposeAsync();
        if (Directory.Exists(_tempCacheDir))
            Directory.Delete(_tempCacheDir, true);
    }

    #region Non-Static Requests Tests

    [TestMethod]
    public async Task InvokeAsync_NonGetRequest_CallsNextMiddleware()
    {
        // Arrange
        var context = CreateHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/static/test.jpg";

        // Act
        await _middleware.InvokeAsync(context);

        // Assert
        await _mockNext.Received(1).Invoke(context);
    }

    [TestMethod]
    public async Task InvokeAsync_NonStaticPath_CallsNextMiddleware()
    {
        // Arrange
        var context = CreateHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/test";

        // Act
        await _middleware.InvokeAsync(context);

        // Assert
        await _mockNext.Received(1).Invoke(context);
    }

    #endregion

    #region Cache Hit Tests

    [TestMethod]
    public async Task InvokeAsync_CacheHit_ServesFileDirectly()
    {
        // Arrange
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

        // Act
        await _middleware.InvokeAsync(context);

        // Assert
        await _mockNext.DidNotReceive().Invoke(Arg.Any<HttpContext>());
        
        Assert.AreEqual("image/jpeg", context.Response.ContentType);
        Assert.AreEqual(testContent.Length, context.Response.ContentLength);
        Assert.AreEqual("\"test-etag\"", context.Response.Headers.ETag.ToString());
        Assert.AreEqual("max-age=3600", context.Response.Headers.CacheControl.ToString());

        var responseContent = GetResponseContent(context);
        CollectionAssert.AreEqual(testContent, responseContent);
    }

    #endregion

    #region Content Type Tests

    [TestMethod]
    [DataRow(".jpg", "image/jpeg")]
    [DataRow(".jpeg", "image/jpeg")]
    [DataRow(".png", "image/png")]
    [DataRow(".gif", "image/gif")]
    [DataRow(".webp", "image/webp")]
    [DataRow(".svg", "image/svg+xml")]
    [DataRow(".unknown", "application/octet-stream")]
    public async Task InvokeAsync_DifferentExtensions_SetsCorrectContentType(string extension, string expectedContentType)
    {
        // Arrange
        var context = CreateHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = $"/static/test{extension}";

        var testContent = "test content"u8.ToArray();
        var cacheFilePath = Path.Combine(_tempCacheDir, $"test{extension}");
        
        await File.WriteAllBytesAsync(cacheFilePath, testContent);

        // Act
        await _middleware.InvokeAsync(context);

        // Assert
        Assert.AreEqual(expectedContentType, context.Response.ContentType);
    }

    #endregion

    #region Helper Methods

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static byte[] GetResponseContent(HttpContext context)
    {
        if (context.Response.Body is MemoryStream stream)
        {
            return stream.ToArray();
        }
        return [];
    }

    #endregion
}