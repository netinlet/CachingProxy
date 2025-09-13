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

namespace CachingProxyMiddware.Tests.Services;

[TestClass]
public class MediaCacheServiceTests
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

        var options = Options.Create(new MediaCacheOptions { CacheDirectory = _tempCacheDir });
        
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

    [TestMethod]
    public async Task GetOrCacheAsync_ValidImageUrl_DownloadsAndCaches()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");
        var imageData = "fake-image-data"u8.ToArray();

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond("image/jpeg", new MemoryStream(imageData));

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(cacheFilePath, result.Value.FilePath);
        Assert.AreEqual("image/jpeg", result.Value.ContentType);
        Assert.IsTrue(File.Exists(cacheFilePath));
        
        var cachedData = await File.ReadAllBytesAsync(cacheFilePath);
        CollectionAssert.AreEqual(imageData, cachedData);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_ExistingCachedFile_ServeFromCache()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");
        var imageData = "cached-image-data"u8.ToArray();

        // Create pre-existing cache file
        await File.WriteAllBytesAsync(cacheFilePath, imageData);

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(cacheFilePath, result.Value.FilePath);
        Assert.AreEqual(imageData.Length, result.Value.Size);
        
        // Verify no HTTP request was made
        _mockHttpHandler.VerifyNoOutstandingExpectation();
    }

    [TestMethod]
    public async Task GetOrCacheAsync_InvalidExtension_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.txt");

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("supported media extension"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_HttpError_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.NotFound);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("404") || result.Error.Contains("NotFound"), 
            $"Expected error to contain '404' or 'NotFound' but got: {result.Error}");
    }

    [TestMethod]
    public async Task ClearCacheAsync_RemovesAllFiles()
    {
        // Create some test cache files
        var testFile1 = Path.Combine(_tempCacheDir, "test1.jpg");
        var testFile2 = Path.Combine(_tempCacheDir, "subdir", "test2.png");
        
        Directory.CreateDirectory(Path.GetDirectoryName(testFile2)!);
        await File.WriteAllTextAsync(testFile1, "test1");
        await File.WriteAllTextAsync(testFile2, "test2");

        var result = await _service.ClearCacheAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(File.Exists(testFile1));
        Assert.IsFalse(File.Exists(testFile2));
    }

    [TestMethod]
    public async Task GetCacheSizeAsync_CalculatesCorrectSize()
    {
        var testFile1 = Path.Combine(_tempCacheDir, "test1.jpg");
        var testFile2 = Path.Combine(_tempCacheDir, "test2.png");
        
        var data1 = new byte[100];
        var data2 = new byte[200];
        
        await File.WriteAllBytesAsync(testFile1, data1);
        await File.WriteAllBytesAsync(testFile2, data2);

        var result = await _service.GetCacheSizeAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(300L, result.Value);
    }
}