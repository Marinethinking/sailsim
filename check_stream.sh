#!/bin/bash
# Check if MediaMTX has the 'front' stream active

echo "Checking MediaMTX for 'front' stream..."
echo ""

# Try the MediaMTX API (default port 9997)
echo "=== Checking MediaMTX API (port 9997) ==="
curl -s http://localhost:9997/v3/paths/list 2>&1 || echo "API not available"
echo ""

# Try WHEP endpoint
echo "=== Checking WHEP endpoint (port 8889) ==="
curl -s -X OPTIONS http://localhost:8889/front/whep 2>&1
echo ""

# Try RTSP endpoint
echo "=== Checking RTSP endpoint (port 8554) ==="
curl -s -I rtsp://localhost:8554/front 2>&1 | head -5
echo ""

echo "If all fail, the stream is not currently published."
echo "Make sure Unity is running in Play mode and look for:"
echo "  '[WebRTC front] ✓ STREAMING - Connected to MediaMTX'"

