#!/bin/bash

# Performance test script for CachingProxy
# Tests cache miss vs cache hit response times

set -e

PROXY_URL="http://localhost:5150"
TEST_URL="https://httpbin.org/json"
CURL_FORMAT_FILE="./curl-format.txt"

echo "=== CachingProxy Performance Test ==="
echo "Testing URL: $TEST_URL"
echo "Proxy: $PROXY_URL"
echo ""

# Check if proxy is running
if ! curl -s "$PROXY_URL/health" > /dev/null; then
    echo "❌ Error: CachingProxy is not running on $PROXY_URL"
    echo "Please start the proxy with: dotnet run --project CachingProxy.Server"
    exit 1
fi

# Clear cache to ensure clean test
echo "🧹 Clearing cache..."
if ! curl -s -X POST "$PROXY_URL/clear" > /dev/null; then
    echo "❌ Error: Failed to clear cache"
    exit 1
fi
echo "✅ Cache cleared"
echo ""

# Test 1: Cache MISS (first request)
echo "🔍 Testing Cache MISS (first request)..."
MISS_OUTPUT=$(curl -s -w "@$CURL_FORMAT_FILE" "$PROXY_URL/proxy?url=$TEST_URL" -o /dev/null)
MISS_TIME=$(echo "$MISS_OUTPUT" | grep "time_total" | awk '{print $2}' | sed 's/s$//')
echo "$MISS_OUTPUT"
echo ""

# Test 2: Cache HIT (second request)
echo "⚡ Testing Cache HIT (second request)..."
HIT_OUTPUT=$(curl -s -w "@$CURL_FORMAT_FILE" "$PROXY_URL/proxy?url=$TEST_URL" -o /dev/null)
HIT_TIME=$(echo "$HIT_OUTPUT" | grep "time_total" | awk '{print $2}' | sed 's/s$//')
echo "$HIT_OUTPUT"
echo ""

# Calculate improvement
echo "📊 Performance Comparison:"
echo "Cache MISS time: ${MISS_TIME}s"
echo "Cache HIT time:  ${HIT_TIME}s"

# Calculate percentage improvement (using bc for float math if available)
if command -v bc >/dev/null 2>&1; then
    IMPROVEMENT=$(echo "scale=2; (($MISS_TIME - $HIT_TIME) / $MISS_TIME) * 100" | bc)
    SPEEDUP=$(echo "scale=2; $MISS_TIME / $HIT_TIME" | bc)
    echo "Improvement: ${IMPROVEMENT}% faster"
    echo "Speedup: ${SPEEDUP}x faster"
else
    echo "Cache hit is faster (install 'bc' for precise calculations)"
fi

# Simple validation
if (( $(echo "$HIT_TIME < $MISS_TIME" | bc -l 2>/dev/null || echo "0") )); then
    echo "✅ Cache is working - hit is faster than miss"
else
    echo "⚠️  Warning: Cache hit not significantly faster than miss"
fi