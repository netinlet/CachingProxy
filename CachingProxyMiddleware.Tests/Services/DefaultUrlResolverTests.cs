using CachingProxyMiddleware.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CachingProxyMiddleware.Tests.Services;

[TestClass]
public class DefaultUrlResolverTests
{
    private DefaultUrlResolver _resolver = null!;

    [TestInitialize]
    public void Setup()
    {
        _resolver = new DefaultUrlResolver();
    }

    [TestMethod]
    public async Task ResolveAsync_HttpUrl_ReturnsSuccess()
    {
        var url = new Uri("http://example.com/test.jpg");

        var result = await _resolver.ResolveAsync(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(url, result.Value);
    }

    [TestMethod]
    public async Task ResolveAsync_HttpsUrl_ReturnsSuccess()
    {
        var url = new Uri("https://example.com/test.jpg");

        var result = await _resolver.ResolveAsync(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(url, result.Value);
    }

    [TestMethod]
    public async Task ResolveAsync_FtpUrl_ReturnsFailure()
    {
        var url = new Uri("ftp://example.com/test.jpg");

        var result = await _resolver.ResolveAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("HTTP or HTTPS"));
    }

    [TestMethod]
    public async Task ResolveAsync_FileUrl_ReturnsFailure()
    {
        var url = new Uri("file:///c:/test.jpg");

        var result = await _resolver.ResolveAsync(url);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error.Contains("HTTP or HTTPS"));
    }
}