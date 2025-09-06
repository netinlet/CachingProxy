# Archive

This directory contains components that were removed from the main codebase but are preserved for potential reuse in future projects.

## TeeStream Implementation

**Location:** `impl/TeeStream.cs` and `tests/TeeStreamTests.cs`

**What it does:** A duplex stream implementation that writes to two output streams simultaneously. It's essentially a "T-junction" for streams - data written to the TeeStream gets copied to both a primary and secondary stream.

**Why it was removed:** During race condition fixes (September 2025), the caching proxy architecture changed from:
- ✅ **Old:** Stream directly to client response + cache file simultaneously (using TeeStream)
- ✅ **New:** Download to temporary file → atomic rename → serve from cache

The new approach eliminated race conditions and made TeeStream unnecessary in this codebase.

**Why it's archived:** The TeeStream implementation is well-designed and thoroughly tested (21 test cases). It could be valuable for future projects that need to duplicate stream output.

## Key Features of TeeStream

- ✅ **Thread-safe:** Uses `Task.WhenAll()` for parallel async operations
- ✅ **Complete Stream implementation:** All abstract methods properly implemented
- ✅ **Error handling:** Robust disposal and exception handling
- ✅ **Performance:** Parallel writes to both streams
- ✅ **Well-tested:** Comprehensive test suite covering edge cases

## Usage Example

```csharp
using var responseStream = httpContext.Response.Body;
using var cacheFileStream = File.Create("cache.dat");
using var teeStream = new TeeStream(responseStream, cacheFileStream);

// Any data written to teeStream gets written to both streams
await originStream.CopyToAsync(teeStream);
```

## Original Use Case

The TeeStream was originally used in the caching proxy to simultaneously:
1. Stream HTTP response data to the client (for immediate response)
2. Cache the same data to disk (for future requests)

This provided optimal performance by avoiding double-buffering, but introduced race conditions when multiple clients requested the same uncached resource simultaneously.

## Reuse Potential

Consider using TeeStream for scenarios requiring:
- **Logging + Processing:** Write data to log file while processing
- **Backup + Primary:** Write to backup while serving primary
- **Monitoring + Serving:** Monitor data flow while serving clients
- **Multi-destination streaming:** Any scenario requiring data duplication

---

*Archived: September 2025*
*Original Implementation: CachingProxy v1.0*