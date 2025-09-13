#!/usr/bin/env pwsh

# Static File Proxy Concurrency Test Script
# Tests if multiple simultaneous requests to the same static file are properly handled

$ErrorActionPreference = "Stop"

$PROXY_URL = "http://localhost:5150"
$TEST_STATIC_PATH = "/static/image/png"
$TEST_URL = "$PROXY_URL$TEST_STATIC_PATH"
$CONCURRENT_REQUESTS = 10

Write-Host "=== Static File Proxy Concurrency Test ===" -ForegroundColor Cyan
Write-Host "Testing URL: $TEST_URL"
Write-Host "Concurrent requests: $CONCURRENT_REQUESTS"
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

# Clear static cache
Write-Host "[CLEAN] Clearing static cache..."
$staticCacheDir = "./static-cache"
if (Test-Path $staticCacheDir) {
    Remove-Item $staticCacheDir -Recurse -Force
    Write-Host "Static cache cleared" -ForegroundColor Green
} else {
    Write-Host "Static cache already clean" -ForegroundColor Green
}
Write-Host ""

# Function to make a timed request
function Invoke-TimedStaticRequest {
    param([int]$RequestId)
    
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-WebRequest -Uri $TEST_URL -Method Get -ErrorAction Stop
        $stopwatch.Stop()
        
        return [PSCustomObject]@{
            RequestId = $RequestId
            Success = $true
            StatusCode = $response.StatusCode
            ResponseSize = $response.RawContentLength
            ContentType = $response.Headers['Content-Type'] -join ', '
            TimeSeconds = $stopwatch.Elapsed.TotalSeconds
            Content = $response.Content
        }
    } catch {
        $stopwatch.Stop()
        return [PSCustomObject]@{
            RequestId = $RequestId
            Success = $false
            StatusCode = "ERROR"
            Error = $_.Exception.Message
            TimeSeconds = $stopwatch.Elapsed.TotalSeconds
        }
    }
}

# Launch multiple concurrent requests
Write-Host "Launching $CONCURRENT_REQUESTS simultaneous requests..."
$jobs = @()

for ($i = 1; $i -le $CONCURRENT_REQUESTS; $i++) {
    $job = Start-Job -ScriptBlock {
        param($RequestId, $TestUrl)
        
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $response = Invoke-WebRequest -Uri $TestUrl -Method Get -ErrorAction Stop
            $stopwatch.Stop()
            
            [PSCustomObject]@{
                RequestId = $RequestId
                Success = $true
                StatusCode = $response.StatusCode
                ResponseSize = $response.RawContentLength
                ContentType = $response.Headers['Content-Type'] -join ', '
                TimeSeconds = $stopwatch.Elapsed.TotalSeconds
                ContentLength = $response.Content.Length
            }
        } catch {
            $stopwatch.Stop()
            [PSCustomObject]@{
                RequestId = $RequestId
                Success = $false
                StatusCode = "ERROR"
                Error = $_.Exception.Message
                TimeSeconds = $stopwatch.Elapsed.TotalSeconds
            }
        }
    } -ArgumentList $i, $TEST_URL
    
    $jobs += $job
}

Write-Host "[WAIT] Waiting for all requests to complete..."
$overallStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$results = $jobs | ForEach-Object { Receive-Job -Job $_ -Wait }
$overallStopwatch.Stop()
$jobs | Remove-Job

Write-Host "All requests completed in $($overallStopwatch.Elapsed.TotalSeconds)s" -ForegroundColor Green
Write-Host ""

# Analyze results
Write-Host "Results Analysis:" -ForegroundColor Cyan
$successfulResults = $results | Where-Object { $_.Success }
$failedResults = $results | Where-Object { -not $_.Success }

Write-Host "Successful responses: $($successfulResults.Count)/$($results.Count)"

if ($failedResults.Count -gt 0) {
    Write-Host "Failed responses: $($failedResults.Count)" -ForegroundColor Red
    foreach ($failure in $failedResults) {
        Write-Host "  Request $($failure.RequestId): $($failure.Error)" -ForegroundColor Red
    }
}

if ($successfulResults.Count -gt 1) {
    # Check if all successful responses are identical
    $firstLength = $successfulResults[0].ContentLength
    $identicalCount = ($successfulResults | Where-Object { $_.ContentLength -eq $firstLength }).Count
    Write-Host "Identical responses: $identicalCount/$($successfulResults.Count)"
    
    if ($identicalCount -eq $successfulResults.Count) {
        Write-Host "All responses identical - proper request coalescing/caching" -ForegroundColor Green
    } else {
        Write-Host "Responses differ - potential race condition issue!" -ForegroundColor Red
    }
}

# Timing analysis
if ($successfulResults.Count -gt 0) {
    Write-Host ""
    Write-Host "[TIME] Request Timing Analysis:" -ForegroundColor Yellow
    Write-Host "Request#,HTTP_Code,Time(s)"
    
    $successfulResults | Sort-Object RequestId | ForEach-Object {
        Write-Host "$($_.RequestId),$($_.StatusCode),$($_.TimeSeconds)"
    }
    
    $times = $successfulResults.TimeSeconds
    $fastest = ($times | Measure-Object -Minimum).Minimum
    $slowest = ($times | Measure-Object -Maximum).Maximum
    $average = ($times | Measure-Object -Average).Average
    
    Write-Host ""
    Write-Host "Timing Statistics:" -ForegroundColor Yellow
    Write-Host "Fastest request: ${fastest}s"
    Write-Host "Slowest request: ${slowest}s"
    Write-Host "Average time: $([math]::Round($average, 3))s"
    
    Write-Host ""
    Write-Host "Test Interpretation:" -ForegroundColor Cyan
    Write-Host "- If most requests complete quickly (~same time), request coalescing is working"
    Write-Host "- If one request takes longer and others are much faster, caching/coalescing is working correctly"
    Write-Host "- If all requests take similar long times, there may be a race condition issue"
    
    # Determine if concurrency control is working
    $fastRequests = ($times | Where-Object { $_ -lt 1.0 }).Count
    $slowRequests = ($times | Where-Object { $_ -ge 1.0 }).Count
    
    if ($fastRequests -ge ($successfulResults.Count * 0.7)) {
        Write-Host ""
        Write-Host "[VERDICT] Request coalescing appears to be working correctly!" -ForegroundColor Green
        Write-Host "Most requests completed quickly, suggesting they waited for a single download." -ForegroundColor Green
    } elseif ($slowRequests -eq $successfulResults.Count) {
        Write-Host ""
        Write-Host "[WARNING] Possible race condition - all requests took significant time" -ForegroundColor Yellow
        Write-Host "This might indicate multiple concurrent downloads occurred." -ForegroundColor Yellow
    } else {
        Write-Host ""
        Write-Host "[INFO] Mixed timing results - manual review recommended" -ForegroundColor Yellow
    }
}