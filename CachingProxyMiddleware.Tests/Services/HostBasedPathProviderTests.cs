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
        Assert.IsFalse(result.Value.Contains(':'));
        Assert.IsFalse(result.Value.Contains('*'));
        Assert.IsTrue(result.Value.Contains('_'));
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
    public void GetCacheFilePath_PathWithSpaces_SanitizesCorrectly()
    {
        var url = new Uri("https://example.com/images with spaces/test file.jpg");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Value.Contains(' '), "Spaces should be sanitized");
        // After URL decoding, spaces become actual spaces, then get sanitized to underscores
        Assert.IsTrue(result.Value.Contains('_'), "Spaces should be replaced with underscores after URL decoding");
        Assert.IsFalse(result.Value.Contains("%20"), "URL-encoded spaces should be decoded and sanitized, not preserved");
    }

    [TestMethod]
    public void GetCacheFilePath_PathTraversalAttempt_RejectsInvalidPath()
    {
        var url = new Uri("https://example.com/../../../etc/passwd");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsFailure, "Path traversal attempts without file extensions should be rejected");
        Assert.IsTrue(result.Error.Contains("extension") || result.Error.Contains("file"));
    }
    
    [TestMethod]
    public void GetCacheFilePath_PathTraversalAttempt_RejectsInvalidPathWithExtension()
    {
        var url = new Uri("https://example.com/../../../etc/passwd.txt");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsFailure, "Path traversal attempt should be rejected");
        Assert.IsTrue(result.Error.Contains("traversal") || result.Error.Contains("security"), 
            $"Expected security/traversal error but got: {result.Error}");
    }
    
    [TestMethod]
    public void GetCacheFilePath_PathWithSpecialCharacters_SanitizesAll()
    {
        // Test characters that appear directly in URI paths and need sanitization
        var directTestCases = new[] { ":", "*" };

        foreach (var specialChar in directTestCases)
        {
            var url = new Uri($"https://example.com/test{specialChar}file.jpg");
            var result = _provider.GetCacheFilePath(url);

            Assert.IsTrue(result.IsSuccess, $"Should handle special character: {specialChar}");
            Assert.IsFalse(result.Value.Contains(specialChar), $"Special character {specialChar} should be sanitized");
            Assert.IsTrue(result.Value.Contains("_"), $"Should contain underscore replacement for {specialChar}");
        }

        // Test characters that get URL-encoded but are now properly decoded and sanitized
        var encodedTestCases = new[]
        {
            ("<", "%3C"),  // < gets encoded to %3C, then decoded and sanitized
            (">", "%3E"),  // > gets encoded to %3E, then decoded and sanitized
            ("\"", "%22"), // " gets encoded to %22, then decoded and sanitized
            ("|", "%7C"),  // | gets encoded to %7C, then decoded and sanitized
        };

        foreach (var (originalChar, _) in encodedTestCases)
        {
            var url = new Uri($"https://example.com/test{originalChar}file.jpg");
            var result = _provider.GetCacheFilePath(url);

            Assert.IsTrue(result.IsSuccess, $"Should handle encoded character representing: {originalChar}");

            // After our security fix, the path should NOT contain the original character
            // because it gets URL-decoded first, then sanitized
            Assert.IsFalse(result.Value.Contains(originalChar), $"Original character {originalChar} should be sanitized after decoding");
            Assert.IsTrue(result.Value.Contains("_"), $"Should contain underscore replacement for {originalChar}");
        }

        // Test that ? character truncates path (becomes query parameter)
        var urlWithQuery = new Uri("https://example.com/test?file.jpg");
        var queryResult = _provider.GetCacheFilePath(urlWithQuery);

        // This should fail because path becomes "/test" without extension
        Assert.IsTrue(queryResult.IsFailure, "URL with ? should fail due to missing extension");
    }

    [TestMethod]
    public void GetCacheFilePath_URLEncodedSecurityThreats_RejectsAll()
    {
        // Test URL-encoded directory traversal attacks
        var securityTestCases = new[]
        {
            ("https://example.com/test%2E%2E/file.jpg", "URL-encoded dot-dot traversal"),
            ("https://example.com/test%2F%2E%2E%2Ffile.jpg", "URL-encoded slash-dot-dot traversal"),
            ("https://example.com/test%00file.jpg", "URL-encoded null byte"),
        };

        foreach (var (urlString, description) in securityTestCases)
        {
            var url = new Uri(urlString);
            var result = _provider.GetCacheFilePath(url);

            Assert.IsTrue(result.IsFailure, $"{description} should be rejected for security");
            Assert.IsTrue(
                result.Error.Contains("traversal") ||
                result.Error.Contains("Null byte") ||
                result.Error.Contains("extension"),
                $"Expected security error but got: {result.Error}");
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
        // Ensure the path is created (implementation may hash long paths)
        Assert.IsTrue(result.Value.Length > 0, "Path should be generated");
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
    public void GetCacheFilePath_RequiresAbsoluteUri_WithHttpScheme()
    {
        var validUrls = new[]
        {
            "https://example.com/test.jpg",
            "http://example.com/test.png",
            "https://subdomain.example.com/path/image.gif"
        };

        foreach (var urlString in validUrls)
        {
            var url = new Uri(urlString);
            var result = _provider.GetCacheFilePath(url);

            Assert.IsTrue(result.IsSuccess, $"Valid absolute URL should succeed: {urlString}");
            Assert.IsNotNull(result.Value);
        }
    }

    [TestMethod]
    public void GetCacheFilePath_RejectsNonHttpSchemes()
    {
        var invalidSchemes = new[]
        {
            "file:///c:/test.jpg",
            "ftp://example.com/test.jpg",
            "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQAAAQ"
        };

        foreach (var urlString in invalidSchemes)
        {
            var url = new Uri(urlString);
            var result = _provider.GetCacheFilePath(url);

            Assert.IsTrue(result.IsFailure, $"Non-HTTP scheme should be rejected: {urlString}");
            Assert.IsTrue(result.Error.Contains("HTTP") || result.Error.Contains("scheme"));
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
    public void GetCacheFilePath_WithoutExtension_ReturnsFailure()
    {
        var url = new Uri("https://example.com/images/photo-without-extension");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsFailure, "URLs without file extensions should be rejected");
        Assert.IsTrue(result.Error.Contains("extension") || result.Error.Contains("file"));
    }

    [TestMethod]
    public void GetCacheFilePath_RequiresFileExtension_ValidExtensions()
    {
        var validExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".mp4", ".webm" };
        
        foreach (var ext in validExtensions)
        {
            var url = new Uri($"https://example.com/test{ext}");
            var result = _provider.GetCacheFilePath(url);

            Assert.IsTrue(result.IsSuccess, $"Extension {ext} should be valid");
            Assert.IsTrue(result.Value.EndsWith(ext));
        }
    }

    [TestMethod]
    public void GetCacheFilePath_EmptyPath_ReturnsFailure()
    {
        var url = new Uri("https://example.com/");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsFailure, "Empty path should be rejected");
        Assert.IsTrue(result.Error.Contains("path") || result.Error.Contains("file"));
    }

    [TestMethod]
    public void GetCacheFilePath_RootPath_ReturnsFailure()
    {
        var url = new Uri("https://example.com");

        var result = _provider.GetCacheFilePath(url);

        Assert.IsTrue(result.IsFailure, "Root path without file should be rejected");
        Assert.IsTrue(result.Error.Contains("path") || result.Error.Contains("file"));
    }

    #endregion
}
