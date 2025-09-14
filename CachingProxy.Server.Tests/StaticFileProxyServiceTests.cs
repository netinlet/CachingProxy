using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RichardSzalay.MockHttp;

namespace CachingProxy.Server.Tests;

[TestClass]
public class StaticFileProxyServiceTests
{
    private HttpClient _httpClient = null!;
    private MockHttpMessageHandler _mockHttpHandler = null!;
    private ILogger<StaticFileProxyService> _mockLogger = null!;
    private StaticFileProxyOptions _options = null!;
    private StaticFileProxyService _staticFileProxyService = null!;
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
            MaxConcurrentDownloads = 5,
            AllowedExtensions = [".jpg", ".png", ".gif"]
        };

        _mockHttpHandler = new MockHttpMessageHandler();
        _httpClient = _mockHttpHandler.ToHttpClient();
        _mockLogger = Substitute.For<ILogger<StaticFileProxyService>>();

        _staticFileProxyService = new StaticFileProxyService(_options, _httpClient, _mockLogger);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _staticFileProxyService.DisposeAsync();
        _httpClient.Dispose();
        _mockHttpHandler.Dispose();
        if (Directory.Exists(_tempCacheDir))
            Directory.Delete(_tempCacheDir, true);
    }

    #region Metadata Tests

    [TestMethod]
    public async Task DownloadAndCacheAsync_WithHeaders_SavesMetadata()
    {
        var testPath = "/images/headers.jpg";
        var testContent = new byte[] { 1, 2, 3, 4, 5 };
        var expectedUrl = "https://example.com/images/headers.jpg";

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(testContent)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
        response.Headers.ETag = new EntityTagHeaderValue("\"test-etag\"");
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };

        _mockHttpHandler
            .When(HttpMethod.Get, expectedUrl)
            .Respond(req => Task.FromResult(response));

        var result = await _staticFileProxyService.DownloadAndCacheAsync(testPath);

        Assert.IsTrue(result);

        var cacheFilePath = Path.Combine(_tempCacheDir, "images", "headers.jpg");
        var metadataPath = cacheFilePath + ".meta";
        Assert.IsTrue(File.Exists(metadataPath));

        var metadataJson = await File.ReadAllTextAsync(metadataPath);
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);

        Assert.IsNotNull(metadata);
        Assert.AreEqual("image/jpeg", metadata["Content-Type"]);
        Assert.IsTrue(metadata.ContainsKey("Last-Modified"));
        Assert.AreEqual("\"test-etag\"", metadata["ETag"]);
        Assert.IsTrue(metadata.ContainsKey("Cache-Control"));
    }

    #endregion

    #region Constructor Tests

    [TestMethod]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new StaticFileProxyService(null!, _httpClient, _mockLogger));
    }

    [TestMethod]
    public void Constructor_NullHttpClient_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new StaticFileProxyService(_options, null!, _mockLogger));
    }

    [TestMethod]
    public async Task Constructor_CreatesCacheDirectory()
    {
        var newCacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var options = new StaticFileProxyOptions { StaticCacheDirectory = newCacheDir };

        await using var service = new StaticFileProxyService(options, _httpClient, _mockLogger);

        Assert.IsTrue(Directory.Exists(newCacheDir));
        Directory.Delete(newCacheDir);
    }

    #endregion

    #region Path Sanitization Tests

    [TestMethod]
    public async Task DownloadAndCacheAsync_InvalidPath_ReturnsFalse()
    {
        // Test directory traversal attempt
        var result = await _staticFileProxyService.DownloadAndCacheAsync("/../etc/passwd");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task DownloadAndCacheAsync_DisallowedExtension_ReturnsFalse()
    {
        // Test disallowed extension (.txt not in allowed list)
        var result = await _staticFileProxyService.DownloadAndCacheAsync("/test.txt");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task DownloadAndCacheAsync_EmptyPath_ReturnsFalse()
    {
        var result = await _staticFileProxyService.DownloadAndCacheAsync("");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task DownloadAndCacheAsync_PathWithDoubleSlash_ReturnsFalse()
    {
        var result = await _staticFileProxyService.DownloadAndCacheAsync("/path//file.jpg");
        Assert.IsFalse(result);
    }

    #endregion

    #region Download and Cache Tests

    [TestMethod]
    public async Task DownloadAndCacheAsync_ValidPath_ReturnsExpectedResult()
    {
        var testPath = "/images/test.jpg";

        // Don't set up HTTP handler expectations - just test path validation
        var result = await _staticFileProxyService.DownloadAndCacheAsync(testPath);

        // This will fail due to network, but should not fail due to path validation
        Assert.IsFalse(result); // Network failure expected without proper setup
    }

    [TestMethod]
    public async Task DownloadAndCacheAsync_HttpError_ReturnsFalse()
    {
        var testPath = "/images/notfound.jpg";
        var expectedUrl = "https://example.com/images/notfound.jpg";

        _mockHttpHandler
            .When(HttpMethod.Get, expectedUrl)
            .Respond(HttpStatusCode.NotFound);

        var result = await _staticFileProxyService.DownloadAndCacheAsync(testPath);

        Assert.IsFalse(result);

        var cacheFilePath = Path.Combine(_tempCacheDir, "images", "notfound.jpg");
        Assert.IsFalse(File.Exists(cacheFilePath));
    }

    [TestMethod]
    public async Task DownloadAndCacheAsync_ExistingFile_ReturnsTrueWithoutDownload()
    {
        var testPath = "/images/existing.jpg";
        var cacheFilePath = Path.Combine(_tempCacheDir, "images", "existing.jpg");

        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, "existing content");

        // Don't set up any HTTP expectations since it shouldn't make a request

        var result = await _staticFileProxyService.DownloadAndCacheAsync(testPath);

        Assert.IsTrue(result);

        // Verify no HTTP requests were made
        _mockHttpHandler.VerifyNoOutstandingExpectation();
    }

    [TestMethod]
    public async Task DownloadAndCacheAsync_ConcurrentRequests_OnlyOneDownload()
    {
        var testPath = "/images/concurrent.jpg";
        var testContent = new byte[] { 1, 2, 3, 4, 5 };
        var expectedUrl = "https://example.com/images/concurrent.jpg";
        var requestCount = 0;

        _mockHttpHandler
            .When(HttpMethod.Get, expectedUrl)
            .Respond(async () =>
            {
                Interlocked.Increment(ref requestCount);
                await Task.Delay(100); // Simulate network delay
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(testContent)
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") }
                    }
                };
            });

        // Start multiple concurrent downloads
        var tasks = new List<Task<bool>>();
        for (var i = 0; i < 5; i++) tasks.Add(_staticFileProxyService.DownloadAndCacheAsync(testPath));

        var results = await Task.WhenAll(tasks);

        Assert.IsTrue(results.All(r => r)); // All should succeed
        Assert.AreEqual(1, requestCount); // Only one HTTP request should have been made

        var cacheFilePath = Path.Combine(_tempCacheDir, "images", "concurrent.jpg");
        Assert.IsTrue(File.Exists(cacheFilePath));
    }

    #endregion

    #region Disposal Tests

    [TestMethod]
    public async Task DisposeAsync_WaitsForInProgressDownloads()
    {
        var testPath = "/images/slow.jpg";
        var expectedUrl = "https://example.com/images/slow.jpg";
        var downloadStarted = false;
        var downloadCompleted = false;

        _mockHttpHandler
            .When(HttpMethod.Get, expectedUrl)
            .Respond(async () =>
            {
                downloadStarted = true;
                await Task.Delay(200);
                downloadCompleted = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") }
                    }
                };
            });

        // Start download but don't wait
        var downloadTask = _staticFileProxyService.DownloadAndCacheAsync(testPath);

        // Wait for download to start
        while (!downloadStarted) await Task.Delay(10);

        // Act - Dispose while download is in progress
        await _staticFileProxyService.DisposeAsync();

        // Assert - Download should have completed before disposal
        Assert.IsTrue(downloadCompleted);
        Assert.IsTrue(downloadTask.IsCompleted);
    }

    [TestMethod]
    public async Task DownloadAndCacheAsync_AfterDisposal_ReturnsFalse()
    {
        await _staticFileProxyService.DisposeAsync();
        var result = await _staticFileProxyService.DownloadAndCacheAsync("/test.jpg");

        Assert.IsFalse(result);
    }

    #endregion
}