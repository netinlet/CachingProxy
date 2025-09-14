using System.Collections.Concurrent;
using System.Net;
using CachingProxyMiddleware.Interfaces;
using CachingProxyMiddleware.Models;
using CachingProxyMiddleware.Services;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using RichardSzalay.MockHttp;

namespace CachingProxyMiddleware.Tests.Services;

[TestClass]
public class MediaCacheServiceConcurrencyTests
{
    private MediaCacheService _service = null!;
    private MockHttpMessageHandler _mockHttpHandler = null!;
    private HttpClient _httpClient = null!;
    private IHostBasedPathProvider _mockPathProvider = null!;
    private IUrlResolver _mockUrlResolver = null!;
    private ILogger<MediaCacheService> _mockLogger = null!;
    private string _tempCacheDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempCacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempCacheDir);

        var options = Options.Create(new MediaCacheOptions 
        { 
            CacheDirectory = _tempCacheDir,
            MaxConcurrentDownloads = 3
        });
        
        _mockHttpHandler = new MockHttpMessageHandler();
        _httpClient = _mockHttpHandler.ToHttpClient();
        _mockPathProvider = Substitute.For<IHostBasedPathProvider>();
        _mockUrlResolver = Substitute.For<IUrlResolver>();
        _mockLogger = Substitute.For<ILogger<MediaCacheService>>();

        _service = new MediaCacheService(options, _httpClient, _mockPathProvider, _mockUrlResolver, _mockLogger);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _service.DisposeAsync();
        _httpClient.Dispose();
        _mockHttpHandler.Dispose();
        
        if (Directory.Exists(_tempCacheDir))
            Directory.Delete(_tempCacheDir, true);
    }

    #region Race Condition Tests

    [TestMethod]
    public async Task GetOrCacheAsync_SimultaneousRequests_OnlyOneDownloadOccurs()
    {
        // Arrange
        var url = new Uri("https://example.com/concurrent-test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "concurrent-test.jpg");
        var imageData = "concurrent-test-data"u8.ToArray();
        var downloadCount = 0;

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(async _ =>
            {
                Interlocked.Increment(ref downloadCount);
                await Task.Delay(100); // Simulate network delay
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageData)
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                    }
                };
            });

        // Act - Start 5 simultaneous requests
        var tasks = new List<Task<Result<CachedMedia>>>();
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(_service.GetOrCacheAsync(url));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.IsTrue(results.All(r => r.IsSuccess), "All requests should succeed");
        Assert.AreEqual(1, downloadCount, "Only one download should have occurred");

        // Verify all results point to the same cached file
        foreach (var result in results)
        {
            Assert.AreEqual(cacheFilePath, result.Value.FilePath);
            Assert.AreEqual("image/jpeg", result.Value.ContentType);
            Assert.AreEqual(imageData.Length, result.Value.Size);
        }

        // Verify the cache file exists and has correct content
        Assert.IsTrue(File.Exists(cacheFilePath));
        var cachedData = await File.ReadAllBytesAsync(cacheFilePath);
        CollectionAssert.AreEqual(imageData, cachedData);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_ConcurrentRequestsWithFailure_AllReturnSameFailure()
    {
        // Arrange
        var url = new Uri("https://example.com/failing-concurrent.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "failing-concurrent.jpg");
        var requestCount = 0;

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(request =>
            {
                var count = Interlocked.Increment(ref requestCount);
                if (count == 1)
                {
                    // First request fails
                    throw new HttpRequestException("Network failure");
                }
                
                // Subsequent requests should not happen due to coalescing
                throw new InvalidOperationException("Unexpected additional request");
            });

        // Act - Start multiple simultaneous requests
        var tasks = new List<Task<Result<CachedMedia>>>();
        for (int i = 0; i < 3; i++)
        {
            tasks.Add(_service.GetOrCacheAsync(url));
        }

        var results = await Task.WhenAll(tasks);

        // Assert - All should fail with the same error
        Assert.IsTrue(results.All(r => r.IsFailure), "All requests should fail");
        Assert.AreEqual(1, requestCount, "Only one HTTP request should have been made");

        // Verify all errors are the same
        var firstError = results[0].Error;
        Assert.IsTrue(results.All(r => r.Error.Contains("Network failure")), 
            "All errors should reference the network failure");

        // Verify no cache file was created
        Assert.IsFalse(File.Exists(cacheFilePath));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_MaxConcurrentDownloads_RespectsLimit()
    {
        // Arrange - Test with service configured for max 3 concurrent downloads
        var downloadStarted = 0;
        var downloadCompleted = 0;
        var maxConcurrent = 0;
        var currentConcurrent = 0;
        var lockObj = new object();
        var urls = new List<Uri>();
        var cacheFilePaths = new List<string>();

        // Create 5 different URLs
        for (int i = 0; i < 5; i++)
        {
            var url = new Uri($"https://example.com/file{i}.jpg");
            var cacheFilePath = Path.Combine(_tempCacheDir, $"file{i}.jpg");
            urls.Add(url);
            cacheFilePaths.Add(cacheFilePath);

            _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
                .Returns(Result.Success(url));
            
            _mockPathProvider.GetCacheFilePath(url)
                .Returns(Result.Success(cacheFilePath));
        }

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
                    Content = new ByteArrayContent("test-data"u8.ToArray())
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                    }
                };
            });

        // Act - Start 5 concurrent downloads
        var tasks = urls.Select(url => _service.GetOrCacheAsync(url)).ToList();
        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.IsTrue(results.All(r => r.IsSuccess), "All downloads should succeed");
        Assert.AreEqual(5, downloadStarted, "All 5 downloads should have started");
        Assert.AreEqual(5, downloadCompleted, "All 5 downloads should have completed");
        Assert.IsTrue(maxConcurrent <= 3, $"Max concurrent downloads was {maxConcurrent}, expected <= 3");
    }

    [TestMethod]
    public async Task GetOrCacheAsync_ExceptionDuringDownload_CleansUpResources()
    {
        // Arrange
        var url = new Uri("https://example.com/error-cleanup.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "error-cleanup.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(request =>
            {
                // Simulate a network error after starting
                throw new HttpRequestException("Network error during download");
            });

        // Act
        var result = await _service.GetOrCacheAsync(url);

        // Assert
        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("Network error"));

        // Verify cleanup occurred
        var tempFilePath = cacheFilePath + ".tmp";
        Assert.IsFalse(File.Exists(cacheFilePath), "Cache file should not exist after failed download");
        Assert.IsFalse(File.Exists(tempFilePath), "Temporary file should be cleaned up after failed download");
    }

    [TestMethod]
    public async Task GetOrCacheAsync_ConcurrentRequestsDifferentUrls_AllSucceed()
    {
        // Arrange
        var urls = new[]
        {
            new Uri("https://example.com/image1.jpg"),
            new Uri("https://example.com/image2.png"),
            new Uri("https://example.com/image3.gif")
        };

        var cacheFilePaths = new[]
        {
            Path.Combine(_tempCacheDir, "image1.jpg"),
            Path.Combine(_tempCacheDir, "image2.png"),
            Path.Combine(_tempCacheDir, "image3.gif")
        };

        for (int i = 0; i < urls.Length; i++)
        {
            _mockUrlResolver.ResolveAsync(urls[i], Arg.Any<CancellationToken>())
                .Returns(Result.Success(urls[i]));
            
            _mockPathProvider.GetCacheFilePath(urls[i])
                .Returns(Result.Success(cacheFilePaths[i]));

            _mockHttpHandler.When(HttpMethod.Get, urls[i].ToString())
                .Respond(HttpStatusCode.OK, "image/jpeg", $"content-{i}");
        }

        // Act - Start concurrent requests for different URLs
        var tasks = urls.Select(url => _service.GetOrCacheAsync(url)).ToList();
        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.IsTrue(results.All(r => r.IsSuccess), "All requests should succeed");

        // Verify each result points to the correct file
        for (int i = 0; i < results.Length; i++)
        {
            Assert.AreEqual(cacheFilePaths[i], results[i].Value.FilePath);
            Assert.IsTrue(File.Exists(cacheFilePaths[i]), $"Cache file {i} should exist");
        }
    }

    [TestMethod]
    public async Task GetOrCacheAsync_DisposeWhileDownloading_HandlesGracefully()
    {
        // Arrange
        var url = new Uri("https://example.com/slow-download.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "slow-download.jpg");
        var downloadStarted = false;

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(async request =>
            {
                downloadStarted = true;
                await Task.Delay(200); // Simulate slow download
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("slow-content"u8.ToArray())
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                    }
                };
            });

        // Act - Start download but dispose service while it's in progress
        var downloadTask = _service.GetOrCacheAsync(url);
        
        // Wait for download to start
        while (!downloadStarted)
        {
            await Task.Delay(10);
        }

        // Dispose while download is in progress
        await _service.DisposeAsync();

        // Wait for download task to complete
        var result = await downloadTask;

        // Assert - Download should complete successfully even after disposal
        Assert.IsTrue(result.IsSuccess || result.IsFailure, "Download task should complete (either success or failure)");
        
        // If successful, verify the file was created
        if (result.IsSuccess)
        {
            Assert.IsTrue(File.Exists(cacheFilePath));
        }
    }

    [TestMethod]
    public async Task GetOrCacheAsync_HighConcurrencyStressTest_AllRequestsHandledCorrectly()
    {
        // Arrange - Test with many concurrent requests to stress test the system
        var url = new Uri("https://example.com/stress-test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "stress-test.jpg");
        var imageData = "stress-test-data"u8.ToArray();
        var downloadCount = 0;

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(async request =>
            {
                Interlocked.Increment(ref downloadCount);
                await Task.Delay(50); // Shorter delay for stress test
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageData)
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                    }
                };
            });

        // Act - Start 20 simultaneous requests
        var tasks = new List<Task<Result<CachedMedia>>>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(_service.GetOrCacheAsync(url));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.IsTrue(results.All(r => r.IsSuccess), "All requests should succeed");
        Assert.AreEqual(1, downloadCount, "Only one download should have occurred despite 20 requests");

        // Verify all results are consistent
        foreach (var result in results)
        {
            Assert.AreEqual(cacheFilePath, result.Value.FilePath);
            Assert.AreEqual("image/jpeg", result.Value.ContentType);
            Assert.AreEqual(imageData.Length, result.Value.Size);
        }
    }

    #endregion

    #region Request Coalescing Tests

    [TestMethod]
    public async Task GetOrCacheAsync_RequestCoalescing_WorksCorrectlyWithMixedSuccessFailure()
    {
        // Arrange
        var successUrl = new Uri("https://example.com/success.jpg");
        var failureUrl = new Uri("https://example.com/failure.jpg");
        
        var successCachePath = Path.Combine(_tempCacheDir, "success.jpg");
        var failureCachePath = Path.Combine(_tempCacheDir, "failure.jpg");
        
        var successDownloadCount = 0;
        var failureDownloadCount = 0;

        // Setup success URL
        _mockUrlResolver.ResolveAsync(successUrl, Arg.Any<CancellationToken>())
            .Returns(Result.Success(successUrl));
        _mockPathProvider.GetCacheFilePath(successUrl)
            .Returns(Result.Success(successCachePath));

        // Setup failure URL  
        _mockUrlResolver.ResolveAsync(failureUrl, Arg.Any<CancellationToken>())
            .Returns(Result.Success(failureUrl));
        _mockPathProvider.GetCacheFilePath(failureUrl)
            .Returns(Result.Success(failureCachePath));

        _mockHttpHandler.When(HttpMethod.Get, successUrl.ToString())
            .Respond(async request =>
            {
                Interlocked.Increment(ref successDownloadCount);
                await Task.Delay(100);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("success-data"u8.ToArray())
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                    }
                };
            });

        _mockHttpHandler.When(HttpMethod.Get, failureUrl.ToString())
            .Respond(request =>
            {
                Interlocked.Increment(ref failureDownloadCount);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        // Act - Start concurrent requests for both URLs
        var successTasks = Enumerable.Range(0, 3).Select(_ => _service.GetOrCacheAsync(successUrl));
        var failureTasks = Enumerable.Range(0, 3).Select(_ => _service.GetOrCacheAsync(failureUrl));
        
        var allTasks = successTasks.Concat(failureTasks).ToList();
        var results = await Task.WhenAll(allTasks);

        // Assert
        var successResults = results.Take(3).ToArray();
        var failureResults = results.Skip(3).ToArray();

        Assert.IsTrue(successResults.All(r => r.IsSuccess), "All success URL requests should succeed");
        Assert.IsTrue(failureResults.All(r => r.IsFailure), "All failure URL requests should fail");

        Assert.AreEqual(1, successDownloadCount, "Only one download should occur for success URL");
        Assert.AreEqual(1, failureDownloadCount, "Only one download should occur for failure URL");
    }

    #endregion

    #region Timeout and Cancellation Tests

    [TestMethod]
    public async Task GetOrCacheAsync_CancellationToken_CancelsRequest()
    {
        // Arrange
        var url = new Uri("https://example.com/cancellation-test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "cancellation-test.jpg");
        var downloadStarted = false;

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(async _ =>
            {
                downloadStarted = true;
                await Task.Delay(1000); // Long delay
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("data"u8.ToArray())
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                    }
                };
            });

        using var cts = new CancellationTokenSource();

        // Act - Start request and cancel it shortly after
        var requestTask = _service.GetOrCacheAsync(url, cts.Token);
        
        // Wait for download to start then cancel
        while (!downloadStarted) await Task.Delay(10, cts.Token);
        await cts.CancelAsync();

        // Assert
        var result = await requestTask;
        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("cancel") || result.Error.Contains("timeout"));
    }

    #endregion
}
