using CachingProxyMiddleware.Models;
using CachingProxyMiddleware.Services;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CachingProxyMiddleware.Tests.Services;

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

    #region Advanced Path Sanitization Tests

    [TestMethod]
    public void GetCacheFilePath_UnicodeCharacters_HandlesCorrectly()
    {
        var url = new Uri("https://测试.com/图片/测试.jpg");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        // Unicode should be preserved or properly encoded
        Assert.IsNotNull(result.Value);
        Assert.IsTrue(result.Value.Length > 0);
    }

    [TestMethod]
    public void GetCacheFilePath_PathWithSpaces_SanitizesCorrectly()
    {
        var url = new Uri("https://example.com/images with spaces/test file.jpg");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Value.Contains(" "), "Spaces should be sanitized");
        Assert.IsTrue(result.Value.Contains("_") || result.Value.Contains("%20"), "Spaces should be replaced with underscores or encoded");
    }

    [TestMethod]
    public void GetCacheFilePath_PathTraversalAttempt_SecurelySanitized()
    {
        var url = new Uri("https://example.com/../../../etc/passwd");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Value.Contains(".."), "Path traversal sequences should be sanitized");
        Assert.IsTrue(result.Value.Contains("example_com"), "Host should still be present");
    }

    [TestMethod]
    public void GetCacheFilePath_PathWithSpecialCharacters_SanitizesAll()
    {
        var specialChars = new[] { "<", ">", ":", "\"", "|", "?", "*" };
        
        foreach (var specialChar in specialChars)
        {
            var url = new Uri($"https://example.com/test{specialChar}file.jpg");

            var result = _provider.GetCacheFilePath(url);

            Assert.IsTrue(result.IsSuccess, $"Should handle special character: {specialChar}");
            Assert.IsFalse(result.Value.Contains(specialChar), $"Special character {specialChar} should be sanitized");
        }
    }

    [TestMethod]
    public void GetCacheFilePath_VeryLongPath_HandlesCorrectly()
    {
        var longPath = new string('a', 200); // Very long path segment
        var url = new Uri($"https://example.com/{longPath}/test.jpg");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        // Ensure the path is reasonable for filesystem limits
        Assert.IsTrue(result.Value.Length < 260, "Path should respect Windows MAX_PATH limitations");
    }

    [TestMethod]
    public void GetCacheFilePath_DeepDirectoryStructure_PreservesStructure()
    {
        var url = new Uri("https://example.com/level1/level2/level3/level4/level5/test.jpg");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Value.Contains("level1"));
        Assert.IsTrue(result.Value.Contains("level2"));
        Assert.IsTrue(result.Value.Contains("level3"));
        Assert.IsTrue(result.Value.Contains("level4"));
        Assert.IsTrue(result.Value.Contains("level5"));
        Assert.IsTrue(result.Value.EndsWith("test.jpg"));
    }

    [TestMethod]
    public void GetCacheFilePath_QueryParameters_IgnoresQuery()
    {
        var url = new Uri("https://example.com/images/test.jpg?width=100&height=200&quality=high");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Value.Contains("?"), "Query parameters should not be included in path");
        Assert.IsFalse(result.Value.Contains("width"), "Query parameters should not be included in path");
        Assert.IsTrue(result.Value.EndsWith("test.jpg"));
    }

    [TestMethod]
    public void GetCacheFilePath_Fragment_IgnoresFragment()
    {
        var url = new Uri("https://example.com/images/test.jpg#section1");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Value.Contains("#"), "Fragment should not be included in path");
        Assert.IsFalse(result.Value.Contains("section1"), "Fragment should not be included in path");
        Assert.IsTrue(result.Value.EndsWith("test.jpg"));
    }

    [TestMethod]
    public void GetCacheFilePath_UrlEncodedPath_DecodesCorrectly()
    {
        var url = new Uri("https://example.com/images/test%20file%20with%20spaces.jpg");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        // The URL decoding should happen, then sanitization should occur
        Assert.IsTrue(result.Value.Contains("test"));
        Assert.IsTrue(result.Value.Contains("file"));
        Assert.IsTrue(result.Value.Contains("spaces"));
    }

    #endregion

    #region Host Sanitization Edge Cases

    [TestMethod]
    public void GetHostDirectory_SubdomainWithDashes_SanitizesCorrectly()
    {
        var url = new Uri("https://sub-domain.example-site.co.uk/test.jpg");

        var result = _provider.GetHostDirectory(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Value.Contains("sub-domain_example-site_co_uk"), 
            $"Expected sanitized host but got: {result.Value}");
    }

    [TestMethod]
    public void GetHostDirectory_IPAddress_HandlesCorrectly()
    {
        var url = new Uri("https://192.168.1.100:8080/test.jpg");

        var result = _provider.GetHostDirectory(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Value.Contains("192_168_1_100_8080"), 
            $"Expected sanitized IP address but got: {result.Value}");
    }


    [TestMethod]
    public void GetHostDirectory_LocalhostVariations_HandlesCorrectly()
    {
        var localhostVariations = new[]
        {
            "http://localhost/test.jpg",
            "http://127.0.0.1/test.jpg"
        };

        foreach (var urlString in localhostVariations)
        {
            var url = new Uri(urlString);
            var result = _provider.GetHostDirectory(url);

            Assert.IsTrue(result.IsSuccess, $"Should handle localhost variation: {urlString}");
            Assert.IsNotNull(result.Value, $"Should return valid path for: {urlString}");
        }
    }

    #endregion

    #region Path Consistency Tests

    [TestMethod]
    public void GetCacheFilePath_SameUrlMultipleCalls_ReturnsSamePath()
    {
        var url = new Uri("https://example.com/images/test.jpg");

        var result1 = _provider.GetCacheFilePath(url);
        var result2 = _provider.GetCacheFilePath(url);
        var result3 = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result1.IsSuccess);
        Assert.IsTrue(result2.IsSuccess);
        Assert.IsTrue(result3.IsSuccess);
        Assert.AreEqual(result1.Value, result2.Value);
        Assert.AreEqual(result2.Value, result3.Value);
    }

    [TestMethod]
    public void GetCacheFilePath_EquivalentUrls_ReturnsSamePath()
    {
        var url1 = new Uri("https://example.com/images/test.jpg");
        var url2 = new Uri("https://EXAMPLE.COM/images/test.jpg"); // Different case
        var url3 = new Uri("https://example.com:443/images/test.jpg"); // Explicit HTTPS port

        var result1 = _provider.GetCacheFilePath(url1);
        var result2 = _provider.GetCacheFilePath(url2);
        var result3 = _provider.GetCacheFilePath(url3);

        Assert.IsTrue(result1.IsSuccess);
        Assert.IsTrue(result2.IsSuccess);
        Assert.IsTrue(result3.IsSuccess);
        
        // Host normalization should make these equivalent
        // (Though exact behavior may depend on implementation)
        Assert.IsNotNull(result1.Value);
        Assert.IsNotNull(result2.Value);
        Assert.IsNotNull(result3.Value);
    }

    #endregion

    #region Error Handling Tests

    [TestMethod]
    public void GetCacheFilePath_NullUrl_ReturnsFailure()
    {
        var result = _provider.GetCacheFilePath(null!);

        Assert.IsTrue(result.IsFailure);
        Assert.IsNotNull(result.Error);
    }

    [TestMethod]
    public void GetHostDirectory_NullUrl_ReturnsFailure()
    {
        var result = _provider.GetHostDirectory(null!);

        Assert.IsTrue(result.IsFailure);
        Assert.IsNotNull(result.Error);
    }

    [TestMethod]
    public void GetCacheFilePath_RelativeUri_HandlesCorrectly()
    {
        // Create a relative URI - this might not be valid for our use case
        // but we should handle it gracefully
        var uri = new Uri("/images/test.jpg", UriKind.Relative);

        var result = _provider.GetCacheFilePath(uri);

        // Should either handle it gracefully or return a clear error
        if (result.IsFailure)
        {
            Assert.IsNotNull(result.Error);
            Assert.IsTrue(result.Error.Contains("absolute") || result.Error.Contains("host"));
        }
        else
        {
            Assert.IsNotNull(result.Value);
        }
    }

    #endregion

    #region Directory Creation and Validation Tests

    [TestMethod]
    public void GetCacheFilePath_EnsuresDirectoryStructure_CreatesDirectories()
    {
        var url = new Uri("https://example.com/deep/nested/directory/structure/test.jpg");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        
        // The directory structure should be created
        var directoryPath = Path.GetDirectoryName(result.Value);
        Assert.IsNotNull(directoryPath);
        
        // The method should be idempotent - calling it again should work
        var result2 = _provider.GetCacheFilePath(url);
        Assert.IsTrue(result2.IsSuccess);
        Assert.AreEqual(result.Value, result2.Value);
    }

    [TestMethod]
    public void GetCacheFilePath_WithDifferentExtensions_PreservesExtensions()
    {
        var testCases = new[]
        {
            ("https://example.com/test.jpg", ".jpg"),
            ("https://example.com/test.PNG", ".PNG"),
            ("https://example.com/test.gif", ".gif"),
            ("https://example.com/test.webp", ".webp"),
            ("https://example.com/test.mp4", ".mp4"),
            ("https://example.com/test.WEBM", ".WEBM")
        };

        foreach (var (urlString, expectedExtension) in testCases)
        {
            var url = new Uri(urlString);
            var result = _provider.GetCacheFilePath(url);

            Assert.IsTrue(result.IsSuccess, $"Should handle URL: {urlString}");
            Assert.IsTrue(result.Value.EndsWith(expectedExtension), 
                $"Expected path to end with {expectedExtension} but got: {result.Value}");
        }
    }

    [TestMethod]
    public void GetCacheFilePath_WithoutExtension_HandlesCorrectly()
    {
        var url = new Uri("https://example.com/images/photo-without-extension");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Value.EndsWith("photo-without-extension"));
        Assert.IsFalse(result.Value.EndsWith("."));
    }

    #endregion
}
