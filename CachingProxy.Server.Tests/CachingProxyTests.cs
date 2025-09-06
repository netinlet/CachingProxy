using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using RichardSzalay.MockHttp;
using CachingProxy.Server;

namespace CachingProxy.Server.Tests;

[TestClass]
public class CachingProxyTests
{
    private string _tempCacheDir = null!;
    private MockHttpMessageHandler _mockHttpHandler = null!;
    private HttpClient _httpClient = null!;
    private ILogger<CachingProxy> _mockLogger = null!;
    private CachingProxy _cachingProxy = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempCacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempCacheDir);
        
        _mockHttpHandler = new MockHttpMessageHandler();
        _httpClient = _mockHttpHandler.ToHttpClient();
        _mockLogger = Substitute.For<ILogger<CachingProxy>>();
        
        _cachingProxy = new CachingProxy(_tempCacheDir, _httpClient, _mockLogger);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _httpClient?.Dispose();
        _mockHttpHandler?.Dispose();
        if (Directory.Exists(_tempCacheDir))
        {
            Directory.Delete(_tempCacheDir, true);
        }
    }

    #region Constructor Tests

    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void Constructor_NullCacheDirectory_ThrowsArgumentNullException()
    {
        new CachingProxy(null!);
    }

    [TestMethod]
    public void Constructor_ValidParameters_CreatesInstance()
    {
        var proxy = new CachingProxy(_tempCacheDir);
        Assert.IsNotNull(proxy);
    }

    [TestMethod]
    public void Constructor_NonExistentDirectory_CreatesDirectory()
    {
        var newDir = Path.Combine(_tempCacheDir, "newdir");
        var proxy = new CachingProxy(newDir);
        
        Assert.IsTrue(Directory.Exists(newDir));
    }

    #endregion

    #region ValidateAndPrepareAsync Cache Hit Tests

    [TestMethod]
    public async Task ValidateAndPrepareAsync_CacheHit_ReturnsSuccessWithCachedData()
    {
        // Arrange
        var url = "https://example.com/image.png";
        CreateCacheFile(url, "test content", "image/png");

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual("image/png", result.Response.ContentType);
        Assert.AreEqual(12, result.Response.ContentLength); // "test content" length
        Assert.IsNull(result.ErrorMessage);
    }

    [TestMethod]
    public async Task ValidateAndPrepareAsync_CacheHit_LogsInformation()
    {
        // Arrange
        var url = "https://example.com/image.png";
        CreateCacheFile(url, "test content", "image/png");

        // Act
        await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Cache HIT")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    #endregion

    #region ValidateAndPrepareAsync Cache Miss Tests

    [TestMethod]
    public async Task ValidateAndPrepareAsync_CacheMiss_PerformsHeadRequest()
    {
        // Arrange
        var url = "https://example.com/image.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond(HttpStatusCode.OK, "image/png", "");

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual("image/png", result.Response.ContentType);
    }

    [TestMethod]
    public async Task ValidateAndPrepareAsync_CacheMiss_LogsInformation()
    {
        // Arrange
        var url = "https://example.com/image.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond(HttpStatusCode.OK, "image/png", "");

        // Act
        await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Cache MISS")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    #endregion

    #region HTTP Status Code Tests

    [TestMethod]
    public async Task ValidateAndPrepareAsync_404NotFound_ReturnsFailure()
    {
        // Arrange
        var url = "https://example.com/notfound.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond(HttpStatusCode.NotFound);

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.ErrorMessage);
    }

    [TestMethod]
    public async Task ValidateAndPrepareAsync_500InternalServerError_ReturnsFailure()
    {
        // Arrange
        var url = "https://example.com/error.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond(HttpStatusCode.InternalServerError);

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.ErrorMessage);
    }

    [TestMethod]
    public async Task ValidateAndPrepareAsync_401Unauthorized_ReturnsFailure()
    {
        // Arrange
        var url = "https://example.com/secure.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond(HttpStatusCode.Unauthorized);

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.ErrorMessage);
    }

    [TestMethod]
    public async Task ValidateAndPrepareAsync_403Forbidden_ReturnsFailure()
    {
        // Arrange
        var url = "https://example.com/forbidden.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond(HttpStatusCode.Forbidden);

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.ErrorMessage);
    }

    [TestMethod]
    public async Task ValidateAndPrepareAsync_302Redirect_ReturnsSuccess()
    {
        // Arrange
        var url = "https://example.com/redirect.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond(HttpStatusCode.Redirect, "image/png", "");

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual("image/png", result.Response.ContentType);
    }

    [TestMethod]
    public async Task ValidateAndPrepareAsync_503ServiceUnavailable_ReturnsFailure()
    {
        // Arrange
        var url = "https://example.com/unavailable.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond(HttpStatusCode.ServiceUnavailable);

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.ErrorMessage);
    }

    #endregion

    #region Timeout Tests

    [TestMethod]
    public async Task ValidateAndPrepareAsync_Timeout_ReturnsFailure()
    {
        // Arrange
        var url = "https://example.com/slow.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Throw(new TaskCanceledException("Request timeout"));

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.ErrorMessage!.Contains("timeout"));
    }

    [TestMethod]
    public async Task ValidateAndPrepareAsync_Timeout_LogsWarning()
    {
        // Arrange
        var url = "https://example.com/slow.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Throw(new TaskCanceledException("Request timeout"));

        // Act
        await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("timeout")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    #endregion

    #region ServeAsync Tests

    [TestMethod]
    public async Task ServeAsync_CacheHit_ServesCachedContent()
    {
        // Arrange
        var url = "https://example.com/image.png";
        var content = "cached content";
        CreateCacheFile(url, content, "image/png");
        
        using var responseStream = new MemoryStream();

        // Act
        var result = await _cachingProxy.ServeAsync(url, responseStream);

        // Assert
        responseStream.Position = 0;
        var servedContent = await new StreamReader(responseStream).ReadToEndAsync();
        Assert.AreEqual(content, servedContent);
        Assert.AreEqual("image/png", result.ContentType);
    }

    [TestMethod]
    public async Task ServeAsync_CacheMiss_FetchesAndCachesContent()
    {
        // Arrange
        var url = "https://example.com/image.png";
        var content = "fresh content";
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Respond("image/png", content);
        
        using var responseStream = new MemoryStream();

        // Act
        var result = await _cachingProxy.ServeAsync(url, responseStream);

        // Assert
        responseStream.Position = 0;
        var servedContent = await new StreamReader(responseStream).ReadToEndAsync();
        Assert.AreEqual(content, servedContent);
        Assert.AreEqual("image/png", result.ContentType);
        
        // Verify content was cached
        var cacheFilePath = GetExpectedCacheFilePath(url);
        Assert.IsTrue(File.Exists(cacheFilePath));
        var cachedContent = await File.ReadAllTextAsync(cacheFilePath);
        Assert.AreEqual(content, cachedContent);
    }

    [TestMethod]
    public async Task ServeAsync_CacheMiss_LogsCacheStorage()
    {
        // Arrange
        var url = "https://example.com/image.png";
        var content = "fresh content";
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Respond("image/png", content);
        
        using var responseStream = new MemoryStream();

        // Act
        await _cachingProxy.ServeAsync(url, responseStream);

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Cached content for URL")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    #endregion

    #region Header Tests

    [TestMethod]
    public async Task ValidateAndPrepareAsync_WithHeaders_PreservesImportantHeaders()
    {
        // Arrange
        var url = "https://example.com/image.png";
        var response = _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond(HttpStatusCode.OK, "image/png", "");
        
        response.WithHeaders("ETag", "\"12345\"");
        response.WithHeaders("Cache-Control", "max-age=3600");
        response.WithHeaders("Last-Modified", "Wed, 21 Oct 2015 07:28:00 GMT");

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual("\"12345\"", result.Response.ETag);
        Assert.AreEqual("max-age=3600", result.Response.CacheControl);
        Assert.IsTrue(result.Response.LastModified.HasValue);
    }

    [TestMethod]
    public async Task ValidateAndPrepareAsync_CachedHeaders_RestoresHeaders()
    {
        // Arrange
        var url = "https://example.com/image.png";
        var headers = new Dictionary<string, string>
        {
            { "Content-Type", "image/png" },
            { "ETag", "\"cached-etag\"" },
            { "Cache-Control", "max-age=7200" }
        };
        
        CreateCacheFileWithHeaders(url, "content", headers);

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual("image/png", result.Response.ContentType);
        Assert.AreEqual("\"cached-etag\"", result.Response.ETag);
        Assert.AreEqual("max-age=7200", result.Response.CacheControl);
    }

    #endregion

    #region Edge Cases

    [TestMethod]
    public async Task ServeAsync_LargeContent_HandledCorrectly()
    {
        // Arrange
        var url = "https://example.com/large-file.bin";
        var largeContent = new string('A', 10000); // 10KB content
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Respond("application/octet-stream", largeContent);
        
        using var responseStream = new MemoryStream();

        // Act
        var result = await _cachingProxy.ServeAsync(url, responseStream);

        // Assert
        responseStream.Position = 0;
        var servedContent = await new StreamReader(responseStream).ReadToEndAsync();
        Assert.AreEqual(largeContent, servedContent);
        Assert.AreEqual(10000, result.ContentLength);
    }

    [TestMethod]
    public async Task ServeAsync_HttpRequestException_LogsError()
    {
        // Arrange
        var url = "https://example.com/error.png";
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Throw(new HttpRequestException("Network error"));
        
        using var responseStream = new MemoryStream();

        // Act & Assert
        await Assert.ThrowsExceptionAsync<HttpRequestException>(
            async () => await _cachingProxy.ServeAsync(url, responseStream));
        
        _mockLogger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Failed to cache content")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [TestMethod]
    public async Task ServeAsync_TaskCanceledException_LogsError()
    {
        // Arrange
        var url = "https://example.com/timeout.png";
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Throw(new TaskCanceledException("Request timeout"));
        
        using var responseStream = new MemoryStream();

        // Act & Assert
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            async () => await _cachingProxy.ServeAsync(url, responseStream));
        
        _mockLogger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Failed to cache content")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    #endregion

    #region Multiple Status Code Scenarios

    [TestMethod]
    public async Task ValidateAndPrepareAsync_MultipleBadStatusCodes_AllReturnFailure()
    {
        var testCases = new[]
        {
            (HttpStatusCode.NotFound, "https://example.com/404.png"),
            (HttpStatusCode.InternalServerError, "https://example.com/500.png"),
            (HttpStatusCode.BadGateway, "https://example.com/502.png"),
            (HttpStatusCode.ServiceUnavailable, "https://example.com/503.png"),
            (HttpStatusCode.GatewayTimeout, "https://example.com/504.png"),
            (HttpStatusCode.Unauthorized, "https://example.com/401.png"),
            (HttpStatusCode.Forbidden, "https://example.com/403.png")
        };

        foreach (var (statusCode, url) in testCases)
        {
            // Arrange
            _mockHttpHandler.When(HttpMethod.Head, url)
                .Respond(statusCode);

            // Act
            var result = await _cachingProxy.ValidateAndPrepareAsync(url);

            // Assert
            Assert.IsFalse(result.Success, $"Status code {statusCode} should return failure");
            Assert.IsNotNull(result.ErrorMessage, $"Status code {statusCode} should have error message");
        }
    }

    [TestMethod]
    public async Task ValidateAndPrepareAsync_MultipleSuccessStatusCodes_AllReturnSuccess()
    {
        var testCases = new[]
        {
            (HttpStatusCode.OK, "https://example.com/200.png"),
            (HttpStatusCode.Redirect, "https://example.com/302.png"),
            (HttpStatusCode.NotModified, "https://example.com/304.png")
        };

        foreach (var (statusCode, url) in testCases)
        {
            // Arrange
            _mockHttpHandler.When(HttpMethod.Head, url)
                .Respond(statusCode, "image/png", "");

            // Act
            var result = await _cachingProxy.ValidateAndPrepareAsync(url);

            // Assert
            Assert.IsTrue(result.Success, $"Status code {statusCode} should return success");
            Assert.IsNull(result.ErrorMessage, $"Status code {statusCode} should not have error message");
        }
    }

    #endregion

    #region Helper Methods

    private string CreateCacheFile(string url, string content, string contentType)
    {
        var headers = new Dictionary<string, string> { { "Content-Type", contentType } };
        return CreateCacheFileWithHeaders(url, content, headers);
    }

    private string CreateCacheFileWithHeaders(string url, string content, Dictionary<string, string> headers)
    {
        var cacheFilePath = GetExpectedCacheFilePath(url);
        File.WriteAllText(cacheFilePath, content);
        
        var metadataPath = cacheFilePath + ".meta";
        var json = System.Text.Json.JsonSerializer.Serialize(headers);
        File.WriteAllText(metadataPath, json);
        
        return cacheFilePath;
    }

    private string GetExpectedCacheFilePath(string url)
    {
        var uri = new Uri(url);
        var fileName = $"{uri.Host}_{uri.PathAndQuery}".Replace('/', '_').Replace('?', '_').Replace(':', '_');
        
        if (fileName.Length > 100)
        {
            var hash = url.GetHashCode().ToString("X");
            fileName = fileName.Substring(0, 90) + "_" + hash;
        }
        
        return Path.Combine(_tempCacheDir, fileName);
    }

    #endregion
}