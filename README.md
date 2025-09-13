# CachingProxy Performance Test Suite

Simple shell scripts for testing the performance and behavior of the CachingProxy using external tools.

## Prerequisites

- CachingProxy server running on `http://localhost:5150`
- `curl` command available
- `bc` calculator (optional, for enhanced statistics)
- Bash shell

## Quick Start

1. **Start the CachingProxy server:**
   ```bash
   dotnet run --project CachingProxy.Server
   ```

2. **Run the performance tests:**
   ```bash
   # Basic cache performance test
   ./perf-test.sh
   
   # Concurrency and race condition test  
   ./concurrency-test.sh
   
   # Load testing (default: 100 requests, 5 concurrent)
   ./load-test.sh
   
   # Load testing with custom parameters
   ./load-test.sh 500 10  # 500 requests, 10 concurrent
   ```

## Test Scripts

### 1. perf-test.sh - Cache Performance Test

**Purpose:** Measures the performance difference between cache misses and cache hits.

**What it does:**
- Clears the cache
- Makes a request (cache miss) and measures response time
- Makes the same request again (cache hit) and measures response time
- Compares the two times and calculates improvement

**Sample Output:**
```
=== CachingProxy Performance Test ===
🔍 Testing Cache MISS (first request)...
time_total: 0.751s

⚡ Testing Cache HIT (second request)...
time_total: 0.003s

📊 Performance Comparison:
Cache MISS time: 0.751s
Cache HIT time:  0.003s
Improvement: 99.60% faster
Speedup: 250.33x faster
✅ Cache is working - hit is faster than miss
```

### 2. concurrency-test.sh - Race Condition Test

**Purpose:** Tests the proxy's ability to handle simultaneous requests for the same uncached resource.

**What it does:**
- Clears the cache
- Launches 10 simultaneous requests for the same slow endpoint (`/delay/2`)
- Analyzes whether request coalescing is working properly
- Checks if all responses are identical (indicating proper caching)

**Expected Behavior:**
- One request should take ~2 seconds (waiting for origin)
- Other requests should complete much faster (served from cache)
- All responses should be identical

**Sample Output:**
```
=== CachingProxy Concurrency Test ===
🚀 Launching 10 simultaneous requests...
✅ All requests completed in 2.15s

📊 Results Analysis:
Successful responses: 10/10
Identical responses: 10/10
✅ All responses identical - proper request coalescing/caching

⏱️ Request Timing Analysis:
Fastest request: 2.01s
Slowest request: 2.11s
```

### 3. load-test.sh - Load Testing

**Purpose:** Tests the proxy under sustained load with mixed cache hits and misses.

**Usage:**
```bash
./load-test.sh [total_requests] [concurrent_requests]

# Examples:
./load-test.sh           # 100 requests, 5 concurrent (default)
./load-test.sh 500       # 500 requests, 5 concurrent  
./load-test.sh 1000 20   # 1000 requests, 20 concurrent
```

**What it does:**
- Cycles through different URLs from `test-urls.txt`
- Maintains controlled concurrency level
- Tracks success/failure rates
- Measures response times and throughput
- Saves detailed results to CSV file

**Sample Output:**
```
=== CachingProxy Load Test ===
Total requests: 100
Concurrent requests: 5

✅ Load test completed in 15.23s

📊 Results Analysis:
Successful requests: 98
Failed requests: 2
Success rate: 98.00%
Average throughput: 6.57 requests/second

⏱️ Timing Analysis:
Fastest request: 0.002s
Slowest request: 2.15s
Average response time: 0.34s

💾 Detailed results saved to: load_test_results_20250906_163045.csv
```

## Supporting Files

### curl-format.txt
Custom curl output format for consistent timing measurements. Provides detailed timing breakdown including:
- DNS lookup time
- Connection time  
- Transfer start time
- Total time
- Download size and speed
- HTTP response code

### test-urls.txt
Collection of test URLs with different characteristics:
- **Small JSON responses** - Fast cache testing
- **Medium responses** - Typical API responses
- **Delayed responses** - Concurrency testing (`/delay/1`, `/delay/2`, etc.)
- **Different content types** - XML, HTML, plain text
- **Large responses** - Performance testing with bigger payloads
- **Dynamic responses** - Different each time (UUID, IP)

## Interpreting Results

### Performance Test Results
- **Large difference between miss and hit**: Cache is working effectively
- **Small difference**: May indicate network is very fast, or cache overhead
- **Hit slower than miss**: Possible cache corruption or disk I/O issues

### Concurrency Test Results
- **One slow request + many fast**: Request coalescing working properly
- **All requests slow**: Race condition - multiple origin requests happening
- **Different response content**: Cache corruption or race condition

### Load Test Results
- **High success rate (>95%)**: Good reliability
- **Consistent response times**: Stable performance
- **High throughput**: Good scalability
- **Low cache hit ratio**: May need cache warming or better URL distribution

## Troubleshooting

### "Proxy is not running" Error
```bash
# Start the proxy server
cd CachingProxy.Server
dotnet run
```

### "bc: command not found" Warning
Install `bc` for enhanced statistics:
```bash
# Ubuntu/Debian
sudo apt-get install bc

# macOS
brew install bc

# The scripts will work without bc, just with less detailed calculations
```

### Permission Denied
Make scripts executable:
```bash
chmod +x *.sh
```

### High Error Rates in Load Tests
- Check proxy server logs for errors
- Reduce concurrency level
- Verify test URLs are accessible
- Check network connectivity

## Customization

### Adding New Test URLs
Edit `test-urls.txt` to add your own test endpoints:
```bash
# Add your URLs (one per line, # for comments)
https://your-api.com/endpoint
https://your-cdn.com/large-file.json
```

### Modifying Test Parameters
Edit the script variables at the top of each file:
```bash
# In concurrency-test.sh
CONCURRENT_REQUESTS=20  # Test with more concurrent requests

# In load-test.sh  
DEFAULT_REQUESTS=500    # Default total requests
DEFAULT_CONCURRENT=10   # Default concurrency
```

### Custom Reporting
The load test saves detailed results to CSV files with format:
```
request_id,http_code,size_bytes,time_seconds,url
1,200,1234,0.123,https://httpbin.org/json
2,200,5678,0.045,https://httpbin.org/uuid
```

You can analyze these with spreadsheet software or custom scripts.