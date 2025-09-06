#!/bin/bash

# Concurrency test script for CachingProxy
# Tests race conditions and request coalescing

set -e

PROXY_URL="http://localhost:5150"
TEST_URL="https://httpbin.org/delay/2"  # 2-second delay gives us time to launch concurrent requests
CONCURRENT_REQUESTS=10

echo "=== CachingProxy Concurrency Test ==="
echo "Testing URL: $TEST_URL"
echo "Proxy: $PROXY_URL"
echo "Concurrent requests: $CONCURRENT_REQUESTS"
echo ""

# Check if proxy is running
if ! curl -s "$PROXY_URL/health" > /dev/null; then
    echo "❌ Error: CachingProxy is not running on $PROXY_URL"
    echo "Please start the proxy with: dotnet run --project CachingProxy.Server"
    exit 1
fi

# Clear cache to ensure clean test
echo "🧹 Clearing cache..."
curl -s -X POST "$PROXY_URL/clear" > /dev/null
echo "✅ Cache cleared"
echo ""

# Create temp directory for storing request results
TEMP_DIR=$(mktemp -d)
trap "rm -rf $TEMP_DIR" EXIT

echo "🚀 Launching $CONCURRENT_REQUESTS simultaneous requests..."
START_TIME=$(date +%s.%N)

# Launch concurrent requests in background
PIDS=()
for i in $(seq 1 $CONCURRENT_REQUESTS); do
    {
        REQUEST_START=$(date +%s.%N)
        HTTP_CODE=$(curl -s -w "%{http_code}" "$PROXY_URL/proxy?url=$TEST_URL" -o "$TEMP_DIR/response_$i.json")
        REQUEST_END=$(date +%s.%N)
        REQUEST_TIME=$(echo "$REQUEST_END - $REQUEST_START" | bc -l)
        echo "$i,$HTTP_CODE,$REQUEST_TIME" > "$TEMP_DIR/timing_$i.csv"
    } &
    PIDS+=($!)
done

echo "⏳ Waiting for all requests to complete..."

# Wait for all background processes
for pid in "${PIDS[@]}"; do
    wait $pid
done

END_TIME=$(date +%s.%N)
TOTAL_TIME=$(echo "$END_TIME - $START_TIME" | bc -l)

echo "✅ All requests completed in ${TOTAL_TIME}s"
echo ""

# Analyze results
echo "📊 Results Analysis:"

# Count successful responses
SUCCESSFUL=$(ls "$TEMP_DIR"/response_*.json 2>/dev/null | wc -l)
echo "Successful responses: $SUCCESSFUL/$CONCURRENT_REQUESTS"

# Check if all responses are identical (indicating proper caching/coalescing)
if [ $SUCCESSFUL -gt 1 ]; then
    FIRST_RESPONSE=$(ls "$TEMP_DIR"/response_*.json | head -1)
    IDENTICAL_COUNT=0
    
    for response_file in "$TEMP_DIR"/response_*.json; do
        if cmp -s "$FIRST_RESPONSE" "$response_file"; then
            ((IDENTICAL_COUNT++))
        fi
    done
    
    echo "Identical responses: $IDENTICAL_COUNT/$SUCCESSFUL"
    
    if [ $IDENTICAL_COUNT -eq $SUCCESSFUL ]; then
        echo "✅ All responses identical - proper request coalescing/caching"
    else
        echo "⚠️  Warning: Responses differ - possible race condition"
    fi
fi

# Show timing distribution
echo ""
echo "⏱️  Request Timing Analysis:"
echo "Request#,HTTP_Code,Time(s)"

# Sort timing results by request number
for i in $(seq 1 $CONCURRENT_REQUESTS); do
    if [ -f "$TEMP_DIR/timing_$i.csv" ]; then
        cat "$TEMP_DIR/timing_$i.csv"
    fi
done | sort -t, -k1 -n

# Calculate statistics
echo ""
echo "📈 Timing Statistics:"
TIMES=$(for i in $(seq 1 $CONCURRENT_REQUESTS); do
    if [ -f "$TEMP_DIR/timing_$i.csv" ]; then
        cut -d, -f3 "$TEMP_DIR/timing_$i.csv"
    fi
done | sort -n)

if [ -n "$TIMES" ]; then
    MIN_TIME=$(echo "$TIMES" | head -1)
    MAX_TIME=$(echo "$TIMES" | tail -1)
    
    echo "Fastest request: ${MIN_TIME}s"
    echo "Slowest request: ${MAX_TIME}s"
    
    # Calculate average if bc is available
    if command -v bc >/dev/null 2>&1; then
        AVG_TIME=$(echo "$TIMES" | awk '{sum+=$1} END {print sum/NR}')
        echo "Average time: ${AVG_TIME}s"
    fi
fi

# Test interpretation
echo ""
echo "🔍 Test Interpretation:"
echo "- If most requests complete quickly (~same time), request coalescing is working"
echo "- If one request takes ~2s and others are much faster, caching is working correctly"
echo "- If all requests take ~2s each, there may be a race condition issue"