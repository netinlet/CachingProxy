using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
public class MediaCacheServiceHeaderTests
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

    #region Header Preservation Tests

    [TestMethod]
    public async Task GetOrCacheAsync_WithETagHeader_PreservesETag()
    {
        // Arrange
        var url = new Uri("https://example.com/image-with-etag.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "image-with-etag.jpg");
        var imageData = "image-with-etag-data"u8.ToArray();

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageData)
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                response.Headers.ETag = new EntityTagHeaderValue("\"abc123\"");
                return response;
            });

        // Act
        var result = await _service.GetOrCacheAsync(url);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        
        // Verify metadata file exists and contains ETag
        var metadataPath = cacheFilePath + ".meta";
        Assert.IsTrue(File.Exists(metadataPath), "Metadata file should exist");
        
        var metadataJson = await File.ReadAllTextAsync(metadataPath);
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
        
        Assert.IsNotNull(metadata);
        Assert.IsTrue(metadata.ContainsKey("ETag"));
        Assert.AreEqual("\"abc123\"", metadata["ETag"]);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_WithCacheControlHeader_PreservesCacheControl()
    {
        // Arrange
        var url = new Uri("https://example.com/image-with-cache-control.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "image-with-cache-control.jpg");
        var imageData = "image-with-cache-control-data"u8.ToArray();

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageData)
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                response.Headers.CacheControl = new CacheControlHeaderValue 
                { 
                    MaxAge = TimeSpan.FromHours(24),
                    Public = true
                };
                return response;
            });

        // Act
        var result = await _service.GetOrCacheAsync(url);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        
        // Verify metadata file contains Cache-Control
        var metadataPath = cacheFilePath + ".meta";
        var metadataJson = await File.ReadAllTextAsync(metadataPath);
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
        
        Assert.IsNotNull(metadata);
        Assert.IsTrue(metadata.ContainsKey("Cache-Control"));
        Assert.IsTrue(metadata["Cache-Control"].Contains("max-age=86400"));
        Assert.IsTrue(metadata["Cache-Control"].Contains("public"));
    }

    [TestMethod]
    public async Task GetOrCacheAsync_WithLastModifiedHeader_PreservesLastModified()
    {
        // Arrange
        var url = new Uri("https://example.com/image-with-last-modified.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "image-with-last-modified.jpg");
        var imageData = "image-with-last-modified-data"u8.ToArray();
        var lastModified = DateTimeOffset.Parse("Wed, 21 Oct 2015 07:28:00 GMT");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageData)
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                response.Content.Headers.LastModified = lastModified;
                return response;
            });

        // Act
        var result = await _service.GetOrCacheAsync(url);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        
        // Verify metadata file contains Last-Modified
        var metadataPath = cacheFilePath + ".meta";
        var metadataJson = await File.ReadAllTextAsync(metadataPath);
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
        
        Assert.IsNotNull(metadata);
        Assert.IsTrue(metadata.ContainsKey("Last-Modified"));
        Assert.AreEqual("Wed, 21 Oct 2015 07:28:00 GMT", metadata["Last-Modified"]);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_WithContentLengthHeader_PreservesContentLength()
    {
        // Arrange
        var url = new Uri("https://example.com/image-with-content-length.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "image-with-content-length.jpg");
        var imageData = "image-with-content-length-data"u8.ToArray();

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageData)
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                response.Content.Headers.ContentLength = imageData.Length;
                return response;
            });

        // Act
        var result = await _service.GetOrCacheAsync(url);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        
        // Verify metadata file contains Content-Length
        var metadataPath = cacheFilePath + ".meta";
        var metadataJson = await File.ReadAllTextAsync(metadataPath);
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
        
        Assert.IsNotNull(metadata);
        Assert.IsTrue(metadata.ContainsKey("Content-Length"));
        Assert.AreEqual(imageData.Length.ToString(), metadata["Content-Length"]);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_WithMultipleHeaders_PreservesAllHeaders()
    {
        // Arrange
        var url = new Uri("https://example.com/image-with-all-headers.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "image-with-all-headers.jpg");
        var imageData = "image-with-all-headers-data"u8.ToArray();

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageData)
                };
                
                // Set multiple headers
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                response.Content.Headers.LastModified = DateTimeOffset.Parse("Wed, 21 Oct 2015 07:28:00 GMT");
                response.Content.Headers.ContentLength = imageData.Length;
                response.Headers.ETag = new EntityTagHeaderValue("\"multi-headers-etag\"");
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(12) };
                
                // Custom headers
                response.Headers.Add("X-Custom-Header", "custom-value");
                response.Headers.Server.Add(new ProductInfoHeaderValue("CustomServer", "1.0"));
                
                return response;
            });

        // Act
        var result = await _service.GetOrCacheAsync(url);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        
        // Verify all headers are preserved in metadata
        var metadataPath = cacheFilePath + ".meta";
        var metadataJson = await File.ReadAllTextAsync(metadataPath);
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
        
        Assert.IsNotNull(metadata);
        Assert.IsTrue(metadata.ContainsKey("Content-Type"));
        Assert.IsTrue(metadata.ContainsKey("Last-Modified"));
        Assert.IsTrue(metadata.ContainsKey("Content-Length"));
        Assert.IsTrue(metadata.ContainsKey("ETag"));
        Assert.IsTrue(metadata.ContainsKey("Cache-Control"));
        Assert.IsTrue(metadata.ContainsKey("X-Custom-Header"));
        Assert.IsTrue(metadata.ContainsKey("Server"));
        
        Assert.AreEqual("image/jpeg", metadata["Content-Type"]);
        Assert.AreEqual("\"multi-headers-etag\"", metadata["ETag"]);
        Assert.AreEqual("custom-value", metadata["X-Custom-Header"]);
        Assert.AreEqual("CustomServer/1.0", metadata["Server"]);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_CachedFileWithHeaders_RestoresHeadersFromMetadata()
    {
        // Arrange - Create pre-existing cache file with metadata
        var url = new Uri("https://example.com/cached-with-headers.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "cached-with-headers.jpg");
        var imageData = "cached-image-data"u8.ToArray();
        
        // Create cache file and metadata
        await File.WriteAllBytesAsync(cacheFilePath, imageData);
        
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "image/jpeg",
            ["ETag"] = "\"cached-etag\"",
            ["Last-Modified"] = "Thu, 22 Oct 2015 08:30:00 GMT",
            ["Cache-Control"] = "max-age=7200, public",
            ["Content-Length"] = imageData.Length.ToString()
        };
        
        var metadataPath = cacheFilePath + ".meta";
        var metadataJson = JsonSerializer.Serialize(headers);
        await File.WriteAllTextAsync(metadataPath, metadataJson);

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        // Act
        var result = await _service.GetOrCacheAsync(url);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(cacheFilePath, result.Value.FilePath);
        Assert.AreEqual("image/jpeg", result.Value.ContentType);
        Assert.AreEqual(imageData.Length, result.Value.Size);
        
        // Verify no HTTP request was made (cache hit)
        _mockHttpHandler.VerifyNoOutstandingExpectation();
        
        // Verify the cached data is correct
        var cachedData = await File.ReadAllBytesAsync(cacheFilePath);
        CollectionAssert.AreEqual(imageData, cachedData);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_WithContentTypeWithCharset_PreservesFullContentType()
    {
        // Arrange
        var url = new Uri("https://example.com/image-with-charset.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "image-with-charset.jpg");
        var imageData = "image-with-charset-data"u8.ToArray();

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        _mockHttpHandler.When(HttpMethod.Get, url.ToString())
            .Respond(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageData)
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg")
                {
                    CharSet = "utf-8"
                };
                return response;
            });

        // Act
        var result = await _service.GetOrCacheAsync(url);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        
        // Verify full content type is preserved
        var metadataPath = cacheFilePath + ".meta";
        var metadataJson = await File.ReadAllTextAsync(metadataPath);
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
        
        Assert.IsNotNull(metadata);
        Assert.IsTrue(metadata.ContainsKey("Content-Type"));
        Assert.AreEqual("image/jpeg; charset=utf-8", metadata["Content-Type"]);
    }

    [TestMethod]
    public async Task GetOrCacheAsync_WithInvalidMetadataFile_HandleGracefully()
    {
        // Arrange - Create cache file with corrupted metadata
        var url = new Uri("https://example.com/invalid-metadata.jpg");
        var cacheFilePath = Path.Combine(_tempCacheDir, "invalid-metadata.jpg");
        var imageData = "image-data"u8.ToArray();
        
        // Create cache file
        await File.WriteAllBytesAsync(cacheFilePath, imageData);
        
        // Create invalid metadata file
        var metadataPath = cacheFilePath + ".meta";
        await File.WriteAllTextAsync(metadataPath, "invalid-json-content");

        _mockUrlResolver.ResolveAsync(url, Arg.Any<CancellationToken>())
            .Returns(Result.Success(url));
        
        _mockPathProvider.GetCacheFilePath(url)
            .Returns(Result.Success(cacheFilePath));

        // Act
        var result = await _service.GetOrCacheAsync(url);

        // Assert
        Assert.IsTrue(result.IsSuccess, "Should still succeed with cached file even if metadata is invalid");
        Assert.AreEqual(cacheFilePath, result.Value.FilePath);
        Assert.AreEqual(imageData.Length, result.Value.Size);
        
        // Should have a default content type when metadata can't be read
        Assert.IsNotNull(result.Value.ContentType);
    }

    #endregion
}
