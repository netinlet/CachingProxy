#!/usr/bin/env pwsh

# Static File Proxy Performance Test Script
# Compares performance between /proxy endpoint and /static endpoint

$ErrorActionPreference = "Stop"

$PROXY_URL = "http://localhost:5150"
$TEST_BASE_URL = "https://httpbin.org"
$TEST_IMAGE_PATH = "/image/png"  # This returns a real PNG image
$STATIC_TEST_PATH = "/static$TEST_IMAGE_PATH"  # Static endpoint mirrors the original path
$PROXY_TEST_URL = "$PROXY_URL/proxy?url=$TEST_BASE_URL$TEST_IMAGE_PATH"

Write-Host "=== Static File Proxy vs Traditional Proxy Performance Comparison ===" -ForegroundColor Cyan
Write-Host "Testing Image: $TEST_BASE_URL$TEST_IMAGE_PATH"
Write-Host "Proxy URL: $PROXY_URL"
Write-Host "Static URL: $PROXY_URL$STATIC_TEST_PATH"
Write-Host ""

# Check if proxy is running
try {
    $null = Invoke-RestMethod -Uri "$PROXY_URL/health" -Method Get -ErrorAction Stop
    Write-Host "[OK] CachingProxy is running" -ForegroundColor Green
} catch {
    Write-Host "[ERROR] CachingProxy is not running on $PROXY_URL" -ForegroundColor Red
    Write-Host "Please start the proxy with: dotnet run --project CachingProxy.Server"
    exit 1
}

# Clear both caches to ensure clean test
Write-Host "[CLEAN] Clearing caches..."
try {
    $clearResult = Invoke-RestMethod -Uri "$PROXY_URL/clear" -Method Post -ErrorAction Stop
    Write-Host "[OK] Traditional cache cleared: $($clearResult.FilesDeleted) files deleted" -ForegroundColor Green
} catch {
    Write-Warning "Could not clear traditional cache (endpoint might not exist)"
}

# Clear static cache directory
try {
    $config = Invoke-RestMethod -Uri "$PROXY_URL/config" -Method Get -ErrorAction Stop
    $staticCacheDir = "./static-cache"  # Default from our config
    if (Test-Path $staticCacheDir) {
        Remove-Item $staticCacheDir -Recurse -Force
        Write-Host "[OK] Static cache cleared" -ForegroundColor Green
    } else {
        Write-Host "[OK] Static cache directory doesn't exist (clean state)" -ForegroundColor Green
    }
} catch {
    Write-Warning "Could not clear static cache directory"
}
Write-Host ""

# Function to make a timed request and get detailed metrics
function Invoke-TimedRequest {
    param(
        [string]$Url,
        [string]$TestName,
        [string]$Description = ""
    )
    
    Write-Host "Testing $TestName..." -ForegroundColor Yellow
    if ($Description) {
        Write-Host "  $Description" -ForegroundColor Gray
    }
    
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    
    try {
        $response = Invoke-WebRequest -Uri $Url -Method Get -ErrorAction Stop
        $stopwatch.Stop()
        
        $totalTime = $stopwatch.Elapsed.TotalSeconds
        $statusCode = $response.StatusCode
        $responseSize = $response.RawContentLength
        $contentType = $response.Headers['Content-Type'] -join ', '
        
        # Display detailed timing information
        Write-Host "          time_total:  $($totalTime.ToString('F6'))s"
        Write-Host "         size_download:  $responseSize bytes"
        Write-Host "            http_code:  $statusCode"
        if ($contentType) {
            Write-Host "         content_type:  $contentType"
        }
        
        # Validate it's actually an image
        if ($responseSize -gt 0 -and $contentType -match "image") {
            Write-Host "              status:  [OK] Valid image received" -ForegroundColor Green
        } elseif ($responseSize -eq 0) {
            Write-Host "              status:  [WARN] Empty response" -ForegroundColor Yellow
        } else {
            Write-Host "              status:  [WARN] Unexpected content type" -ForegroundColor Yellow
        }
        
        return [PSCustomObject]@{
            Success = $true
            TotalTime = $totalTime
            StatusCode = $statusCode
            ResponseSize = $responseSize
            ContentType = $contentType
            Url = $Url
        }
    } catch {
        $stopwatch.Stop()
        $totalTime = $stopwatch.Elapsed.TotalSeconds
        
        Write-Host "          time_total:  $($totalTime.ToString('F6'))s"
        Write-Host "            http_code:  ERROR"
        Write-Host "              error:  $($_.Exception.Message)" -ForegroundColor Red
        
        return [PSCustomObject]@{
            Success = $false
            TotalTime = $totalTime
            StatusCode = "ERROR"
            Error = $_.Exception.Message
            Url = $Url
        }
    }
}

# Function to run multiple iterations and get average
function Invoke-BenchmarkTest {
    param(
        [string]$Url,
        [string]$TestName,
        [int]$Iterations = 3,
        [string]$Description = ""
    )
    
    Write-Host "[RUN] Running $TestName benchmark ($Iterations iterations)..." -ForegroundColor Magenta
    if ($Description) {
        Write-Host "  $Description" -ForegroundColor Gray
    }
    
    $results = @()
    $successCount = 0
    
    for ($i = 1; $i -le $Iterations; $i++) {
        Write-Host "  Iteration $i/$Iterations" -ForegroundColor Cyan
        $result = Invoke-TimedRequest -Url $Url -TestName "$TestName (Run $i)"
        $results += $result
        
        if ($result.Success) {
            $successCount++
        }
        
        if ($i -lt $Iterations) {
            Start-Sleep -Seconds 1  # Brief pause between iterations
        }
        Write-Host ""
    }
    
    # Calculate statistics
    $successfulResults = $results | Where-Object { $_.Success }
    if ($successfulResults.Count -gt 0) {
        $avgTime = ($successfulResults | Measure-Object -Property TotalTime -Average).Average
        $minTime = ($successfulResults | Measure-Object -Property TotalTime -Minimum).Minimum
        $maxTime = ($successfulResults | Measure-Object -Property TotalTime -Maximum).Maximum
        
        Write-Host "[STATS] $TestName Summary:" -ForegroundColor Green
        Write-Host "  Success Rate: $successCount/$Iterations ($([math]::Round(($successCount/$Iterations)*100, 1))%)"
        Write-Host "  Average Time: $($avgTime.ToString('F6'))s"
        Write-Host "  Min Time:     $($minTime.ToString('F6'))s"
        Write-Host "  Max Time:     $($maxTime.ToString('F6'))s"
        if ($successfulResults.Count -gt 0) {
            Write-Host "  Response Size: $($successfulResults[0].ResponseSize) bytes"
        }
    } else {
        Write-Host "[STATS] $TestName Summary: All requests failed" -ForegroundColor Red
        $avgTime = $null
    }
    
    return @{
        TestName = $TestName
        Success = $successCount -gt 0
        SuccessRate = $successCount / $Iterations
        AverageTime = $avgTime
        MinTime = if ($successfulResults) { $minTime } else { $null }
        MaxTime = if ($successfulResults) { $maxTime } else { $null }
        Results = $results
    }
}

Write-Host "[START] Starting Performance Tests" -ForegroundColor Cyan
Write-Host "=" * 60
Write-Host ""

# Test 1: Traditional Proxy Cache Miss
Write-Host "[PHASE1] Cache MISS Tests" -ForegroundColor Blue
$proxyMissResult = Invoke-BenchmarkTest -Url $PROXY_TEST_URL -TestName "Traditional Proxy (MISS)" -Description "First request through /proxy endpoint"
Write-Host ""

# Update static cache configuration for proper image URL
$actualStaticUrl = "$PROXY_URL/static/image/png"  # This should work with proper configuration
$staticMissResult = Invoke-BenchmarkTest -Url $actualStaticUrl -TestName "Static File Proxy (MISS)" -Description "First request through /static endpoint"
Write-Host ""

# Test 2: Cache Hit Tests
Write-Host "[PHASE2] Cache HIT Tests" -ForegroundColor Blue
$proxyHitResult = Invoke-BenchmarkTest -Url $PROXY_TEST_URL -TestName "Traditional Proxy (HIT)" -Description "Second request through /proxy endpoint (cached)"
Write-Host ""

$staticHitResult = Invoke-BenchmarkTest -Url $actualStaticUrl -TestName "Static File Proxy (HIT)" -Description "Second request through /static endpoint (cached)"
Write-Host ""

# Final Comparison
Write-Host "[RESULTS] FINAL PERFORMANCE COMPARISON" -ForegroundColor Cyan
Write-Host "=" * 60

# Cache Miss Comparison
if ($proxyMissResult.Success -and $staticMissResult.Success) {
    Write-Host ""
    Write-Host "[MISS] Cache MISS Performance:" -ForegroundColor Yellow
    Write-Host "  Traditional Proxy: $($proxyMissResult.AverageTime.ToString('F6'))s"
    Write-Host "  Static File Proxy: $($staticMissResult.AverageTime.ToString('F6'))s"
    
    $missImprovement = [math]::Round((($proxyMissResult.AverageTime - $staticMissResult.AverageTime) / $proxyMissResult.AverageTime) * 100, 2)
    $missSpeedup = [math]::Round($proxyMissResult.AverageTime / $staticMissResult.AverageTime, 2)
    
    if ($staticMissResult.AverageTime -lt $proxyMissResult.AverageTime) {
        Write-Host "  [BETTER] Static is ${missImprovement}% faster (${missSpeedup}x speedup)" -ForegroundColor Green
    } elseif ($staticMissResult.AverageTime -gt $proxyMissResult.AverageTime) {
        $slower = [math]::Abs($missImprovement)
        Write-Host "  [SLOWER] Static is ${slower}% slower" -ForegroundColor Yellow
    } else {
        Write-Host "  [SIMILAR] Similar performance" -ForegroundColor Gray
    }
}

# Cache Hit Comparison
if ($proxyHitResult.Success -and $staticHitResult.Success) {
    Write-Host ""
    Write-Host "[HIT] Cache HIT Performance:" -ForegroundColor Yellow
    Write-Host "  Traditional Proxy: $($proxyHitResult.AverageTime.ToString('F6'))s"
    Write-Host "  Static File Proxy: $($staticHitResult.AverageTime.ToString('F6'))s"
    
    $hitImprovement = [math]::Round((($proxyHitResult.AverageTime - $staticHitResult.AverageTime) / $proxyHitResult.AverageTime) * 100, 2)
    $hitSpeedup = [math]::Round($proxyHitResult.AverageTime / $staticHitResult.AverageTime, 2)
    
    if ($staticHitResult.AverageTime -lt $proxyHitResult.AverageTime) {
        Write-Host "  [BETTER] Static is ${hitImprovement}% faster (${hitSpeedup}x speedup)" -ForegroundColor Green
    } elseif ($staticHitResult.AverageTime -gt $proxyHitResult.AverageTime) {
        $slower = [math]::Abs($hitImprovement)
        Write-Host "  [SLOWER] Static is ${slower}% slower" -ForegroundColor Yellow
    } else {
        Write-Host "  [SIMILAR] Similar performance" -ForegroundColor Gray
    }
}

# Overall Assessment
Write-Host ""
Write-Host "[SUMMARY] Summary & Recommendations:" -ForegroundColor Cyan

$allSuccessful = $proxyMissResult.Success -and $staticMissResult.Success -and $proxyHitResult.Success -and $staticHitResult.Success

if ($allSuccessful) {
    Write-Host "[OK] All tests completed successfully" -ForegroundColor Green
    
    $avgProxyTime = ($proxyMissResult.AverageTime + $proxyHitResult.AverageTime) / 2
    $avgStaticTime = ($staticMissResult.AverageTime + $staticHitResult.AverageTime) / 2
    
    if ($avgStaticTime -lt $avgProxyTime) {
        Write-Host "[WINNER] Static File Proxy shows better overall performance" -ForegroundColor Green
        Write-Host "   - Recommended for image serving use cases" -ForegroundColor Green
        Write-Host "   - Benefits from optimized static file serving" -ForegroundColor Green
    } else {
        Write-Host "[WINNER] Traditional Proxy shows better overall performance" -ForegroundColor Yellow
        Write-Host "   - May be better for general-purpose caching" -ForegroundColor Yellow
    }
} else {
    Write-Host "[WARN] Some tests failed - check configuration and network connectivity" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "[INFO] Notes:" -ForegroundColor Gray
Write-Host "  - Results may vary based on network conditions" -ForegroundColor Gray
Write-Host "  - Static endpoint is optimized for specific URL patterns" -ForegroundColor Gray
Write-Host "  - Traditional proxy supports any URL structure" -ForegroundColor Gray
Write-Host "  - Both approaches use atomic file operations and request deduplication" -ForegroundColor Gray

Write-Host ""
Write-Host "[URLS] Test URLs used:"
Write-Host "  Traditional: $PROXY_TEST_URL" -ForegroundColor Gray
Write-Host "  Static:      $actualStaticUrl" -ForegroundColor Gray