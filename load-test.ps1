#!/usr/bin/env pwsh

# Load test script for CachingProxy
# Tests sustained load with mixed cache hits and misses

param(
    [int]$Requests = 100,
    [int]$Concurrent = 5
)

$ErrorActionPreference = "Stop"

$PROXY_URL = "http://localhost:5150"
$TEST_URLS_FILE = "./test-urls.txt"

Write-Host "=== CachingProxy Load Test ==="
Write-Host "Total requests: $Requests"
Write-Host "Concurrent requests: $Concurrent"
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

# Check if test URLs file exists
if (-not (Test-Path $TEST_URLS_FILE)) {
    Write-Host "Error: Test URLs file not found: $TEST_URLS_FILE" -ForegroundColor Red
    exit 1
}

# Read test URLs (excluding comments and empty lines)
$URLS = Get-Content $TEST_URLS_FILE | Where-Object { $_ -notmatch "^#" -and $_ -notmatch "^$" }
$URL_COUNT = $URLS.Count

if ($URL_COUNT -eq 0) {
    Write-Host "Error: No URLs found in $TEST_URLS_FILE" -ForegroundColor Red
    exit 1
}

Write-Host "Loaded $URL_COUNT test URLs"

# Clear cache for consistent starting point
Write-Host "Clearing cache..."
try {
    $null = Invoke-RestMethod -Uri "$PROXY_URL/clear" -Method Post -ErrorAction Stop
    Write-Host "Cache cleared" -ForegroundColor Green
} catch {
    Write-Warning "Could not clear cache (endpoint might not exist)"
}
Write-Host ""

# Create temp directory for results
$TEMP_DIR = New-TemporaryFile | ForEach-Object { Remove-Item $_; New-Item -ItemType Directory -Path $_ }
$cleanup = {
    if (Test-Path $TEMP_DIR) {
        Remove-Item -Recurse -Force $TEMP_DIR
    }
}
Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action $cleanup

Write-Host "Starting load test..."
$START_TIME = Get-Date

# Initialize result files
$resultsFile = Join-Path $TEMP_DIR.FullName "results.csv"
$successFile = Join-Path $TEMP_DIR.FullName "success.txt"
$errorFile = Join-Path $TEMP_DIR.FullName "error.txt"

# Function to process a single request
function Process-Request {
    param(
        [int]$RequestId,
        [string[]]$Urls,
        [string]$ProxyUrl,
        [string]$ResultsFile,
        [string]$SuccessFile,
        [string]$ErrorFile
    )
    
    $url = $Urls[($RequestId - 1) % $Urls.Count]  # Cycle through URLs
    $request_start = Get-Date
    
    try {
        $response = Invoke-WebRequest -Uri "$ProxyUrl/proxy?url=$url" -Method Get -ErrorAction Stop
        $request_end = Get-Date
        $request_time = ($request_end - $request_start).TotalSeconds
        
        $http_code = $response.StatusCode
        $response_size = $response.RawContentLength
        
        # Write results
        "$RequestId,$http_code,$response_size,$request_time,$url" | Add-Content -Path $ResultsFile -Encoding UTF8
        
        if ($http_code -eq 200) {
            "." | Add-Content -Path $SuccessFile -NoNewline -Encoding UTF8
        } else {
            "E" | Add-Content -Path $ErrorFile -NoNewline -Encoding UTF8
        }
    } catch {
        "$RequestId,ERROR,0,0,$url" | Add-Content -Path $ResultsFile -Encoding UTF8
        "E" | Add-Content -Path $ErrorFile -NoNewline -Encoding UTF8
    }
}

# Process requests with concurrency control using jobs
$JOBS = @()
$completedJobs = 0

for ($i = 1; $i -le $Requests; $i++) {
    # Wait if we've reached max concurrent requests
    while ($JOBS.Count -ge $Concurrent) {
        $completedJob = $JOBS | Wait-Job -Any
        $JOBS = $JOBS | Where-Object { $_.Id -ne $completedJob.Id }
        $completedJob | Remove-Job
        $completedJobs++
    }
    
    # Start new request as background job
    $job = Start-Job -ScriptBlock ${function:Process-Request} -ArgumentList $i, $URLS, $PROXY_URL, $resultsFile, $successFile, $errorFile
    $JOBS += $job
    
    # Progress indicator
    if ($i % 10 -eq 0) {
        Write-Progress -Activity "Processing requests" -Status "Processed: $i/$Requests requests" -PercentComplete (($i / $Requests) * 100)
    }
}

# Wait for all remaining jobs
if ($JOBS.Count -gt 0) {
    $JOBS | Wait-Job | Out-Null
    $JOBS | Remove-Job
}

Write-Progress -Activity "Processing requests" -Completed

$END_TIME = Get-Date
$TOTAL_TIME = ($END_TIME - $START_TIME).TotalSeconds

Write-Host ""
Write-Host "Load test completed in $($TOTAL_TIME.ToString('F2'))s" -ForegroundColor Green
Write-Host ""

# Analyze results
Write-Host "Results Analysis:"

# Count successes and errors
$SUCCESS_COUNT = 0
$ERROR_COUNT = 0

if (Test-Path $successFile) {
    $successContent = Get-Content $successFile -Raw
    if ($successContent) {
        $SUCCESS_COUNT = $successContent.Length
    }
}

if (Test-Path $errorFile) {
    $errorContent = Get-Content $errorFile -Raw
    if ($errorContent) {
        $ERROR_COUNT = $errorContent.Length
    }
}

Write-Host "Successful requests: $SUCCESS_COUNT"
Write-Host "Failed requests: $ERROR_COUNT"
$successRate = if ($Requests -gt 0) { [math]::Round(($SUCCESS_COUNT * 100.0) / $Requests, 2) } else { 0 }
Write-Host "Success rate: ${successRate}%"

# Calculate throughput
$THROUGHPUT = if ($TOTAL_TIME -gt 0) { [math]::Round($Requests / $TOTAL_TIME, 2) } else { 0 }
Write-Host "Average throughput: $THROUGHPUT requests/second"

# Timing analysis
if (Test-Path $resultsFile) {
    Write-Host ""
    Write-Host "Timing Analysis:"
    
    # Read and parse results
    $results = Get-Content $resultsFile | ForEach-Object {
        $parts = $_ -split ','
        if ($parts.Length -ge 4 -and $parts[1] -eq "200") {
            try {
                [double]$parts[3]
            } catch {
                $null
            }
        }
    } | Where-Object { $_ -ne $null } | Sort-Object
    
    if ($results.Count -gt 0) {
        $MIN_TIME = $results[0]
        $MAX_TIME = $results[-1]
        $AVG_TIME = ($results | Measure-Object -Average).Average
        
        Write-Host "Fastest request: $($MIN_TIME.ToString('F3'))s"
        Write-Host "Slowest request: $($MAX_TIME.ToString('F3'))s"
        Write-Host "Average response time: $($AVG_TIME.ToString('F3'))s"
    }
}

# Error analysis
if ($ERROR_COUNT -gt 0) {
    Write-Host ""
    Write-Host "Error Analysis:"
    
    if (Test-Path $resultsFile) {
        # Group errors by type
        $errorSummary = @{}
        $connectionErrors = 0
        
        Get-Content $resultsFile | ForEach-Object {
            $parts = $_ -split ','
            if ($parts.Length -ge 2) {
                if ($parts[1] -eq "ERROR") {
                    $connectionErrors++
                } elseif ($parts[1] -ne "200") {
                    $errorType = "HTTP $($parts[1])"
                    if ($errorSummary.ContainsKey($errorType)) {
                        $errorSummary[$errorType]++
                    } else {
                        $errorSummary[$errorType] = 1
                    }
                }
            }
        }
        
        foreach ($errorType in $errorSummary.Keys) {
            Write-Host "$($errorSummary[$errorType]) $errorType errors"
        }
        
        if ($connectionErrors -gt 0) {
            Write-Host "$connectionErrors connection errors"
        }
    }
}

# Save detailed results
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$RESULTS_FILE = "load_test_results_$timestamp.csv"

if (Test-Path $resultsFile) {
    "request_id,http_code,size_bytes,time_seconds,url" | Set-Content -Path $RESULTS_FILE -Encoding UTF8
    Get-Content $resultsFile | Add-Content -Path $RESULTS_FILE -Encoding UTF8
    Write-Host ""
    Write-Host "Detailed results saved to: $RESULTS_FILE"
}

Write-Host ""
Write-Host "Usage: .\load-test.ps1 [-Requests <total_requests>] [-Concurrent <concurrent_requests>]"
Write-Host "Example: .\load-test.ps1 -Requests 500 -Concurrent 10  # 500 total requests, 10 concurrent"

# Cleanup
& $cleanup