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

This is a caching proxy system built with ASP.NET Core and .NET 9.0 consisting of three main projects:

1. **CachingProxy.Server**: Main HTTP proxy server with full caching capabilities
2. **CachingProxyMiddleware**: Minimal middleware-only project template  
3. **ExampleServer**: Basic example/test server for development

The system intercepts HTTP requests, caches responses to disk using atomic file operations, and serves subsequent requests from cache. The system uses request deduplication to prevent race conditions when multiple clients request the same uncached resource simultaneously.

## Project Structure

- **CachingProxy.Server**: Complete HTTP proxy server with both general URL caching (`/proxy`) and static file middleware (`/static/*`)
- **CachingProxy.Server.Tests**: Comprehensive test suite with 51+ tests covering all caching scenarios
- **CachingProxyMiddleware**: Minimal project template containing only basic middleware setup and CSharpFunctionalExtensions dependency
- **ExampleServer**: Simple "Hello World" server for testing and development purposes
- **CachingProxyMiddware.Tests**: Additional test project for middleware-specific functionality

## Architecture

### Core Components

**CachingProxy** (`CachingProxy.Server/CachingProxy.cs`)
- Main caching logic with two-phase approach:
  - `ValidateAndPrepareAsync()`: Performs HEAD request to validate origin and prepare headers
  - `ServeAsync()`: Serves content from cache or fetches from origin using atomic file operations
- Race condition prevention through request deduplication using `ConcurrentDictionary<string, Task<ProxyResponse>>`
- Downloads to temporary files with atomic rename to prevent cache corruption
- Preserves HTTP headers (ETag, Last-Modified, Cache-Control, etc.) in JSON metadata files
- Handles HTTP status codes including 304 Not Modified as success for caching scenarios
- Implements `IAsyncDisposable` for proper resource cleanup

**HTTP API** (`CachingProxy.Server/Program.cs`)
- Minimal API with `/proxy` endpoint that accepts URL parameter
- Two-phase response handling to prevent "response already started" exceptions
- CORS support and comprehensive header preservation
- Additional endpoints: `/health`, `/config`, `/clear`, `/`

**StaticFileProxyService** (`CachingProxy.Server/StaticFileProxyService.cs`)
- Handles static file caching with configurable allowed extensions
- Downloads files from configurable base URL to local cache
- Request deduplication for static files using `ConcurrentDictionary<string, Task<bool>>`
- Semaphore-controlled concurrent downloads

**StaticFileProxyMiddleware** (`CachingProxy.Server/StaticFileProxyMiddleware.cs`)
- ASP.NET Core middleware for intercepting `/static/*` requests
- Serves from cache first, downloads on cache miss
- Proper content-type detection and HTTP headers

### Configuration

Configuration is managed through two options classes in appsettings.json:

**CachingProxyOptions** ("CachingProxy" section):
- `CacheDirectory`: Local cache storage location (default: "./cache")
- `MaxCacheFileSizeMB`: Maximum cached file size (default: 100MB)
- `CacheRetentionDays`: Cache retention period (default: 7 days)
- `HttpTimeout`: HTTP client timeout (default: 2 minutes)
- `MaxConcurrentDownloads`: Maximum number of concurrent downloads (default: 10)
- `InProgressRequestTimeout`: Timeout for waiting on in-progress requests (default: 5 minutes)

**StaticFileProxyOptions** ("StaticFileProxy" section):
- `BaseUrl`: Origin server for static files (default: "https://example.com")
- `StaticCacheDirectory`: Local static cache storage location (default: "./static-cache")
- `HttpTimeout`: HTTP client timeout (default: 2 minutes)
- `MaxConcurrentDownloads`: Maximum concurrent static file downloads (default: 10)
- `AllowedExtensions`: Allowed file extensions (default: [".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg"])

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
dotnet test --filter "FullyQualifiedName~CachingProxyTests"

# Run specific test method
dotnet test --filter "FullyQualifiedName~CachingProxyTests.ServeAsync_SimultaneousRequests_OnlyOneDownloadOccurs"
```

### Running the Applications
```bash
# Run the main server (development)
dotnet run --project CachingProxy.Server

# Run the middleware-only project
dotnet run --project CachingProxyMiddleware

# Run the example server (for testing)
dotnet run --project ExampleServer

# Run with specific configuration
dotnet run --project CachingProxy.Server --environment Development
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

- **CachingProxyTests.cs**: 51 comprehensive tests covering HTTP caching, status codes, headers, race conditions, and edge cases
- **StaticFileProxyServiceTests.cs**: Tests for static file proxy functionality
- **StaticFileProxyMiddlewareTests.cs**: Middleware integration tests
- **StaticFileProxyMiddlewareSimpleTests.cs**: Simple middleware behavior tests

Tests use temporary cache directories and mock HTTP handlers to avoid external dependencies. Race condition tests specifically verify request deduplication and concurrent download limits work correctly.

## Key Implementation Details

- HTTP 304 Not Modified is treated as success for caching proxy scenarios
- Stream disposal is handled carefully to avoid "closed stream" exceptions in tests
- Content-Type headers may include charset parameters from MockHttp in tests
- Redirect status codes (3xx) are treated as failures (not followed automatically)
- All logging uses structured logging with ILogger<T>
- Error handling includes cleanup of partial cache files on failures

## Archived Components

The `archive/` directory contains components that were removed from the main codebase but preserved for potential reuse:

- **TeeStream** (`archive/impl/TeeStream.cs`): A duplex stream implementation that writes to two streams simultaneously. Originally used for simultaneous client response + caching, but removed when the architecture changed to atomic file operations for race condition prevention.
- **TeeStream Tests** (`archive/tests/TeeStreamTests.cs`): 21 comprehensive tests for the TeeStream functionality.

See `archive/README.md` for detailed information about archived components and their potential reuse scenarios.