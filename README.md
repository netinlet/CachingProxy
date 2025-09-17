# CachingProxyMiddleware

A high-performance media caching proxy middleware for ASP.NET Core built with .NET 9.0. This system intercepts HTTP requests for media files, caches them to disk using atomic file operations, and serves subsequent requests from cache with request deduplication to prevent race conditions.

## Features

- **Media-Focused Caching**: Optimized for common media file types (images, videos, audio)
- **Race Condition Prevention**: Request deduplication ensures only one download per URL occurs simultaneously
- **Atomic File Operations**: Downloads to temporary files with atomic rename to prevent cache corruption
- **Header Preservation**: Maintains HTTP headers (ETag, Last-Modified, Cache-Control, etc.) in JSON metadata files
- **Configurable Limits**: Maximum file size, concurrent downloads, and cache retention controls
- **Comprehensive Testing**: 11 test files covering caching scenarios, race conditions, and edge cases
- **Result Pattern**: Uses CSharpFunctionalExtensions Result<T> for robust error handling

## Quick Start

### Running the Application

```bash
# Build the solution
dotnet build

# Run the main application
dotnet run --project CachingProxyMiddleware

# Run tests
dotnet test
```

### Basic Usage

The service provides both API endpoints and middleware functionality:

**API Endpoint:**
```bash
curl "http://localhost:5000/proxy?url=https://example.com/image.jpg"
```

**Middleware Route:**
```bash
curl "http://localhost:5000/media?url=https://example.com/image.jpg"
```

**Cache Management:**
```bash
# Clear cache
curl -X POST http://localhost:5000/cache/clear

# Get cache size
curl http://localhost:5000/cache/size

# Health check
curl http://localhost:5000/health
```

## Project Structure

### Main Application (`CachingProxyMiddleware/`)

- **Program.cs**: ASP.NET Core startup with minimal API endpoints and middleware configuration
- **Services/MediaCacheService.cs**: Core caching logic with two-phase approach and request deduplication
- **Middleware/MediaProxyMiddleware.cs**: ASP.NET Core middleware for intercepting `/media/*` requests
- **Models/MediaCacheOptions.cs**: Configuration options with defaults
- **Models/CachedMedia.cs**: Data model for cached media information
- **Interfaces/**: Service abstractions (IMediaCacheService, IUrlResolver, IHostBasedPathProvider)
- **Extensions/**: Helper extensions for dependency injection and HTTP context
- **Validators/**: URI validation logic
- **Services/**: Supporting services (ContentTypeResolver, DefaultUrlResolver, HostBasedPathProvider)

### Test Suite (`CachingProxyMiddleware.Tests/`)

Comprehensive test coverage with 11 test files using MSTest, NSubstitute, and RichardSzalay.MockHttp:
- Media caching functionality tests
- Middleware integration tests
- Race condition and concurrency tests
- Configuration and validation tests
- Error handling and edge case tests

## Configuration

Configure the service in `appsettings.json`:

```json
{
  "MediaCache": {
    "CacheDirectory": "./media-cache",
    "HttpTimeout": "00:02:00",
    "MaxConcurrentDownloads": 10,
    "MaxFileSizeBytes": 104857600,
    "AllowedExtensions": [
      ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg",
      ".mp4", ".webm", ".mov", ".avi",
      ".mp3", ".wav", ".ogg", ".m4a"
    ]
  }
}
```

### Configuration Options

- **CacheDirectory**: Local cache storage location (default: "./media-cache")
- **HttpTimeout**: HTTP client timeout (default: 2 minutes)
- **MaxConcurrentDownloads**: Maximum concurrent downloads (default: 10)
- **MaxFileSizeBytes**: Maximum cached file size (default: 100MB)
- **AllowedExtensions**: Supported media file extensions

## Architecture

### Core Components

**MediaCacheService** (`Services/MediaCacheService.cs`)
- Main caching logic with atomic file operations
- Request deduplication using `ConcurrentDictionary<string, Task<Result<CachedMedia>>>`
- Downloads to temporary files (`.tmp`) with atomic rename
- Preserves HTTP headers in JSON metadata files (`.meta`)
- Implements `IAsyncDisposable` for proper resource cleanup
- Uses CSharpFunctionalExtensions Result<T> for error handling

**MediaProxyMiddleware** (`Middleware/MediaProxyMiddleware.cs`)
- ASP.NET Core middleware for intercepting `/media` requests
- Serves from cache first, downloads on cache miss
- Proper content-type detection and HTTP headers
- Request cancellation support

**HostBasedPathProvider** (`Services/HostBasedPathProvider.cs`)
- Generates cache file paths based on URL host and path
- Handles special character substitution for filesystem compatibility
- Truncates and hashes long filenames to avoid filesystem limits

### Caching Strategy

- Cache files named using URL host + path with special character substitution
- Long filenames truncated and hashed to avoid filesystem limits
- Metadata stored as `<cachefile>.meta` containing JSON-serialized headers
- Cache hits serve directly from disk without origin requests
- Cache misses download to temporary files with atomic rename
- Request deduplication ensures only one download per URL occurs simultaneously
- SemaphoreSlim limits maximum concurrent downloads

## Development Commands

### Building and Testing
```bash
# Build the entire solution
dotnet build

# Run all tests
dotnet test

# Run tests with detailed output
dotnet test --verbosity normal

# Run specific test class
dotnet test --filter "FullyQualifiedName~MediaCacheServiceTests"
```

### Running the Application
```bash
# Run the main application (development)
dotnet run --project CachingProxyMiddleware

# Run with specific configuration
dotnet run --project CachingProxyMiddleware --environment Development
```

### Cache Management
```bash
# Clear NuGet cache if needed
dotnet nuget locals all --clear

# Clean build artifacts
dotnet clean
```

## Dependencies

- **.NET 9.0**: Target framework
- **CSharpFunctionalExtensions 3.6.0**: Result pattern and functional programming utilities
- **Microsoft.Extensions.Options.ConfigurationExtensions 8.0.0**: Configuration binding
- **Microsoft.CodeAnalysis.Analyzers 3.3.4**: Static code analysis

### Test Dependencies
- **Microsoft.NET.Test.Sdk 17.10.0**: Test SDK
- **MSTest 3.4.3**: Test framework
- **NSubstitute 5.1.0**: Mocking framework
- **Microsoft.AspNetCore.Mvc.Testing 8.0.0**: Integration testing
- **RichardSzalay.MockHttp 7.0.0**: HTTP client mocking
- **coverlet.collector 6.0.2**: Code coverage

## Key Implementation Details

- Uses atomic file operations to prevent cache corruption during concurrent access
- Request deduplication prevents multiple downloads of the same resource
- Stream disposal handled carefully to avoid "closed stream" exceptions
- Content-Type headers include charset parameters when provided by origin
- Error handling includes cleanup of partial cache files on failures
- All logging uses structured logging with ILogger<T>
- HTTP status code validation ensures only successful responses are cached

## API Endpoints

- `GET /proxy?url=<media-url>` - Proxy and cache media via API endpoint
- `GET /media?url=<media-url>` - Proxy and cache media via middleware
- `POST /cache/clear` - Clear all cached media
- `GET /cache/size` - Get total cache size in bytes
- `GET /health` - Health check endpoint
- `GET /` - Service information and available endpoints