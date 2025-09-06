#!/bin/bash

# Load test script for CachingProxy
# Tests sustained load with mixed cache hits and misses

set -e

PROXY_URL="http://localhost:5150"
TEST_URLS_FILE="./test-urls.txt"
DEFAULT_REQUESTS=100
DEFAULT_CONCURRENT=5

# Parse command line arguments
REQUESTS=${1:-$DEFAULT_REQUESTS}
CONCURRENT=${2:-$DEFAULT_CONCURRENT}

echo "=== CachingProxy Load Test ==="
echo "Total requests: $REQUESTS"
echo "Concurrent requests: $CONCURRENT"
echo "Proxy: $PROXY_URL"
echo ""

# Check if proxy is running
if ! curl -s "$PROXY_URL/health" > /dev/null; then
    echo "❌ Error: CachingProxy is not running on $PROXY_URL"
    echo "Please start the proxy with: dotnet run --project CachingProxy.Server"
    exit 1
fi

# Check if test URLs file exists
if [ ! -f "$TEST_URLS_FILE" ]; then
    echo "❌ Error: Test URLs file not found: $TEST_URLS_FILE"
    exit 1
fi

# Read test URLs (excluding comments and empty lines)
readarray -t URLS < <(grep -v "^#" "$TEST_URLS_FILE" | grep -v "^$")
URL_COUNT=${#URLS[@]}

if [ $URL_COUNT -eq 0 ]; then
    echo "❌ Error: No URLs found in $TEST_URLS_FILE"
    exit 1
fi

echo "📋 Loaded $URL_COUNT test URLs"

# Clear cache for consistent starting point
echo "🧹 Clearing cache..."
curl -s -X POST "$PROXY_URL/clear" > /dev/null
echo "✅ Cache cleared"
echo ""

# Create temp directory for results
TEMP_DIR=$(mktemp -d)
trap "rm -rf $TEMP_DIR" EXIT

echo "🚀 Starting load test..."
START_TIME=$(date +%s.%N)

# Initialize counters
SUCCESS_COUNT=0
ERROR_COUNT=0
ACTIVE_JOBS=0
REQUEST_NUM=0

# Function to process a single request
process_request() {
    local req_id=$1
    local url=${URLS[$((req_id % URL_COUNT))]}  # Cycle through URLs
    
    local request_start=$(date +%s.%N)
    local http_code
    local response_size
    
    # Make request and capture metrics
    if response=$(curl -s -w "%{http_code},%{size_download},%{time_total}" "$PROXY_URL/proxy?url=$url" -o /dev/null 2>/dev/null); then
        local request_end=$(date +%s.%N)
        local request_time=$(echo "$request_end - $request_start" | bc -l)
        
        # Parse curl output
        IFS=',' read -r http_code response_size curl_time <<< "$response"
        
        # Write results
        echo "$req_id,$http_code,$response_size,$request_time,$url" >> "$TEMP_DIR/results.csv"
        
        if [ "$http_code" = "200" ]; then
            echo "." >> "$TEMP_DIR/success"
        else
            echo "E" >> "$TEMP_DIR/error"
        fi
    else
        echo "$req_id,ERROR,0,0,$url" >> "$TEMP_DIR/results.csv"
        echo "E" >> "$TEMP_DIR/error"
    fi
}

# Process requests with concurrency control
for ((i=1; i<=REQUESTS; i++)); do
    # Wait if we've reached max concurrent requests
    while [ $ACTIVE_JOBS -ge $CONCURRENT ]; do
        wait -n  # Wait for any background job to finish
        ((ACTIVE_JOBS--))
    done
    
    # Start new request in background
    process_request $i &
    ((ACTIVE_JOBS++))
    
    # Progress indicator
    if (( i % 10 == 0 )); then
        printf "Processed: %d/%d requests\r" $i $REQUESTS
    fi
done

# Wait for all remaining jobs
wait

END_TIME=$(date +%s.%N)
TOTAL_TIME=$(echo "$END_TIME - $START_TIME" | bc -l)

echo ""
echo "✅ Load test completed in ${TOTAL_TIME}s"
echo ""

# Analyze results
echo "📊 Results Analysis:"

# Count successes and errors
SUCCESS_COUNT=$(wc -l < "$TEMP_DIR/success" 2>/dev/null || echo 0)
ERROR_COUNT=$(wc -l < "$TEMP_DIR/error" 2>/dev/null || echo 0)

echo "Successful requests: $SUCCESS_COUNT"
echo "Failed requests: $ERROR_COUNT"
echo "Success rate: $(echo "scale=2; $SUCCESS_COUNT * 100 / $REQUESTS" | bc -l)%"

# Calculate throughput
THROUGHPUT=$(echo "scale=2; $REQUESTS / $TOTAL_TIME" | bc -l)
echo "Average throughput: $THROUGHPUT requests/second"

# Timing analysis
if [ -f "$TEMP_DIR/results.csv" ] && [ -s "$TEMP_DIR/results.csv" ]; then
    echo ""
    echo "⏱️  Timing Analysis:"
    
    # Calculate timing statistics
    TIMES=$(awk -F, '$2==200 {print $4}' "$TEMP_DIR/results.csv" | sort -n)
    
    if [ -n "$TIMES" ]; then
        MIN_TIME=$(echo "$TIMES" | head -1)
        MAX_TIME=$(echo "$TIMES" | tail -1)
        
        echo "Fastest request: ${MIN_TIME}s"
        echo "Slowest request: ${MAX_TIME}s"
        
        if command -v bc >/dev/null 2>&1; then
            AVG_TIME=$(echo "$TIMES" | awk '{sum+=$1; count++} END {if(count>0) print sum/count; else print 0}')
            echo "Average response time: ${AVG_TIME}s"
        fi
    fi
fi

# Error analysis
if [ $ERROR_COUNT -gt 0 ]; then
    echo ""
    echo "❌ Error Analysis:"
    awk -F, '$2!="200" && $2!="ERROR" {print "HTTP " $2 ": " $5}' "$TEMP_DIR/results.csv" | sort | uniq -c
    awk -F, '$2=="ERROR" {print "Connection Error: " $5}' "$TEMP_DIR/results.csv" | wc -l | awk '{print $1 " connection errors"}'
fi

# Save detailed results
RESULTS_FILE="load_test_results_$(date +%Y%m%d_%H%M%S).csv"
if [ -f "$TEMP_DIR/results.csv" ]; then
    echo "request_id,http_code,size_bytes,time_seconds,url" > "$RESULTS_FILE"
    cat "$TEMP_DIR/results.csv" >> "$RESULTS_FILE"
    echo ""
    echo "💾 Detailed results saved to: $RESULTS_FILE"
fi

echo ""
echo "Usage: $0 [total_requests] [concurrent_requests]"
echo "Example: $0 500 10  # 500 total requests, 10 concurrent"