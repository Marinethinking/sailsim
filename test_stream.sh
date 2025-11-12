#!/bin/bash
# Quick test script to check if Unity stream is available in MediaMTX

echo "Testing MediaMTX stream availability..."
echo ""

# Test RTSP
echo "1. Testing RTSP endpoint..."
if timeout 2 ffprobe -v error rtsp://localhost:8554/front >/dev/null 2>&1; then
    echo "   ✓ RTSP stream is AVAILABLE"
    echo "   View with: ffplay -rtsp_transport tcp rtsp://localhost:8554/front"
else
    echo "   ✗ RTSP stream NOT available (404 = stream not published yet)"
fi

echo ""

# Test WHEP
echo "2. Testing WHEP endpoint..."
WHEP_RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST -H "Content-Type: application/sdp" http://localhost:8889/front/whep 2>/dev/null)
if [ "$WHEP_RESPONSE" = "200" ]; then
    echo "   ✓ WHEP endpoint is AVAILABLE"
elif [ "$WHEP_RESPONSE" = "404" ]; then
    echo "   ✗ WHEP endpoint NOT available (404 = stream not published yet)"
else
    echo "   ? WHEP endpoint returned: $WHEP_RESPONSE"
fi

echo ""
echo "3. Check Unity Console for these messages:"
echo "   - [WebRTC front] ✓ Connected to MediaMTX"
echo "   - [WebRTC front] ✓ STREAMING - Connected to MediaMTX and ready to send video"
echo "   - [WebRTC front] Stream available at: RTSP: rtsp://localhost:8554/front"

