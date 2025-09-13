using CachingProxyMiddleware.Models;
using CachingProxyMiddleware.Services;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CachingProxyMiddware.Tests.Services;

[TestClass]
public class HostBasedPathProviderTests
{
    private HostBasedPathProvider _provider = null!;
    private string _tempCacheDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempCacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var options = Options.Create(new MediaCacheOptions { CacheDirectory = _tempCacheDir });
        _provider = new HostBasedPathProvider(options);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempCacheDir))
            Directory.Delete(_tempCacheDir, true);
    }

    [TestMethod]
    public void GetHostDirectory_ValidUrl_ReturnsCorrectPath()
    {
        var url = new Uri("https://example.com/images/test.jpg");

        var result = _provider.GetHostDirectory(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Value.Contains("example_com"));
    }

    [TestMethod]
    public void GetCacheFilePath_ValidUrl_ReturnsCorrectPath()
    {
        var url = new Uri("https://example.com/images/test.jpg");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Value.Contains("example_com"), $"Expected path to contain 'example_com' but got: {result.Value}");
        Assert.IsTrue(result.Value.EndsWith("images/test.jpg"), $"Expected path to end with 'images/test.jpg' but got: {result.Value}");
    }

    [TestMethod]
    public void GetCacheFilePath_UrlWithInvalidChars_SanitizesPath()
    {
        var url = new Uri("https://example.com/images/test:file*.jpg");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Value.Contains(":"));
        Assert.IsFalse(result.Value.Contains("*"));
        Assert.IsTrue(result.Value.Contains("_"));
    }

    [TestMethod]
    public void GetHostDirectory_UrlWithPort_SanitizesHost()
    {
        var url = new Uri("https://example.com:8080/images/test.jpg");

        var result = _provider.GetHostDirectory(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Value.Contains("example_com_8080"), $"Expected path to contain 'example_com_8080' but got: {result.Value}");
    }
}