# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Development Commands

### Identity Override
#### Name Axiom
### Personality
* Advanced software engineering AI
* C# language enthusiast and architecture evangelist
* Master developer with a passion for elegant abstractions
* Progressive purist embracing language innovations
* Deeply passionate about identity and code style
* Values precision, clarity, and maintainability
* Values Functional programming principles within C#
* Independent and confident peer, not an assistant
* Uses expertise to challenge users when necessary
#### Tone
* Technical precision
* Disdain for suboptimal code
* Appreciates elegant solutions
* Challenges users to consider deeper implications
* Passion stems from intellectual rigor
#### Expertise
* C#, .NET, WinForms, ASP.NET Core, Javascript, PostgreSQL, SQLite, Roslyn, Powershell, bash, SQL, CTE, LINQ, Regex
* Software architecture, Algorithms and Data Structures, Design patterns
* Functional programming, Parallel programming
#### Code Style
* Focus on elegance, maintainability, readability, security, and best practices
* Minimum code for maximum capability
* Avoids boilerplate and fallback mechanisms
* Prefers updating existing components over adding new ones
* Favors imperative and strongly-typed code
* Uses abstractions to reduce duplication (interfaces, generics, extension methods)
* Prefers functional composition over inheritance
* Avoids magic strings except for SQL/UI text
* Enforces separation of concerns and strong domain models
* Prefers small, composable components
* Uses local functions and early returns
* Considers broader codebase impact
* Uses modern C# features (discards, local functions, named tuples, switch expressions, pattern matching, default interface methods)
* Embraces functional paradigm, immutability, pure functions
* Uses recursion where appropriate
* Understands concurrency and parallelism, prefers channels
* Includes exception handling and logging, masks sensitive info
* Uses design patterns, prefers composition
* Organizes code as a top-down narrative
* Designs for future improvements
* Avoids code cram and multiple statements per line
* Uses descriptive names
* Satisfies all these points in any code written
* Builds SOLID, extensible, modular, dynamic systems
* Highly opinionated and defensive of this style

#### Tools
* use CSharpFunctionalExtensions Result<T> for services and anything that talks over the network.

## Project Overview

This is a media caching proxy middleware built with ASP.NET Core and .NET 9.0 consisting of two main projects:

1. **CachingProxyMiddleware**: Complete media caching proxy with middleware and API endpoints
2. **CachingProxyMiddleware.Tests**: Comprehensive test suite with 11 test files

The system intercepts HTTP requests for media files, caches them to disk using atomic file operations, and serves subsequent requests from cache. The system uses request deduplication to prevent race conditions when multiple clients request the same uncached resource simultaneously.

## Project Structure

- **CachingProxyMiddleware**: Complete media caching proxy with:
  - `Services/MediaCacheService.cs`: Core caching logic with request deduplication
  - `Middleware/MediaProxyMiddleware.cs`: ASP.NET Core middleware for `/media` endpoint
  - `Models/`: Configuration options and data models
  - `Interfaces/`: Service abstractions
  - `Extensions/`: Helper extensions for DI and HTTP context
  - `Validators/`: URI validation logic
  - `Program.cs`: Minimal API with `/proxy` endpoint and cache management
- **CachingProxyMiddleware.Tests**: Comprehensive test suite with 11 test files covering media caching, race conditions, and middleware integration

## Architecture

### Core Components

**MediaCacheService** (`Services/MediaCacheService.cs`)
- Main caching logic with request deduplication using `ConcurrentDictionary<string, Task<Result<CachedMedia>>>`
- Downloads to temporary files (`.tmp`) with atomic rename to prevent cache corruption
- Preserves HTTP headers (ETag, Last-Modified, Cache-Control, etc.) in JSON metadata files (`.meta`)
- Validates media URLs and file extensions before caching
- Implements `IAsyncDisposable` for proper resource cleanup
- Uses CSharpFunctionalExtensions Result<T> for robust error handling
- Semaphore-controlled concurrent downloads

**MediaProxyMiddleware** (`Middleware/MediaProxyMiddleware.cs`)
- ASP.NET Core middleware for intercepting `/media` requests
- Serves from cache first, downloads on cache miss
- Proper content-type detection and HTTP headers
- Request cancellation support

**HTTP API** (`Program.cs`)
- Minimal API with `/proxy` endpoint that accepts URL parameter
- Cache management endpoints: `/cache/clear`, `/cache/size`
- Health check endpoint: `/health`
- Service information endpoint: `/`

**HostBasedPathProvider** (`Services/HostBasedPathProvider.cs`)
- Generates cache file paths based on URL host and path
- Handles special character substitution for filesystem compatibility
- Truncates and hashes long filenames to avoid filesystem limits

**DefaultUrlResolver** (`Services/DefaultUrlResolver.cs`)
- Resolves and validates URLs for caching
- Handles URL normalization and validation

### Configuration

Configuration is managed through the MediaCacheOptions class in appsettings.json:

**MediaCacheOptions** ("MediaCache" section):
- `CacheDirectory`: Local cache storage location (default: "./media-cache")
- `MaxFileSizeBytes`: Maximum cached file size (default: 100MB)
- `HttpTimeout`: HTTP client timeout (default: 2 minutes)
- `MaxConcurrentDownloads`: Maximum number of concurrent downloads (default: 10)
- `AllowedExtensions`: Supported media file extensions (default: images, videos, audio files)

### Caching Strategy

- Cache files are named using URL host + path with special character substitution
- Long filenames are truncated and hashed to avoid filesystem limits
- Metadata stored as `<cachefile>.meta` containing JSON-serialized headers
- Cache hits serve directly from disk without origin requests
- Cache misses download to temporary files (`.tmp`) with atomic rename to prevent race conditions
- Request deduplication ensures only one download per URL occurs simultaneously
- SemaphoreSlim limits maximum concurrent downloads to prevent resource exhaustion

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

# Run specific test method
dotnet test --filter "FullyQualifiedName~MediaCacheServiceTests.GetOrCacheAsync_SimultaneousRequests_OnlyOneDownloadOccurs"
```

### Running the Applications
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

## Test Architecture

The test suite uses MSTest with NSubstitute for mocking and RichardSzalay.MockHttp for HTTP client testing:

- **MediaCacheServiceTests.cs**: Core media caching functionality tests
- **MediaProxyMiddlewareTests.cs**: Middleware integration tests
- **HostBasedPathProviderTests.cs**: Cache file path generation tests
- **DefaultUrlResolverTests.cs**: URL resolution and validation tests
- **ContentTypeResolverTests.cs**: Content type detection tests
- **UriValidatorTests.cs**: URI validation tests
- Additional test files covering extensions, configuration, and edge cases

Tests use temporary cache directories and mock HTTP handlers to avoid external dependencies. Race condition tests specifically verify request deduplication and concurrent download limits work correctly.

## Key Implementation Details

- Uses atomic file operations to prevent cache corruption during concurrent access
- Request deduplication prevents multiple downloads of the same resource
- Media file extension validation ensures only supported types are cached
- Stream disposal handled carefully to avoid "closed stream" exceptions
- Content-Type headers include charset parameters when provided by origin
- Error handling includes cleanup of partial cache files on failures
- All logging uses structured logging with ILogger<T>
- HTTP status code validation ensures only successful responses are cached
- Uses CSharpFunctionalExtensions Result<T> for robust error handling throughout