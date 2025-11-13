using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.WebRTC;

namespace Nami
{
    [DisallowMultipleComponent]
    public class WebRtcStreamer : MonoBehaviour
    {
        [Serializable]
        public class StreamConfig
        {
            public string streamName = "front";
            public Camera camera;
            public int width = 1280;
            public int height = 720;
            [Tooltip("Target frame rate for capture and encoding. Higher values = smoother but more CPU/GPU usage.")]
            public int fps = 30;
            [Tooltip("Bitrate in kbps for video encoding")]
            public int bitrateKbps = 4000;
            [Tooltip("Flip video vertically (useful if camera is upside down)")]
            public bool flipVertical = false;
        }

        [Header("WebRTC Settings")]
        [Tooltip("Base signaling server URL (e.g., http://host:port)")]
        public string baseUrl = "http://localhost:8889";
        [Tooltip("Use WHIP protocol (WebRTC-HTTP Ingestion Protocol) for publishing")]
        public bool useWhip = true;
        [Tooltip("Use custom JSON WHIP server (POST base/whip with { sdp, camera_id })")]
        public bool useJsonWhipServer = false;
        [Tooltip("Accept self-signed certificates when using HTTPS (dev only)")]
        public bool acceptSelfSignedCertificates = true;
        [Header("Reconnect")]
        [Tooltip("Automatically reconnect when disconnected or signaling fails")]
        public bool autoReconnect = true;
        [Tooltip("Initial reconnect delay (seconds)")]
        public float reconnectInitialDelaySec = 2f;
        [Tooltip("Maximum reconnect delay (seconds)")]
        public float reconnectMaxDelaySec = 5f;

        public List<StreamConfig> streams = new List<StreamConfig>();

        private readonly List<RenderTexture> _renderTextures = new List<RenderTexture>();
        private readonly List<RenderTexture> _resolveLdrTextures = new List<RenderTexture>();
        private readonly List<WebRtcStream> _webrtcStreams = new List<WebRtcStream>();
        private readonly List<float> _nextCaptureTime = new List<float>();
        private int _generation = 0;
        [Tooltip("Run Unity WebRTC Update() coroutine each frame (per docs). Optional on 3.x, safe to enable if in doubt.")]
        public bool runWebRtcUpdateLoop = true;
        private Coroutine _webrtcUpdateCoroutine;
        [Header("Diagnostics")]
        [Tooltip("Log outbound WebRTC payload stats periodically")]
        public bool enableStatsLogging = true;
        [Tooltip("Seconds between stats logs (use 10s for low-noise logging)")]
        public float statsLogIntervalSeconds = 10f;
        [Tooltip("Log a frame progress line every N frames (set 100 for low-noise)")]
        public int frameLogInterval = 100;
        [Header("Encoder")]
        [Tooltip("Force an IDR/keyframe on an interval to help receivers that do not send RTCP PLI/NACK")]
        public bool forcePeriodicKeyframe = false;
        [Tooltip("Seconds between forced keyframes when enabled")]
        public float keyframeIntervalSec = 2f;

        private void OnEnable()
        {
            // Unity WebRTC 3.0+ no longer requires explicit Initialize()
            if (runWebRtcUpdateLoop && _webrtcUpdateCoroutine == null)
            {
                try { _webrtcUpdateCoroutine = StartCoroutine(WebRTC.Update()); } catch { }
            }
            InitializeStreams();
        }

        private void OnDisable()
        {
            TeardownStreams();
            if (_webrtcUpdateCoroutine != null)
            {
                try { StopCoroutine(_webrtcUpdateCoroutine); } catch { }
                _webrtcUpdateCoroutine = null;
            }
        }

        private void InitializeStreams()
        {
            TeardownStreams();
            _renderTextures.Clear();
            _resolveLdrTextures.Clear();
            _webrtcStreams.Clear();
            _nextCaptureTime.Clear();
            _generation++;

            foreach (var sc in streams)
            {
                if (sc.camera == null) continue;

                var width = Mathf.Max(16, sc.width);
                var height = Mathf.Max(16, sc.height);

                // Render to HDR format so URP post-processing works correctly
                RenderTexture rt = new RenderTexture(width, height, 24, RenderTextureFormat.DefaultHDR)
                {
                    useMipMap = false,
                    antiAliasing = 1,
                    autoGenerateMips = false,
                    enableRandomWrite = false,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                rt.Create();
                sc.camera.targetTexture = rt;
                sc.camera.allowHDR = true;
                sc.camera.allowMSAA = false;
                // Let Unity handle camera rendering - don't force enabled state
                // Set viewport rect to full render texture to avoid frustum errors
                sc.camera.rect = new Rect(0, 0, 1, 1); // Full viewport (normalized coordinates)

                // Log camera configuration for debugging
                Debug.Log($"[WebRTC {sc.streamName}] Camera '{sc.camera.name}' configuration:");
                Debug.Log($"  - Enabled: {sc.camera.enabled}");
                Debug.Log($"  - CullingMask: {sc.camera.cullingMask} (layers: {LayerMaskToString(sc.camera.cullingMask)})");
                Debug.Log($"  - ClearFlags: {sc.camera.clearFlags}");
                Debug.Log($"  - BackgroundColor: {sc.camera.backgroundColor}");
                Debug.Log($"  - Depth: {sc.camera.depth}");
                Debug.Log($"  - RenderingPath: {sc.camera.renderingPath}");

                // CRITICAL FIX: ClearFlags = Nothing causes black frames!
                // When rendering to a RenderTexture, the camera MUST clear the buffer
                if (sc.camera.clearFlags == CameraClearFlags.Nothing)
                {
                    Debug.LogWarning($"[WebRTC {sc.streamName}] Camera ClearFlags is 'Nothing' - this causes BLACK FRAMES!");
                    Debug.LogWarning($"  Changing to 'SolidColor' so camera clears the RenderTexture before rendering...");
                    sc.camera.clearFlags = CameraClearFlags.SolidColor;
                    // Use a nice sky blue as default if background is black
                    if (sc.camera.backgroundColor == Color.black)
                    {
                        sc.camera.backgroundColor = new Color(0.5f, 0.7f, 1.0f, 1.0f);
                    }
                }
                else if (sc.camera.clearFlags == CameraClearFlags.Depth)
                {
                    Debug.LogWarning($"[WebRTC {sc.streamName}] Camera ClearFlags is 'Depth' - changing to 'SolidColor' for WebRTC");
                    sc.camera.clearFlags = CameraClearFlags.SolidColor;
                }

                // Create LDR resolve texture for final output with proper tonemapping
                // CRITICAL: Unity WebRTC requires B8G8R8A8_SRGB format specifically
                var ldrDesc = new RenderTextureDescriptor(width, height, GraphicsFormat.B8G8R8A8_SRGB, 0)
                {
                    msaaSamples = 1,
                    sRGB = true
                };
                var resolve = new RenderTexture(ldrDesc)
                {
                    useMipMap = false,
                    antiAliasing = 1,
                    autoGenerateMips = false,
                    enableRandomWrite = false,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                resolve.Create();
                _resolveLdrTextures.Add(resolve);

                // Configure URP camera
                var urp = sc.camera.GetUniversalAdditionalCameraData();
                if (urp != null)
                {
                    // CRITICAL: Overlay cameras don't render independently!
                    // They only add content on top of a base camera
                    if (urp.renderType == CameraRenderType.Overlay)
                    {
                        Debug.LogWarning($"[WebRTC {sc.streamName}] Camera '{sc.camera.name}' is set to OVERLAY mode!");
                        Debug.LogWarning($"  Overlay cameras don't render to targetTexture independently.");
                        Debug.LogWarning($"  Changing to BASE camera for WebRTC streaming...");
                        urp.renderType = CameraRenderType.Base;
                    }

                    // Ensure this camera doesn't render to screen (only to targetTexture)
                    // This prevents conflicts with your main camera
                    urp.renderType = CameraRenderType.Base;

                    Debug.Log($"[WebRTC {sc.streamName}] URP Camera Render Type: {urp.renderType}");
                    Debug.Log($"[WebRTC {sc.streamName}]   Render Post Processing: {urp.renderPostProcessing}");
                    Debug.Log($"[WebRTC {sc.streamName}]   Anti-aliasing: {urp.antialiasing}");

                    if (urp.renderPostProcessing)
                    {
                        Debug.Log($"[WebRTC {sc.streamName}]   ✓ Post-processing ENABLED");
                        if (urp.volumeLayerMask == 0)
                        {
                            urp.volumeLayerMask = ~0;
                            Debug.Log($"[WebRTC {sc.streamName}]   Set volumeLayerMask to Everything");
                        }
                        if (urp.volumeTrigger == null)
                        {
                            urp.volumeTrigger = sc.camera.transform;
                            Debug.Log($"[WebRTC {sc.streamName}]   Set volumeTrigger to camera transform");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[WebRTC {sc.streamName}]   ✗ Post-processing DISABLED - effects won't show in stream");
                    }
                }

                // Create WebRTC stream with LDR resolve texture
                var webrtcStream = new WebRtcStream(sc, resolve, baseUrl, useWhip, useJsonWhipServer, this);
                StartCoroutine(webrtcStream.InitializeCoroutine());
                _webrtcStreams.Add(webrtcStream);

                _renderTextures.Add(rt);
                _nextCaptureTime.Add(Time.time);
            }
        }

        private void TeardownStreams()
        {
            foreach (var stream in _webrtcStreams)
            {
                stream?.Dispose();
            }
            _webrtcStreams.Clear();

            foreach (var rt in _renderTextures)
            {
                if (rt == null) continue;
                try { rt.Release(); } catch { }
            }
            _renderTextures.Clear();

            foreach (var rt in _resolveLdrTextures)
            {
                if (rt == null) continue;
                try { rt.Release(); } catch { }
            }
            _resolveLdrTextures.Clear();

            foreach (var sc in streams)
            {
                if (sc.camera != null)
                {
                    sc.camera.targetTexture = null;
                }
            }
        }

        // Let Unity's render pipeline handle camera rendering automatically
        // Use Update() like BevRtspStreamer - LateUpdate was causing post-processing issues
        private void Update()
        {
            for (int i = 0; i < streams.Count; i++)
            {
                var sc = streams[i];
                if (sc.camera == null) continue;
                if (_webrtcStreams.Count <= i || _renderTextures.Count <= i) continue;

                var webrtcStream = _webrtcStreams[i];
                var rt = _renderTextures[i];

                var now = Time.time;
                var frameInterval = 1f / Mathf.Max(1, sc.fps);
                if (now < _nextCaptureTime[i]) continue;
                _nextCaptureTime[i] = now + frameInterval;

                if (webrtcStream == null || !webrtcStream.IsReady) continue;

                // Don't call camera.Render() - let Unity's rendering pipeline handle it
                // Manual rendering bypasses URP's post-processing pipeline

                // Blit from HDR to LDR with proper color space conversion
                // Graphics.Blit applies tonemapping automatically
                var resolve = _resolveLdrTextures.Count > i ? _resolveLdrTextures[i] : null;
                if (resolve != null)
                {
                    Graphics.Blit(rt, resolve);
                }

                webrtcStream.UpdateFrame(resolve ?? rt);
            }
        }

        private class WebRtcStream : IDisposable
        {
            private readonly StreamConfig _config;
            private readonly RenderTexture _sourceTexture;
            private readonly string _endpointUrl;
            private readonly bool _useWhip;
            private readonly bool _useJsonWhip;
            private readonly MonoBehaviour _owner;
            private readonly bool _acceptSelfSigned;

            private RTCPeerConnection _peerConnection;
            private VideoStreamTrack _videoTrack;
            private bool _isReady = false;
            private bool _disposed = false;
            private System.Collections.Generic.List<RTCIceCandidate> _gatheredCandidates = new System.Collections.Generic.List<RTCIceCandidate>();
            private bool _iceGatheringComplete = false;
            private int _frameCount = 0;
            private Coroutine _initializeCoroutine;
            private Coroutine _connectCoroutine;
            private Coroutine _reconnectCoroutine;
            private float _currentBackoffSec = 0f;
            private RTCRtpSender _sender;
            private Coroutine _statsCoroutine;
            private ulong _lastBytesSent = 0;
            private float _lastStatsWallTime = 0f;
            // WHIP resource management
            private string _whipResourceUrl = null;
            private string _whipResourceETag = null;
            private Coroutine _keyframeCoroutine;
            private static System.Reflection.MethodInfo _generateKeyFrameMethod;

            public bool IsReady => _isReady && _peerConnection != null &&
                _peerConnection.ConnectionState == RTCPeerConnectionState.Connected && !_disposed;

            public WebRtcStream(StreamConfig config, RenderTexture sourceTexture, string baseUrl, bool useWhip, bool useJsonWhip, MonoBehaviour owner)
            {
                _config = config;
                _sourceTexture = sourceTexture;
                _useWhip = useWhip;
                _useJsonWhip = useJsonWhip;
                _acceptSelfSigned = ((WebRtcStreamer)owner).acceptSelfSignedCertificates;
                // Initialize backoff from parent settings
                var parent = (WebRtcStreamer)owner;
                _currentBackoffSec = Mathf.Max(0.5f, parent.reconnectInitialDelaySec);
                if (useJsonWhip)
                {
                    // Custom JSON WHIP server expects POST base/whip with camera_id in body
                    _endpointUrl = baseUrl.TrimEnd('/') + "/whip";
                }
                else
                {
                    _endpointUrl = baseUrl.TrimEnd('/') + "/" + config.streamName + (useWhip ? "/whip" : "/whep");
                }
                _owner = owner;
            }

            public IEnumerator InitializeCoroutine()
            {
                if (_disposed) yield break;

                // Small delay to ensure Unity is ready (yield to next frame)
                yield return null;

                if (_disposed) yield break;

                try
                {
                    // Create peer connection
                    var configuration = new RTCConfiguration
                    {
                        iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } }
                    };
                    _peerConnection = new RTCPeerConnection(ref configuration);

                    // Create VideoStreamTrack directly from the resolve texture (_sourceTexture)
                    _videoTrack = new VideoStreamTrack(_sourceTexture);
                    _videoTrack.Enabled = true;
                    Debug.Log($"[WebRTC {_config.streamName}] Created VideoStreamTrack from texture ({_sourceTexture.width}x{_sourceTexture.height})");
                    Debug.Log($"[WebRTC {_config.streamName}]   Track ID: {_videoTrack.Id}, Enabled: {_videoTrack.Enabled}, ReadyState: {_videoTrack.ReadyState}");
                    Debug.Log($"[WebRTC {_config.streamName}]   Texture Created: {_sourceTexture.IsCreated()}, Format: {_sourceTexture.graphicsFormat}");

                    // Create a MediaStream to ensure msid is included in SDP and attach the track
                    var mediaStream = new MediaStream();
                    mediaStream.AddTrack(_videoTrack);
                    var sender = _peerConnection.AddTrack(_videoTrack, mediaStream);
                    _sender = sender;
                    Debug.Log($"[WebRTC {_config.streamName}] Added video track with MediaStream (msid) to peer connection");

                    // Ensure transceiver is SendOnly and prefer H.264
                    var transceiver = _peerConnection.GetTransceivers().FirstOrDefault(t => t.Sender == sender);
                    if (transceiver != null)
                    {
                        transceiver.Direction = RTCRtpTransceiverDirection.SendOnly;
                        Debug.Log($"[WebRTC {_config.streamName}] Set transceiver direction to SendOnly");

                        var capabilities = RTCRtpSender.GetCapabilities(TrackKind.Video);
                        var h264Codec = capabilities.codecs.FirstOrDefault(c => c.mimeType.Contains("H264"));
                        if (h264Codec != null)
                        {
                            var err = transceiver.SetCodecPreferences(new[] { h264Codec });
                            if (err == RTCErrorType.None)
                            {
                                Debug.Log($"[WebRTC {_config.streamName}] ✓ Using H.264 codec (default)");
                            }
                            else
                            {
                                Debug.LogWarning($"[WebRTC {_config.streamName}] Could not set H.264 preference: {err}");
                            }
                        }
                    }

                    // Set up connection state handler
                    _peerConnection.OnConnectionStateChange = (state) =>
                    {
                        if (_disposed) return;
                        Debug.Log($"[WebRTC {_config.streamName}] Connection state: {state}");
                        if (state == RTCPeerConnectionState.Connecting)
                        {
                            Debug.Log($"[WebRTC {_config.streamName}] Connecting to signaling server...");
                        }
                        if (state == RTCPeerConnectionState.Failed || state == RTCPeerConnectionState.Disconnected)
                        {
                            _isReady = false;
                            ScheduleReconnect();
                        }
                        else if (state == RTCPeerConnectionState.Connected)
                        {
                            _isReady = true;
                            Debug.Log($"[WebRTC {_config.streamName}] ✓ STREAMING - Connected and ready to send video");
                            // Reset backoff on success
                            var parent = (WebRtcStreamer)_owner;
                            _currentBackoffSec = Mathf.Max(0.5f, parent.reconnectInitialDelaySec);
                        }
                    };

                    // Handle ICE candidates - collect them for embedding in SDP (WHIP/WHEP requires non-trickle ICE)
                    _peerConnection.OnIceCandidate = (candidate) =>
                    {
                        if (candidate != null)
                        {
                            _gatheredCandidates.Add(candidate);
                            // Add candidate to peer connection so it's included in SDP when we set local description
                            _peerConnection.AddIceCandidate(candidate);
                        }
                    };

                    // Handle ICE gathering completion
                    _peerConnection.OnIceGatheringStateChange = (state) =>
                    {
                        if (state == RTCIceGatheringState.Complete)
                        {
                            _iceGatheringComplete = true;
                            Debug.Log($"WebRTC [{_config.streamName}] ICE gathering complete");
                        }
                    };

                    // Create and send offer (start as coroutine to stay on main thread)
                    _owner.StartCoroutine(CreateOfferAndConnectCoroutine());
                    // Start periodic stats logging for outbound payload
                    if (_statsCoroutine == null && ((WebRtcStreamer)_owner).enableStatsLogging)
                    {
                        _statsCoroutine = _owner.StartCoroutine(OutboundStatsLogger());
                    }
                    // Optionally start periodic keyframe generator (if API is available)
                    if (_keyframeCoroutine == null && ((WebRtcStreamer)_owner).forcePeriodicKeyframe)
                    {
                        _keyframeCoroutine = _owner.StartCoroutine(ForceKeyframeTicker());
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"WebRTC [{_config.streamName}] initialization failed: {e.Message}");
                    _isReady = false;
                }
            }

            private IEnumerator ForceKeyframeTicker()
            {
                // Uses RTCRtpSender.GenerateKeyFrame() if present in current Unity WebRTC package
                float wait = Mathf.Max(0.5f, ((WebRtcStreamer)_owner).keyframeIntervalSec);
                while (!_disposed)
                {
                    if (_peerConnection != null && _peerConnection.ConnectionState == RTCPeerConnectionState.Connected && _sender != null)
                    {
                        try
                        {
                            if (_generateKeyFrameMethod == null)
                            {
                                _generateKeyFrameMethod = typeof(RTCRtpSender).GetMethod("GenerateKeyFrame", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                if (_generateKeyFrameMethod == null)
                                {
                                    Debug.LogWarning($"[WebRTC {_config.streamName}] RTCRtpSender.GenerateKeyFrame() not available in this WebRTC package.");
                                    yield break;
                                }
                            }
                            _generateKeyFrameMethod.Invoke(_sender, null);
                            Debug.Log($"[WebRTC {_config.streamName}] Forced keyframe request on sender");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[WebRTC {_config.streamName}] Force keyframe failed: {ex.Message}");
                        }
                    }
                    yield return new WaitForSeconds(wait);
                }
            }

            private IEnumerator OutboundStatsLogger()
            {
                // Log bytes sent deltas every configured interval to detect freezes
                _lastStatsWallTime = Time.realtimeSinceStartup;
                var interval = Mathf.Max(1f, ((WebRtcStreamer)_owner).statsLogIntervalSeconds);
                while (!_disposed)
                {
                    if (_peerConnection == null || _peerConnection.ConnectionState != RTCPeerConnectionState.Connected)
                    {
                        yield return new WaitForSeconds(interval);
                        continue;
                    }
                    RTCStatsReportAsyncOperation op = null;
                    try
                    {
                        op = _peerConnection.GetStats();
                    }
                    catch { }
                    if (op == null)
                    {
                        yield return new WaitForSeconds(interval);
                        continue;
                    }
                    float timeout = Time.time + 2f * interval;
                    while (!op.IsDone && Time.time < timeout && !_disposed)
                    {
                        yield return null;
                    }
                    if (_disposed) yield break;
                    if (!op.IsDone || op.IsError)
                    {
                        yield return new WaitForSeconds(interval);
                        continue;
                    }
                    try
                    {
                        var report = op.Value; // RTCStatsReport
                        ulong bytesSent = ExtractOutboundVideoBytes(report);
                        var now = Time.realtimeSinceStartup;
                        var deltaTime = Mathf.Max(0.001f, now - _lastStatsWallTime);
                        if (_lastBytesSent > 0 && bytesSent >= _lastBytesSent)
                        {
                            ulong deltaBytes = bytesSent - _lastBytesSent;
                            double kbps = (deltaBytes * 8.0) / 1000.0 / deltaTime;
                            if (deltaBytes <= 64)
                                Debug.LogWarning($"[WebRTC {_config.streamName}] Outbound payload in last {deltaTime:0.00}s: {deltaBytes} bytes (~{kbps:0} kbps)");
                            else
                                Debug.Log($"[WebRTC {_config.streamName}] Outbound payload in last {deltaTime:0.00}s: {deltaBytes} bytes (~{kbps:0} kbps)");
                        }
                        _lastBytesSent = bytesSent;
                        _lastStatsWallTime = now;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[WebRTC {_config.streamName}] Stats parse error: {e.Message}");
                    }
                    yield return new WaitForSeconds(interval);
                }
            }

            private static ulong ExtractOutboundVideoBytes(RTCStatsReport report)
            {
                try
                {
                    // Try to iterate report stats via reflection to support multiple package versions
                    if (report == null) return 0;
                    var statsProp = report.GetType().GetProperty("Stats");
                    var dict = statsProp != null ? statsProp.GetValue(report) as System.Collections.IDictionary : null;
                    if (dict == null)
                    {
                        // Some versions expose 'Stats' as IEnumerable<KeyValuePair<string, RTCStats>>
                        var enumerable = statsProp?.GetValue(report) as System.Collections.IEnumerable;
                        if (enumerable != null)
                        {
                            ulong bytes = 0;
                            foreach (var entry in enumerable)
                            {
                                object stat = entry;
                                var kvpType = entry.GetType();
                                var valProp = kvpType.GetProperty("Value");
                                if (valProp != null) stat = valProp.GetValue(entry);
                                if (stat == null) continue;
                                if (IsOutboundVideoStat(stat, out ulong b)) bytes += b;
                            }
                            return bytes;
                        }
                        return 0;
                    }
                    ulong total = 0;
                    foreach (System.Collections.DictionaryEntry kv in dict)
                    {
                        var stat = kv.Value;
                        if (stat == null) continue;
                        if (IsOutboundVideoStat(stat, out ulong b)) total += b;
                    }
                    return total;
                }
                catch { return 0; }
            }

            private static bool IsOutboundVideoStat(object stat, out ulong bytesSent)
            {
                bytesSent = 0;
                if (stat == null) return false;
                var t = stat.GetType();
                // type / kind / mediaType
                string typeStr = TryGetString(stat, t.GetProperty("type")) ?? TryGetString(stat, t.GetProperty("Type"));
                if (string.IsNullOrEmpty(typeStr))
                {
                    var typeProp = t.GetProperty("m_Type", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    typeStr = TryGetString(stat, typeProp);
                }
                if (string.IsNullOrEmpty(typeStr) || !typeStr.ToLowerInvariant().Contains("outbound"))
                    return false;
                var kind = (TryGetString(stat, t.GetProperty("kind")) ?? TryGetString(stat, t.GetProperty("mediaType")) ?? "").ToLowerInvariant();
                if (kind.Length > 0 && kind != "video") return false;
                // bytesSent
                var bytesObj = TryGetULong(stat, t.GetProperty("bytesSent"));
                if (bytesObj.HasValue)
                {
                    bytesSent = bytesObj.Value;
                    return true;
                }
                return false;
            }

            private static string TryGetString(object obj, System.Reflection.PropertyInfo prop)
            {
                try
                {
                    if (prop == null) return null;
                    var v = prop.GetValue(obj);
                    return v?.ToString();
                }
                catch { return null; }
            }

            private static ulong? TryGetULong(object obj, System.Reflection.PropertyInfo prop)
            {
                try
                {
                    if (prop == null) return null;
                    var v = prop.GetValue(obj);
                    if (v == null) return null;
                    if (v is ulong u) return u;
                    if (ulong.TryParse(v.ToString(), out var parsed)) return parsed;
                    return null;
                }
                catch { return null; }
            }

            private IEnumerator CreateOfferAndConnectCoroutine()
            {
                if (_disposed || _peerConnection == null) yield break;

                // Reset ICE gathering state
                _gatheredCandidates.Clear();
                _iceGatheringComplete = false;

                // Create offer
                Debug.Log($"[WebRTC {_config.streamName}] Creating WebRTC offer...");
                var offerOp = _peerConnection.CreateOffer();
                // Wait for offer to complete
                float offerTimeout = Time.time + 10f; // 10 second timeout
                while (!offerOp.IsDone && !_disposed && Time.time < offerTimeout)
                {
                    yield return null;
                }
                if (_disposed || offerOp.IsError || !offerOp.IsDone)
                {
                    Debug.LogError($"[WebRTC {_config.streamName}] Failed to create offer (timeout or error)");
                    _isReady = false;
                    ScheduleReconnect();
                    yield break;
                }
                var offer = offerOp.Desc;

                // Set local description - this triggers ICE candidate gathering
                Debug.Log($"[WebRTC {_config.streamName}] Setting local description, gathering ICE candidates...");
                var setLocalOp = _peerConnection.SetLocalDescription(ref offer);
                float setLocalTimeout = Time.time + 10f; // 10 second timeout
                while (!setLocalOp.IsDone && !_disposed && Time.time < setLocalTimeout)
                {
                    yield return null;
                }
                if (_disposed || setLocalOp.IsError || !setLocalOp.IsDone)
                {
                    Debug.LogError($"[WebRTC {_config.streamName}] Failed to set local description (timeout or error)");
                    _isReady = false;
                    ScheduleReconnect();
                    yield break;
                }

                // Wait for ICE gathering to complete (for WHIP/WHEP, we need all candidates in SDP)
                // Unity WebRTC may include candidates in SDP automatically, but we wait to be sure
                float iceTimeout = Time.time + 5f; // 5 second timeout
                while (!_iceGatheringComplete && Time.time < iceTimeout && !_disposed)
                {
                    yield return null;
                }

                // Get the updated SDP (may have been updated with candidates)
                // Note: In Unity WebRTC, candidates might be added to SDP automatically
                // or we may need to manually add them. The implementation handles this.
                var updatedOffer = _peerConnection.LocalDescription;
                var sdpOffer = updatedOffer.sdp;
                Debug.Log($"[WebRTC {_config.streamName}] Sending SDP offer to endpoint: {_endpointUrl}");

                UnityWebRequest request = null;
                try
                {
                    // Warn about HTTP if platform disallows insecure HTTP
                    if (_endpointUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.LogWarning($"[WebRTC {_config.streamName}] Using HTTP endpoint {_endpointUrl}. If you see 'Insecure connection not allowed', enable 'Allow downloads over HTTP' in Player Settings or switch to HTTPS.");
                    }
                    if (_useJsonWhip)
                    {
                        // POST JSON: { sdp: "...", camera_id: "front" }
                        var json = "{\"sdp\":\"" + EscapeJson(sdpOffer) + "\",\"camera_id\":\"" + _config.streamName + "\"}";
                        var bytes = Encoding.UTF8.GetBytes(json);
                        request = new UnityWebRequest(_endpointUrl, UnityWebRequest.kHttpVerbPOST);
                        request.uploadHandler = new UploadHandlerRaw(bytes);
                        request.downloadHandler = new DownloadHandlerBuffer();
                        request.SetRequestHeader("Content-Type", "application/json");
                    }
                    else
                    {
                        request = UnityWebRequest.Post(_endpointUrl, sdpOffer, "application/sdp");
                    }
                    // Accept self-signed HTTPS certs when requested
                    if (_endpointUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && _acceptSelfSigned)
                    {
                        request.certificateHandler = new AcceptAllCertificates();
                        request.disposeCertificateHandlerOnDispose = true;
                    }
                    request.timeout = 10; // 10 second timeout
                    var requestOp = request.SendWebRequest();
                    float requestTimeout = Time.time + 10f; // 10 second timeout
                    while (!requestOp.isDone && !_disposed && Time.time < requestTimeout)
                    {
                        yield return null;
                    }

                    if (_disposed) yield break;

                    if (!requestOp.isDone)
                    {
                        Debug.LogError($"[WebRTC {_config.streamName}] Request to signaling server timed out");
                        _isReady = false;
                        ScheduleReconnect();
                        yield break;
                    }

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        string body = request.downloadHandler.text ?? string.Empty;
                        string contentType = request.GetResponseHeader("Content-Type") ?? string.Empty;
                        string sdpAnswer = null;
                        // Capture WHIP resource for cleanup
                        _whipResourceUrl = request.GetResponseHeader("Location");
                        _whipResourceETag = request.GetResponseHeader("ETag");

                        if (_useJsonWhip)
                        {
                            if (contentType.IndexOf("application/sdp", StringComparison.OrdinalIgnoreCase) >= 0 || LooksLikeSdp(body))
                            {
                                sdpAnswer = body;
                            }
                            else
                            {
                                // Try JSON with various fields
                                try
                                {
                                    var ans = JsonUtility.FromJson<SdpAnswer>(body);
                                    if (ans != null && !string.IsNullOrEmpty(ans.sdp))
                                    {
                                        sdpAnswer = ans.sdp;
                                    }
                                }
                                catch { }

                                if (string.IsNullOrEmpty(sdpAnswer))
                                {
                                    sdpAnswer = ExtractJsonValue(body, "sdp");
                                }
                                if (string.IsNullOrEmpty(sdpAnswer))
                                {
                                    sdpAnswer = ExtractJsonValue(body, "answer");
                                }
                                if (string.IsNullOrEmpty(sdpAnswer) && LooksLikeSdp(body))
                                {
                                    sdpAnswer = body;
                                }

                                if (string.IsNullOrEmpty(sdpAnswer))
                                {
                                    var preview = body.Length > 256 ? body.Substring(0, 256) + "..." : body;
                                    Debug.LogError($"[WebRTC {_config.streamName}] Could not extract SDP from response. Content-Type='{contentType}', body preview='{preview}'");
                                    _isReady = false;
                                    yield break;
                                }
                            }
                        }
                        else
                        {
                            sdpAnswer = body;
                        }
                        var answer = new RTCSessionDescription
                        {
                            type = RTCSdpType.Answer,
                            sdp = sdpAnswer
                        };
                        var setRemoteOp = _peerConnection.SetRemoteDescription(ref answer);
                        float setRemoteTimeout = Time.time + 10f; // 10 second timeout
                        while (!setRemoteOp.IsDone && !_disposed && Time.time < setRemoteTimeout)
                        {
                            yield return null;
                        }
                        if (_disposed || setRemoteOp.IsError || !setRemoteOp.IsDone)
                        {
                            Debug.LogError($"[WebRTC {_config.streamName}] Failed to set remote description (timeout or error)");
                            _isReady = false;
                            ScheduleReconnect();
                            yield break;
                        }
                        _isReady = true;
                        Debug.Log($"[WebRTC {_config.streamName}] ✓ Connected to signaling server at {_endpointUrl}");
                        Debug.Log($"[WebRTC {_config.streamName}] Stream available at:");
                        Debug.Log($"[WebRTC {_config.streamName}]   - WHIP publish endpoint: {_endpointUrl}");
                        if (!string.IsNullOrEmpty(_whipResourceUrl))
                        {
                            Debug.Log($"[WebRTC {_config.streamName}]   - WHIP resource: {_whipResourceUrl} (will DELETE on teardown)");
                        }
                    }
                    else
                    {
                        Debug.LogError($"WebRTC [{_config.streamName}] failed to connect to {_endpointUrl}: {request.error}");
                        _isReady = false;
                        ScheduleReconnect();
                    }
                }
                finally
                {
                    request?.Dispose();
                }
            }

            private IEnumerator DeleteWhipResource()
            {
                if (!_useWhip) yield break;
                if (string.IsNullOrEmpty(_whipResourceUrl)) yield break;
                UnityWebRequest del = null;
                try
                {
                    del = UnityWebRequest.Delete(_whipResourceUrl);
                    if (!string.IsNullOrEmpty(_whipResourceETag))
                    {
                        del.SetRequestHeader("If-Match", _whipResourceETag);
                    }
                    var op = del.SendWebRequest();
                    float until = Time.time + 4f;
                    while (!op.isDone && Time.time < until) { yield return null; }
                    if (op.isDone && del.result == UnityWebRequest.Result.Success)
                    {
                        Debug.Log($"[WebRTC {_config.streamName}] Deleted WHIP resource {_whipResourceUrl}");
                    }
                }
                finally
                {
                    try { del?.Dispose(); } catch { }
                    _whipResourceUrl = null;
                    _whipResourceETag = null;
                }
            }

            private void ScheduleReconnect()
            {
                if (_disposed) return;
                if (!((WebRtcStreamer)_owner).autoReconnect) return;
                if (_reconnectCoroutine != null) return;
                _reconnectCoroutine = _owner.StartCoroutine(ReconnectWithBackoff());
            }

            private IEnumerator ReconnectWithBackoff()
            {
                if (_disposed) yield break;

                _isReady = false;
                // Attempt to cleanup WHIP resource before tearing down
                yield return DeleteWhipResource();
                if (_peerConnection != null)
                {
                    try
                    {
                        _peerConnection.Close();
                        _peerConnection.Dispose();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[WebRTC {_config.streamName}] error closing peer connection: {e.Message}");
                    }
                    _peerConnection = null;
                }

                // Backoff and retry loop until connected or disposed
                while (!_disposed)
                {
                    var parent = (WebRtcStreamer)_owner;
                    var maxDelay = Mathf.Max(parent.reconnectInitialDelaySec, parent.reconnectMaxDelaySec);
                    var delay = Mathf.Clamp(_currentBackoffSec, 0.5f, maxDelay);
                    Debug.Log($"[WebRTC {_config.streamName}] Reconnecting in {delay:0.0}s...");
                    yield return new WaitForSeconds(delay);

                    // Attempt re-initialize and connect
                    yield return InitializeCoroutine();

                    // Wait a short window to see if it connects
                    float waitUntil = Time.time + 5f;
                    while (Time.time < waitUntil && !_disposed)
                    {
                        if (_peerConnection != null && _peerConnection.ConnectionState == RTCPeerConnectionState.Connected)
                        {
                            _reconnectCoroutine = null;
                            yield break;
                        }
                        yield return null;
                    }

                    // Increase backoff for next round
                    _currentBackoffSec = Mathf.Min(delay * 2f, maxDelay);
                }
                _reconnectCoroutine = null;
            }

            public void UpdateFrame(RenderTexture source)
            {
                if (_disposed || !_isReady || _videoTrack == null || source == null) return;

                // VideoStreamTrack should automatically read from _sourceTexture, but let's verify it's active
                if (!_videoTrack.Enabled)
                {
                    Debug.LogWarning($"[WebRTC {_config.streamName}] VideoStreamTrack is disabled! Enabling...");
                    _videoTrack.Enabled = true;
                }

                _frameCount++;
                var parent = (WebRtcStreamer)_owner;
                int interval = Mathf.Max(1, parent.frameLogInterval <= 0 ? 100 : parent.frameLogInterval);
                if (_frameCount % interval == 0)
                {
                    Debug.Log($"[WebRTC {_config.streamName}] Frame {_frameCount} ({source.width}x{source.height}) | " +
                             $"Track enabled: {_videoTrack.Enabled} | " +
                             $"Track ReadyState: {_videoTrack.ReadyState} | " +
                             $"Connection: {_peerConnection.ConnectionState} | " +
                             $"Texture valid: {source.IsCreated()}");
                }
            }

            public void Dispose()
            {
                _disposed = true;
                _isReady = false;
                try
                {
                    // Cleanup WHIP resource if present
                    try { _owner.StartCoroutine(DeleteWhipResource()); } catch { }
                    if (_statsCoroutine != null)
                    {
                        try { _owner.StopCoroutine(_statsCoroutine); } catch { }
                        _statsCoroutine = null;
                    }
                    if (_keyframeCoroutine != null)
                    {
                        try { _owner.StopCoroutine(_keyframeCoroutine); } catch { }
                        _keyframeCoroutine = null;
                    }
                    _videoTrack?.Dispose();
                    _videoTrack = null;
                    _peerConnection?.Close();
                    _peerConnection?.Dispose();
                    _peerConnection = null;
                    // nothing extra to release for source texture
                }
                catch (Exception e)
                {
                    Debug.LogError($"[WebRTC {_config.streamName}] dispose error: {e.Message}");
                }
            }

            [Serializable]
            private class SdpAnswer { public string sdp; }

            private static string EscapeJson(string s)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;
                return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
            }
        }

        private sealed class AcceptAllCertificates : CertificateHandler
        {
            protected override bool ValidateCertificate(byte[] certificateData)
            {
                return true;
            }
        }

        // Helpers
        private static string LayerMaskToString(int mask)
        {
            if (mask == 0) return "Nothing";
            if (mask == -1) return "Everything";
            var layers = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    var layerName = LayerMask.LayerToName(i);
                    layers.Add(string.IsNullOrEmpty(layerName) ? $"Layer{i}" : layerName);
                }
            }
            return layers.Count > 0 ? string.Join(", ", layers) : "None";
        }

        private static bool LooksLikeSdp(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.StartsWith("v=", StringComparison.Ordinal)) return true;
            if (s.IndexOf("\nm=", StringComparison.Ordinal) >= 0) return true;
            if (s.IndexOf("a=ice-ufrag", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string ExtractJsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            try
            {
                var needle = "\"" + key + "\"";
                int i = json.IndexOf(needle, StringComparison.Ordinal);
                if (i < 0) return null;
                i = json.IndexOf(':', i);
                if (i < 0) return null;
                // Skip whitespace
                while (i + 1 < json.Length && char.IsWhiteSpace(json[i + 1])) i++;
                if (i + 1 >= json.Length || json[i + 1] != '"') return null;
                int start = i + 2;
                var sb = new System.Text.StringBuilder();
                bool escape = false;
                for (int j = start; j < json.Length; j++)
                {
                    char c = json[j];
                    if (escape)
                    {
                        // Keep escaped form; SDP will handle \n, etc.
                        sb.Append(c == 'n' ? '\n' : (c == 'r' ? '\r' : (c == 't' ? '\t' : c)));
                        escape = false;
                        continue;
                    }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') return sb.ToString();
                    sb.Append(c);
                }
            }
            catch { }
            return null;
        }

        // Switch which Unity Camera is streamed for a given stream name at runtime
        public void SetStreamCamera(string streamName, Camera newCamera)
        {
            if (string.IsNullOrEmpty(streamName)) return;
            bool changed = false;
            for (int i = 0; i < streams.Count; i++)
            {
                var sc = streams[i];
                if (sc != null && sc.streamName == streamName)
                {
                    sc.camera = newCamera;
                    changed = true;
                    break;
                }
            }
            if (changed)
            {
                InitializeStreams();
            }
        }
    }
}

