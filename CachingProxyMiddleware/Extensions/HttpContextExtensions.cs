using CSharpFunctionalExtensions;

namespace CachingProxyMiddleware.Extensions;

public static class HttpContextExtensions
{
    /// <summary>
    ///     Gets a query parameter as a Maybe&lt;string&gt;, returning None if the parameter
    ///     doesn't exist or is empty/null.
    /// </summary>
    public static Maybe<string> GetQueryParameter(this HttpContext context, string key)
    {
        if (!context.Request.Query.ContainsKey(key))
            return Maybe<string>.None;

        var value = context.Request.Query[key].ToString();
        return string.IsNullOrEmpty(value)
            ? Maybe<string>.None
            : Maybe<string>.From(value);
    }

    /// <summary>
    ///     Tries to parse a query parameter as a URI, returning a Result for error handling.
    /// </summary>
    public static Result<Uri> TryParseUri(this Maybe<string> maybeUrl)
    {
        return maybeUrl
            .ToResult("URL parameter is required")
            .Bind(url => Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? Result.Success(uri)
                : Result.Failure<Uri>("Invalid URL format"));
    }
}