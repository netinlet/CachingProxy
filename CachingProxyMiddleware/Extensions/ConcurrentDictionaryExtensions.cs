using System.Collections.Concurrent;
using CSharpFunctionalExtensions;

namespace CachingProxyMiddleware.Extensions;

public static class ConcurrentDictionaryExtensions
{
    /// <summary>
    ///     Gets a value from the dictionary as a Maybe&lt;T&gt; instead of using TryGetValue pattern.
    ///     Returns None if the key is not found, Some(value) if it exists.
    /// </summary>
    public static Maybe<TValue> GetMaybe<TKey, TValue>(
        this ConcurrentDictionary<TKey, TValue> dictionary,
        TKey key) where TKey : notnull
    {
        return dictionary.TryGetValue(key, out var value)
            ? Maybe<TValue>.From(value)
            : Maybe<TValue>.None;
    }
}