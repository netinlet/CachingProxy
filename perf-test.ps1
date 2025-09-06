#!/usr/bin/env pwsh

# Performance test script for CachingProxy
# Tests cache miss vs cache hit response times

$ErrorActionPreference = "Stop"

$PROXY_URL = "http://localhost:5150"
$TEST_URL = "https://httpbin.org/json"
$CURL_FORMAT_FILE = "./curl-format.txt"

Write-Host "=== CachingProxy Performance Test ==="
Write-Host "Testing URL: $TEST_URL"
Write-Host "Proxy: $PROXY_URL"
Write-Host ""

# Check if proxy is running
try {
    $null = Invoke-RestMethod -Uri "$PROXY_URL/health" -Method Get -ErrorAction Stop
} catch {
    Write-Host "Error: CachingProxy is not running on $PROXY_URL" -ForegroundColor Red
    Write-Host "Please start the proxy with: dotnet run --project CachingProxy.Server"
    exit 1
}

# Clear cache to ensure clean test
Write-Host "🧹 Clearing cache..."
try {
    $null = Invoke-RestMethod -Uri "$PROXY_URL/clear" -Method Post -ErrorAction Stop
    Write-Host "Cache cleared" -ForegroundColor Green
} catch {
    Write-Warning "Could not clear cache (endpoint might not exist)"
}
Write-Host ""

# Function to make a timed request and get detailed metrics
function Invoke-TimedRequest {
    param(
        [string]$Url,
        [string]$TestName
    )
    
    Write-Host "Testing $TestName..."
    
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    
    try {
        $response = Invoke-WebRequest -Uri $Url -Method Get -ErrorAction Stop
        $stopwatch.Stop()
        
        $totalTime = $stopwatch.Elapsed.TotalSeconds
        $statusCode = $response.StatusCode
        $responseSize = $response.RawContentLength
        $contentType = $response.Headers['Content-Type'] -join ', '
        
        # Display detailed timing information (similar to curl format)
        Write-Host "          time_total:  $($totalTime.ToString('F6'))s"
        Write-Host "         size_download:  $responseSize bytes"
        Write-Host "            http_code:  $statusCode"
        if ($contentType) {
            Write-Host "         content_type:  $contentType"
        }
        
        return @{
            Success = $true
            TotalTime = $totalTime
            StatusCode = $statusCode
            ResponseSize = $responseSize
            ContentType = $contentType
        }
    } catch {
        $stopwatch.Stop()
        $totalTime = $stopwatch.Elapsed.TotalSeconds
        
        Write-Host "          time_total:  $($totalTime.ToString('F6'))s"
        Write-Host "            http_code:  ERROR"
        Write-Host "              error:  $($_.Exception.Message)"
        
        return @{
            Success = $false
            TotalTime = $totalTime
            StatusCode = "ERROR"
            Error = $_.Exception.Message
        }
    }
}

# Test 1: Cache MISS (first request)
Write-Host "Testing Cache MISS (first request)..."
$missResult = Invoke-TimedRequest -Url "$PROXY_URL/proxy?url=$TEST_URL" -TestName "Cache MISS"
Write-Host ""

# Test 2: Cache HIT (second request)
Write-Host "Testing Cache HIT (second request)..."
$hitResult = Invoke-TimedRequest -Url "$PROXY_URL/proxy?url=$TEST_URL" -TestName "Cache HIT"
Write-Host ""

# Calculate improvement
Write-Host "Performance Comparison:"
Write-Host "Cache MISS time: $($missResult.TotalTime.ToString('F6'))s"
Write-Host "Cache HIT time:  $($hitResult.TotalTime.ToString('F6'))s"

if ($missResult.Success -and $hitResult.Success) {
    if ($missResult.TotalTime -gt 0 -and $hitResult.TotalTime -gt 0) {
        $improvement = [math]::Round((($missResult.TotalTime - $hitResult.TotalTime) / $missResult.TotalTime) * 100, 2)
        $speedup = [math]::Round($missResult.TotalTime / $hitResult.TotalTime, 2)
        
        Write-Host "Improvement: ${improvement}% faster"
        Write-Host "Speedup: ${speedup}x faster"
        
        # Simple validation
        if ($hitResult.TotalTime -lt $missResult.TotalTime) {
            Write-Host "Cache is working - hit is faster than miss" -ForegroundColor Green
        } else {
            Write-Host "Warning: Cache hit not significantly faster than miss" -ForegroundColor Yellow
        }
    } else {
        Write-Host "Warning: Could not calculate improvement (zero timing values)" -ForegroundColor Yellow
    }
} else {
    Write-Host "Error: One or both requests failed, cannot compare performance" -ForegroundColor Red
    
    if (-not $missResult.Success) {
        Write-Host "Cache MISS failed: $($missResult.Error)"
    }
    if (-not $hitResult.Success) {
        Write-Host "Cache HIT failed: $($hitResult.Error)"
    }
}

# Additional information
Write-Host ""
Write-Host "Additional Information:"
if ($missResult.Success) {
    Write-Host "Response size: $($missResult.ResponseSize) bytes"
    if ($missResult.ContentType) {
        Write-Host "Content type: $($missResult.ContentType)"
    }
}

Write-Host ""
Write-Host "Tips:"
Write-Host "- Run this test multiple times to get consistent results"
Write-Host "- Ensure network conditions are stable between tests"
Write-Host "- Check that the cache directory has sufficient space"