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
public class MediaCacheServiceTests
{
    private HttpClient _httpClient = null!;
    private MockHttpMessageHandler _mockHttpHandler = null!;
    private ILogger<MediaCacheService> _mockLogger = null!;
    private IHostBasedPathProvider _mockPathProvider = null!;
    private IUrlResolver _mockUrlResolver = null!;
    private MediaCacheService _service = null!;
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

    #region Service Dependency Tests

    [TestMethod]
    public async Task GetOrCacheAsync_AfterDisposal_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");

        await _service.DisposeAsync();
        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("disposed") || result.Error.Contains("ObjectDisposedException"));
    }

    #endregion

    #region HTTP Status Code Tests (2xx-5xx)

    [TestMethod]
    public async Task GetOrCacheAsync_200OK_ReturnsSuccess()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");
        var imageData = "test-image-data"u8.ToArray();

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.OK, "image/jpeg", new MemoryStream(imageData));

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(cacheFilePath, result.Value.FilePath);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_201Created_ReturnsSuccess()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");
        var imageData = "test-image-data"u8.ToArray();

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.Created, "image/jpeg", new MemoryStream(imageData));

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(cacheFilePath, result.Value.FilePath);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_202Accepted_ReturnsSuccess()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");
        var imageData = "test-image-data"u8.ToArray();

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.Accepted, "image/jpeg", new MemoryStream(imageData));

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(cacheFilePath, result.Value.FilePath);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_204NoContent_ReturnsSuccess()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.NoContent, "image/jpeg", "");

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsSuccess,
            "204 No Content is considered a success status code by HttpResponseMessage.IsSuccessStatusCode");
    }

    [TestMethod]
    public async Task GetOrCacheAsync_301MovedPermanently_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.MovedPermanently);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("301") || result.Error.Contains("MovedPermanently"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_302Found_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.Found);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("302") || result.Error.Contains("Found"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_304NotModified_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.NotModified, "image/jpeg", "");

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure,
            "304 Not Modified is not considered a success status code by HttpResponseMessage.IsSuccessStatusCode");
    }

    [TestMethod]
    public async Task GetOrCacheAsync_400BadRequest_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.BadRequest);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("400") || result.Error.Contains("BadRequest"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_401Unauthorized_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.Unauthorized);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("401") || result.Error.Contains("Unauthorized"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_403Forbidden_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.Forbidden);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("403") || result.Error.Contains("Forbidden"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_409Conflict_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.Conflict);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("409") || result.Error.Contains("Conflict"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_410Gone_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.Gone);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("410") || result.Error.Contains("Gone"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_429TooManyRequests_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.TooManyRequests);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("429") || result.Error.Contains("TooManyRequests"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_500InternalServerError_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.InternalServerError);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("500") || result.Error.Contains("InternalServerError"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_501NotImplemented_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.NotImplemented);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("501") || result.Error.Contains("NotImplemented"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_502BadGateway_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.BadGateway);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("502") || result.Error.Contains("BadGateway"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_503ServiceUnavailable_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.ServiceUnavailable);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("503") || result.Error.Contains("ServiceUnavailable"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_504GatewayTimeout_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.GatewayTimeout);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("504") || result.Error.Contains("GatewayTimeout"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_505HttpVersionNotSupported_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.HttpVersionNotSupported);

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("505") || result.Error.Contains("HttpVersionNotSupported"));
    }

    #endregion

    #region Multiple Status Code Scenarios

    [TestMethod]
    public async Task GetOrCacheAsync_SuccessStatusCodes_AllReturnSuccess()
    {
        var testCases = new[]
        {
            (HttpStatusCode.OK, "https://example.com/200.jpg"),
            (HttpStatusCode.Created, "https://example.com/201.jpg"),
            (HttpStatusCode.Accepted, "https://example.com/202.jpg"),
            (HttpStatusCode.NoContent, "https://example.com/204.jpg")
        };

        foreach (var (statusCode, urlString) in testCases)
        {
            var url = new Uri(urlString);
            var cacheFilePath = Path.Combine(_tempCacheDir, $"{(int)statusCode}.jpg");
            var imageData = "test-data"u8.ToArray();

            _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
                .Returns(Result.Success(url));

            _mockPathProvider.GetCacheFilePath(url)
                .Returns(Result.Success(cacheFilePath));

            if (statusCode == HttpStatusCode.NoContent)
                _mockHttpHandler.When(HttpMethod.Get, url.ToString())
                    .Respond(statusCode, "image/jpeg", "");
            else
                _mockHttpHandler.When(HttpMethod.Get, url.ToString())
                    .Respond(statusCode, "image/jpeg", new MemoryStream(imageData));

            var result = await _service.GetOrCacheAsync(url);

            Assert.IsTrue(result.IsSuccess, $"Status code {statusCode} should return success");
        }
    }

    [TestMethod]
    public async Task GetOrCacheAsync_FailureStatusCodes_AllReturnFailure()
    {
        var testCases = new[]
        {
            (HttpStatusCode.BadRequest, "https://example.com/400.jpg"),
            (HttpStatusCode.Unauthorized, "https://example.com/401.jpg"),
            (HttpStatusCode.Forbidden, "https://example.com/403.jpg"),
            (HttpStatusCode.NotFound, "https://example.com/404.jpg"),
            (HttpStatusCode.Conflict, "https://example.com/409.jpg"),
            (HttpStatusCode.Gone, "https://example.com/410.jpg"),
            (HttpStatusCode.TooManyRequests, "https://example.com/429.jpg"),
            (HttpStatusCode.InternalServerError, "https://example.com/500.jpg"),
            (HttpStatusCode.NotImplemented, "https://example.com/501.jpg"),
            (HttpStatusCode.BadGateway, "https://example.com/502.jpg"),
            (HttpStatusCode.ServiceUnavailable, "https://example.com/503.jpg"),
            (HttpStatusCode.GatewayTimeout, "https://example.com/504.jpg"),
            (HttpStatusCode.HttpVersionNotSupported, "https://example.com/505.jpg")
        };

        foreach (var (statusCode, urlString) in testCases)
        {
            var url = new Uri(urlString);
            var cacheFilePath = Path.Combine(_tempCacheDir, $"{(int)statusCode}.jpg");

            _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
                .Returns(Result.Success(url));

            _mockPathProvider.GetCacheFilePath(url)
                .Returns(Result.Success(cacheFilePath));

            _mockHttpHandler.When(HttpMethod.Get, url.ToString())
                .Respond(statusCode);

            var result = await _service.GetOrCacheAsync(url);

            Assert.IsTrue(result.IsFailure, $"Status code {statusCode} should return failure");
            Assert.IsNotNull(result.Error, $"Status code {statusCode} should have error message");
        }
    }

    [TestMethod]
    public async Task GetOrCacheAsync_RedirectStatusCodes_ReturnFailure()
    {
        var testCases = new[]
        {
            (HttpStatusCode.MovedPermanently, "https://example.com/301.jpg"),
            (HttpStatusCode.Found, "https://example.com/302.jpg"),
            (HttpStatusCode.SeeOther, "https://example.com/303.jpg"),
            (HttpStatusCode.NotModified, "https://example.com/304.jpg"),
            (HttpStatusCode.TemporaryRedirect, "https://example.com/307.jpg"),
            (HttpStatusCode.PermanentRedirect, "https://example.com/308.jpg")
        };

        foreach (var (statusCode, urlString) in testCases)
        {
            var url = new Uri(urlString);
            var cacheFilePath = Path.Combine(_tempCacheDir, $"{(int)statusCode}.jpg");

            _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
                .Returns(Result.Success(url));

            _mockPathProvider.GetCacheFilePath(url)
                .Returns(Result.Success(cacheFilePath));

            _mockHttpHandler.When(HttpMethod.Get, url.ToString())
                .Respond(statusCode);

            var result = await _service.GetOrCacheAsync(url);

            Assert.IsTrue(result.IsFailure, $"Redirect status code {statusCode} should return failure");
            Assert.IsNotNull(result.Error, $"Status code {statusCode} should have error message");
        }
    }

    #endregion

    #region Comprehensive Media Type Validation Tests

    [TestMethod]
    public async Task GetOrCacheAsync_SupportedImageExtensions_AllReturnSuccess()
    {
        var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".bmp", ".ico" };

        foreach (var extension in supportedExtensions)
        {
            var url = new Uri($"https://example.com/test{extension}");
            var cacheFilePath = Path.Combine(_tempCacheDir, $"test{extension}");
            var imageData = "test-image-data"u8.ToArray();

            _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
                .Returns(Result.Success(url));

            _mockPathProvider.GetCacheFilePath(url)
                .Returns(Result.Success(cacheFilePath));

            _mockHttpHandler.When(HttpMethod.Get, url.ToString())
                .Respond(HttpStatusCode.OK, $"image/{extension[1..]}", new MemoryStream(imageData));

            var result = await _service.GetOrCacheAsync(url);

            Assert.IsTrue(result.IsSuccess, $"Extension {extension} should be supported");
        }
    }

    [TestMethod]
    public async Task GetOrCacheAsync_SupportedVideoExtensions_AllReturnSuccess()
    {
        var supportedExtensions = new[] { ".mp4", ".webm", ".mov", ".avi", ".mkv", ".flv", ".wmv" };

        foreach (var extension in supportedExtensions)
        {
            var url = new Uri($"https://example.com/test{extension}");
            var cacheFilePath = Path.Combine(_tempCacheDir, $"test{extension}");
            var videoData = "test-video-data"u8.ToArray();

            _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
                .Returns(Result.Success(url));

            _mockPathProvider.GetCacheFilePath(url)
                .Returns(Result.Success(cacheFilePath));

            _mockHttpHandler.When(HttpMethod.Get, url.ToString())
                .Respond(HttpStatusCode.OK, $"video/{extension[1..]}", new MemoryStream(videoData));

            var result = await _service.GetOrCacheAsync(url);

            Assert.IsTrue(result.IsSuccess, $"Extension {extension} should be supported");
        }
    }

    [TestMethod]
    public async Task GetOrCacheAsync_UnsupportedExtensions_AllReturnFailure()
    {
        var unsupportedExtensions = new[] { ".txt", ".pdf", ".doc", ".zip", ".exe", ".js", ".html", ".css" };

        foreach (var extension in unsupportedExtensions)
        {
            var url = new Uri($"https://example.com/test{extension}");

            var result = await _service.GetOrCacheAsync(url);

            Assert.IsTrue(result.IsFailure, $"Extension {extension} should not be supported");
            Assert.IsTrue(result.Error.Contains("supported media extension"),
                $"Error should mention unsupported extension for {extension}");
        }
    }

    [TestMethod]
    public async Task GetOrCacheAsync_ExtensionCaseSensitivity_HandledCorrectly()
    {
        var testCases = new[] { ".JPG", ".PNG", ".GIF", ".Mp4", ".WEBP" };

        foreach (var extension in testCases)
        {
            var url = new Uri($"https://example.com/test{extension}");
            var cacheFilePath = Path.Combine(_tempCacheDir, $"test{extension}");
            var mediaData = "test-media-data"u8.ToArray();

            _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
                .Returns(Result.Success(url));

            _mockPathProvider.GetCacheFilePath(url)
                .Returns(Result.Success(cacheFilePath));

            _mockHttpHandler.When(HttpMethod.Get, url.ToString())
                .Respond(HttpStatusCode.OK, "application/octet-stream", new MemoryStream(mediaData));

            var result = await _service.GetOrCacheAsync(url);

            Assert.IsTrue(result.IsSuccess, $"Extension {extension} (mixed case) should be supported");
        }
    }

    [TestMethod]
    public async Task GetOrCacheAsync_NoExtension_ReturnsFailure()
    {
        var url = new Uri("https://example.com/image-without-extension");

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("supported media extension"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_EmptyExtension_ReturnsFailure()
    {
        var url = new Uri("https://example.com/image.");

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("supported media extension"));
    }

    #endregion

    #region Exception and Error Handling Tests

    [TestMethod]
    public async Task GetOrCacheAsync_HttpRequestException_ReturnsFailure()
    {
        var url = new Uri("https://example.com/network-error.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "network-error.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Throw(new HttpRequestException("Network error"));

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("Network error") || result.Error.Contains("HttpRequestException"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_TaskCanceledException_ReturnsFailure()
    {
        var url = new Uri("https://example.com/timeout.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "timeout.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Throw(new TaskCanceledException("Request timeout"));

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("timeout") || result.Error.Contains("TaskCanceledException"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_UrlResolverFailure_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Uri>("URL resolution failed"));

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual("URL resolution failed", result.Error);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_PathProviderFailure_ReturnsFailure()
    {
        var url = new Uri("https://example.com/test.jpg");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Failure<string>("Path generation failed"));

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual("Path generation failed", result.Error);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_LargeFileHandling_Success()
    {
        var url = new Uri("https://example.com/large-image.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "large-image.jpg");
        var largeImageData = new byte[1024 * 1024]; // 1MB
        Array.Fill(largeImageData, (byte)'A');

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));

        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(HttpStatusCode.OK, "image/jpeg", new MemoryStream(largeImageData));

        var result = await _service.GetOrCacheAsync(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(largeImageData.Length, result.Value.Size);
        Assert.IsTrue(File.Exists(cacheFilePath));

        var cachedData = await File.ReadAllBytesAsync(cacheFilePath);
        Assert.AreEqual(largeImageData.Length, cachedData.Length);
    }

    #endregion
}