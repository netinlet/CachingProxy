using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RichardSzalay.MockHttp;

namespace CachingProxy.Server.Tests;

[TestClass]
public class CachingProxyTests
{
    private CachingProxy _cachingProxy = null!;
    private HttpClient _httpClient = null!;
    private MockHttpMessageHandler _mockHttpHandler = null!;
    private ILogger<CachingProxy> _mockLogger = null!;
    private string _tempCacheDir = null!;

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
    public async Task Cleanup()
    {
        await _cachingProxy.DisposeAsync();
        _httpClient.Dispose();
        _mockHttpHandler.Dispose();
        if (Directory.Exists(_tempCacheDir)) Directory.Delete(_tempCacheDir, true);
    }

    #region Constructor Tests

    [TestMethod]
    public void Constructor_NullCacheDirectory_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new CachingProxy(null!));
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
        _ = new CachingProxy(newDir);

        Assert.IsTrue(Directory.Exists(newDir));
    }

    #endregion

    #region ValidateAndPrepareAsync Cache Hit Tests

    [TestMethod]
    public async Task ValidateAndPrepareAsync_CacheHit_ReturnsSuccessWithCachedData()
    {
        // Arrange
        const string url = "https://example.com/image.png";
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
        const string url = "https://example.com/image.png";
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
        const string url = "https://example.com/image.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond(HttpStatusCode.OK, "image/png", "");

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Response.ContentType);
        StringAssert.StartsWith(result.Response.ContentType, "image/png");
    }

    [TestMethod]
    public async Task ValidateAndPrepareAsync_CacheMiss_LogsInformation()
    {
        // Arrange
        const string url = "https://example.com/image.png";
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
        const string url = "https://example.com/notfound.png";
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
        const string url = "https://example.com/error.png";
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
        const string url = "https://example.com/secure.png";
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
        const string url = "https://example.com/forbidden.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond(HttpStatusCode.Forbidden);

        // Act
        var result = await _cachingProxy.ValidateAndPrepareAsync(url);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.ErrorMessage);
    }


    [TestMethod]
    public async Task ValidateAndPrepareAsync_503ServiceUnavailable_ReturnsFailure()
    {
        // Arrange
        const string url = "https://example.com/unavailable.png";
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
        const string url = "https://example.com/slow.png";
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
        const string url = "https://example.com/slow.png";
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
        const string url = "https://example.com/image.png";
        const string content = "cached content";
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
        const string url = "https://example.com/image.png";
        const string content = "fresh content";
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Respond("image/png", content);

        var responseStream = new MemoryStream();

        // Act
        var result = await _cachingProxy.ServeAsync(url, responseStream);

        // Assert
        var servedContent = Encoding.UTF8.GetString(responseStream.ToArray());
        Assert.AreEqual(content, servedContent);
        Assert.IsNotNull(result.ContentType);
        StringAssert.StartsWith( result.ContentType,"image/png");

        await responseStream.DisposeAsync();

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
        const string url = "https://example.com/image.png";
        const string content = "fresh content";
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
        const string url = "https://example.com/image.png";
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("", Encoding.UTF8, "image/png")
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"12345\"");
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(3600) };
                response.Content.Headers.LastModified = DateTimeOffset.Parse("Wed, 21 Oct 2015 07:28:00 GMT");
                return response;
            });

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
        const string url = "https://example.com/image.png";
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
        const string url = "https://example.com/large-file.bin";
        var largeContent = new string('A', 10000); // 10KB content
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Respond("application/octet-stream", largeContent);

        var responseStream = new MemoryStream();

        // Act
        var result = await _cachingProxy.ServeAsync(url, responseStream);

        // Assert
        var servedContent = Encoding.UTF8.GetString(responseStream.ToArray());
        Assert.AreEqual(largeContent, servedContent);
        Assert.AreEqual(10000, result.ContentLength);

        await responseStream.DisposeAsync();
    }

    [TestMethod]
    public async Task ServeAsync_HttpRequestException_ThrowsException()
    {
        // Arrange
        const string url = "https://example.com/error.png";
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Throw(new HttpRequestException("Network error"));

        using var responseStream = new MemoryStream();

        // Act & Assert
        await Assert.ThrowsExceptionAsync<HttpRequestException>(async () =>
            await _cachingProxy.ServeAsync(url, responseStream));
    }

    [TestMethod]
    public async Task ServeAsync_TaskCanceledException_ThrowsException()
    {
        // Arrange
        const string url = "https://example.com/timeout.png";
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Throw(new TaskCanceledException("Request timeout"));

        using var responseStream = new MemoryStream();

        // Act & Assert
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () =>
            await _cachingProxy.ServeAsync(url, responseStream));
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
    public async Task ValidateAndPrepareAsync_SuccessStatusCodes_ReturnSuccess()
    {
        var testCases = new[]
        {
            (HttpStatusCode.OK, "https://example.com/200.png"),
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

    [TestMethod]
    public async Task ValidateAndPrepareAsync_RedirectStatusCodes_ReturnFailure()
    {
        var testCases = new[]
        {
            (HttpStatusCode.Redirect, "https://example.com/302.png"),
            (HttpStatusCode.Found, "https://example.com/302-found.png")
        };

        foreach (var (statusCode, url) in testCases)
        {
            // Arrange
            _mockHttpHandler.When(HttpMethod.Head, url)
                .Respond(statusCode, "image/png", "");

            // Act
            var result = await _cachingProxy.ValidateAndPrepareAsync(url);

            // Assert
            Assert.IsFalse(result.Success, $"Status code {statusCode} should return failure");
            Assert.IsNotNull(result.ErrorMessage, $"Status code {statusCode} should have error message");
        }
    }

    #endregion

    #region Race Condition Tests

    [TestMethod]
    public async Task ServeAsync_SimultaneousRequests_OnlyOneDownloadOccurs()
    {
        // Arrange
        const string url = "https://example.com/large-file.bin";
        const string content = "large file content that takes time to download";
        
        // Use a delay to simulate slow download
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Respond(async request =>
            {
                await Task.Delay(100); // Simulate network delay
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/octet-stream")
                };
            });

        using var responseStream1 = new MemoryStream();
        using var responseStream2 = new MemoryStream();
        using var responseStream3 = new MemoryStream();

        // Act - Start three simultaneous requests
        var task1 = _cachingProxy.ServeAsync(url, responseStream1);
        var task2 = _cachingProxy.ServeAsync(url, responseStream2);  
        var task3 = _cachingProxy.ServeAsync(url, responseStream3);

        var results = await Task.WhenAll(task1, task2, task3);

        // Assert
        var content1 = Encoding.UTF8.GetString(responseStream1.ToArray());
        var content2 = Encoding.UTF8.GetString(responseStream2.ToArray());
        var content3 = Encoding.UTF8.GetString(responseStream3.ToArray());

        Assert.AreEqual(content, content1);
        Assert.AreEqual(content, content2);
        Assert.AreEqual(content, content3);

        // Verify all responses have the same properties
        Assert.AreEqual(results[0].ContentType, results[1].ContentType);
        Assert.AreEqual(results[0].ContentType, results[2].ContentType);

        // Verify the cache file was created only once
        var cacheFilePath = GetExpectedCacheFilePath(url);
        Assert.IsTrue(File.Exists(cacheFilePath));
        var cachedContent = await File.ReadAllTextAsync(cacheFilePath);
        Assert.AreEqual(content, cachedContent);

        // Verify that request coalescing was logged
        _mockLogger.Received().Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Request coalescing")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [TestMethod]
    public async Task ValidateAndPrepareAsync_SimultaneousRequests_HandlesInProgressDownloads()
    {
        // Arrange
        const string url = "https://example.com/test-file.txt";
        const string content = "test content";
        
        _mockHttpHandler.When(HttpMethod.Head, url)
            .Respond("text/plain", "");
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Respond(async request =>
            {
                await Task.Delay(50); // Simulate delay
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "text/plain")
                };
            });

        // Act - Start download in background, then validate
        using var responseStream = new MemoryStream();
        var downloadTask = _cachingProxy.ServeAsync(url, responseStream);
        
        // Small delay to ensure download has started
        await Task.Delay(10);
        
        var validationResult = await _cachingProxy.ValidateAndPrepareAsync(url);
        
        await downloadTask;

        // Assert
        Assert.IsTrue(validationResult.Success);
        
        // Verify in-progress download was detected
        _mockLogger.Received().Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Download in progress")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [TestMethod]
    public async Task ServeAsync_ExceptionDuringDownload_CleansUpResources()
    {
        // Arrange
        const string url = "https://example.com/error-file.txt";
        
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Respond(request =>
            {
                // Simulate a network error after starting
                throw new HttpRequestException("Network error during download");
            });

        using var responseStream = new MemoryStream();

        // Act & Assert
        await Assert.ThrowsExceptionAsync<HttpRequestException>(async () =>
            await _cachingProxy.ServeAsync(url, responseStream));

        // Verify cleanup occurred
        var cacheFilePath = GetExpectedCacheFilePath(url);
        var tempFilePath = cacheFilePath + ".tmp";
        
        Assert.IsFalse(File.Exists(cacheFilePath), "Cache file should not exist after failed download");
        Assert.IsFalse(File.Exists(tempFilePath), "Temporary file should be cleaned up after failed download");
        Assert.IsFalse(File.Exists(cacheFilePath + ".meta"), "Metadata file should not exist after failed download");

        // Verify cleanup was logged
        _mockLogger.Received().Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Cleaned up partial cache files")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [TestMethod]
    public async Task ServeAsync_ConcurrentRequestsWithFailure_HandlesGracefully()
    {
        // Arrange
        const string url = "https://example.com/mixed-success.txt";
        var requestCount = 0;
        
        _mockHttpHandler.When(HttpMethod.Get, url)
            .Respond(request =>
            {
                var count = Interlocked.Increment(ref requestCount);
                if (count == 1)
                {
                    // First request fails
                    throw new HttpRequestException("First request fails");
                }
                
                // Subsequent requests should not happen due to coalescing
                throw new InvalidOperationException("Unexpected additional request");
            });

        using var responseStream1 = new MemoryStream();
        using var responseStream2 = new MemoryStream();

        // Act - Start two simultaneous requests
        var task1 = _cachingProxy.ServeAsync(url, responseStream1);
        var task2 = _cachingProxy.ServeAsync(url, responseStream2);

        // Assert - Both should fail with the same exception
        var exception1 = await Assert.ThrowsExceptionAsync<HttpRequestException>(async () => await task1);
        var exception2 = await Assert.ThrowsExceptionAsync<HttpRequestException>(async () => await task2);

        Assert.AreEqual("First request fails", exception1.Message);
        Assert.AreEqual("First request fails", exception2.Message);

        // Verify no cache files were created
        var cacheFilePath = GetExpectedCacheFilePath(url);
        Assert.IsFalse(File.Exists(cacheFilePath));
    }

    [TestMethod]
    public async Task ServeAsync_MaxConcurrentDownloads_RespectsLimit()
    {
        // Arrange - Create proxy with limit of 2 concurrent downloads
        await _cachingProxy.DisposeAsync();
        _cachingProxy = new CachingProxy(_tempCacheDir, _httpClient, _mockLogger, 2);

        var downloadStarted = 0;
        var downloadCompleted = 0;
        var maxConcurrent = 0;
        var currentConcurrent = 0;
        var lockObj = new object();

        _mockHttpHandler.When(HttpMethod.Get, "*")
            .Respond(async request =>
            {
                lock (lockObj)
                {
                    currentConcurrent++;
                    downloadStarted++;
                    maxConcurrent = Math.Max(maxConcurrent, currentConcurrent);
                }

                await Task.Delay(100); // Simulate work

                lock (lockObj)
                {
                    currentConcurrent--;
                    downloadCompleted++;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("test", Encoding.UTF8, "text/plain")
                };
            });

        // Act - Start 5 concurrent downloads
        var tasks = new List<Task<ProxyResponse>>();
        var streams = new List<MemoryStream>();
        
        for (int i = 0; i < 5; i++)
        {
            var stream = new MemoryStream();
            streams.Add(stream);
            tasks.Add(_cachingProxy.ServeAsync($"https://example.com/file{i}.txt", stream));
        }

        try
        {

            await Task.WhenAll(tasks);

            // Assert - No more than 2 concurrent downloads should have occurred
            Assert.AreEqual(5, downloadStarted);
            Assert.AreEqual(5, downloadCompleted);
            Assert.IsTrue(maxConcurrent <= 2, $"Max concurrent downloads was {maxConcurrent}, expected <= 2");
        }
        finally
        {
            // Clean up streams
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }

    #endregion

    #region Helper Methods

    private void CreateCacheFile(string url, string content, string contentType)
    {
        var headers = new Dictionary<string, string> { { "Content-Type", contentType } };
        CreateCacheFileWithHeaders(url, content, headers);
    }

    private string CreateCacheFileWithHeaders(string url, string content, Dictionary<string, string> headers)
    {
        var cacheFilePath = GetExpectedCacheFilePath(url);
        File.WriteAllText(cacheFilePath, content);

        var metadataPath = cacheFilePath + ".meta";
        var json = JsonSerializer.Serialize(headers);
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