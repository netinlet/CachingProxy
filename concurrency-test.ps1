#!/usr/bin/env pwsh

# Concurrency test script for CachingProxy
# Tests race conditions and request coalescing

$ErrorActionPreference = "Stop"

$PROXY_URL = "http://localhost:5150"
$TEST_URL = "https://httpbin.org/delay/2"  # 2-second delay gives us time to launch concurrent requests
$CONCURRENT_REQUESTS = 10

Write-Host "=== CachingProxy Concurrency Test ==="
Write-Host "Testing URL: $TEST_URL"
Write-Host "Proxy: $PROXY_URL"
Write-Host "Concurrent requests: $CONCURRENT_REQUESTS"
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

# Create temp directory for storing request results
$TEMP_DIR = New-TemporaryFile | ForEach-Object { Remove-Item $_; New-Item -ItemType Directory -Path $_ }
$cleanup = {
    if (Test-Path $TEMP_DIR) {
        Remove-Item -Recurse -Force $TEMP_DIR
    }
}
Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action $cleanup

Write-Host "Launching $CONCURRENT_REQUESTS simultaneous requests..."
$START_TIME = Get-Date

# Launch concurrent requests as jobs
$JOBS = @()
for ($i = 1; $i -le $CONCURRENT_REQUESTS; $i++) {
    $job = Start-Job -ScriptBlock {
        param($ProxyUrl, $TestUrl, $TempDir, $RequestId)
        
        $REQUEST_START = Get-Date
        try {
            $response = Invoke-WebRequest -Uri "$ProxyUrl/proxy?url=$TestUrl" -Method Get -ErrorAction Stop
            $HTTP_CODE = $response.StatusCode
            $REQUEST_END = Get-Date
            $REQUEST_TIME = ($REQUEST_END - $REQUEST_START).TotalSeconds
            
            # Save response content
            $response.Content | Out-File -FilePath "$TempDir/response_$RequestId.json" -Encoding UTF8
            
            # Save timing info
            "$RequestId,$HTTP_CODE,$REQUEST_TIME" | Out-File -FilePath "$TempDir/timing_$RequestId.csv" -Encoding UTF8
        } catch {
            $REQUEST_END = Get-Date
            $REQUEST_TIME = ($REQUEST_END - $REQUEST_START).TotalSeconds
            $HTTP_CODE = if ($_.Exception.Response) { $_.Exception.Response.StatusCode } else { "ERROR" }
            
            # Save error response
            "ERROR: $($_.Exception.Message)" | Out-File -FilePath "$TempDir/response_$RequestId.json" -Encoding UTF8
            
            # Save timing info
            "$RequestId,$HTTP_CODE,$REQUEST_TIME" | Out-File -FilePath "$TempDir/timing_$RequestId.csv" -Encoding UTF8
        }
    } -ArgumentList $PROXY_URL, $TEST_URL, $TEMP_DIR.FullName, $i
    
    $JOBS += $job
}

Write-Host "⏳ Waiting for all requests to complete..."

# Wait for all jobs to complete
$JOBS | Wait-Job | Out-Null

$END_TIME = Get-Date
$TOTAL_TIME = ($END_TIME - $START_TIME).TotalSeconds

Write-Host "All requests completed in ${TOTAL_TIME}s" -ForegroundColor Green
Write-Host ""

# Clean up jobs
$JOBS | Remove-Job

# Analyze results
Write-Host "Results Analysis:"

# Count successful responses
$responseFiles = Get-ChildItem -Path $TEMP_DIR -Filter "response_*.json" -ErrorAction SilentlyContinue
$SUCCESSFUL = $responseFiles.Count
Write-Host "Successful responses: $SUCCESSFUL/$CONCURRENT_REQUESTS"

# Check if all responses are identical (indicating proper caching/coalescing)
if ($SUCCESSFUL -gt 1) {
    $FIRST_RESPONSE = $responseFiles[0]
    $IDENTICAL_COUNT = 0
    
    foreach ($responseFile in $responseFiles) {
        $firstContent = Get-Content $FIRST_RESPONSE.FullName -Raw
        $currentContent = Get-Content $responseFile.FullName -Raw
        if ($firstContent -eq $currentContent) {
            $IDENTICAL_COUNT++
        }
    }
    
    Write-Host "Identical responses: $IDENTICAL_COUNT/$SUCCESSFUL"
    
    if ($IDENTICAL_COUNT -eq $SUCCESSFUL) {
        Write-Host "All responses identical - proper request coalescing/caching" -ForegroundColor Green
    } else {
        Write-Host "Warning: Responses differ - possible race condition" -ForegroundColor Yellow
    }
}

# Show timing distribution
Write-Host ""
Write-Host "⏱️  Request Timing Analysis:"
Write-Host "Request#,HTTP_Code,Time(s)"

# Sort timing results by request number
$timingFiles = Get-ChildItem -Path $TEMP_DIR -Filter "timing_*.csv" -ErrorAction SilentlyContinue
$timingResults = @()

foreach ($file in $timingFiles) {
    $content = Get-Content $file.FullName
    if ($content) {
        $parts = $content -split ','
        $requestId = [int]$parts[0]
        $timingResults += [PSCustomObject]@{
            RequestId = $requestId
            HttpCode = $parts[1]
            Time = [double]$parts[2]
            Line = $content
        }
    }
}

$timingResults | Sort-Object RequestId | ForEach-Object { Write-Host $_.Line }

# Calculate statistics
Write-Host ""
Write-Host "Timing Statistics:"

$times = $timingResults | Where-Object { $_.HttpCode -eq "200" } | Select-Object -ExpandProperty Time | Sort-Object
if ($times.Count -gt 0) {
    $MIN_TIME = $times[0]
    $MAX_TIME = $times[-1]
    $AVG_TIME = ($times | Measure-Object -Average).Average
    
    Write-Host "Fastest request: ${MIN_TIME}s"
    Write-Host "Slowest request: ${MAX_TIME}s"
    Write-Host "Average time: $($AVG_TIME.ToString('F3'))s"
}

# Test interpretation
Write-Host ""
Write-Host "Test Interpretation:"
Write-Host "- If most requests complete quickly (~same time), request coalescing is working"
Write-Host "- If one request takes ~2s and others are much faster, caching is working correctly"
Write-Host "- If all requests take ~2s each, there may be a race condition issue"

# Cleanup
& $cleanup