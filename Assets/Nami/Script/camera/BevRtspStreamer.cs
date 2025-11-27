using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/*
gst-launch-1.0 rtspsrc location=rtsp://127.0.0.1:8554/front protocols=tcp latency=0 drop-on-latency=true \
  ! rtph264depay ! h264parse ! vtdec ! videoconvert ! autovideosink sync=false
*/

namespace Nami
{
    [DisallowMultipleComponent]
    public class BevRtspStreamer : MonoBehaviour
    {
        [Serializable]
        public class StreamConfig
        {
            public string streamName = "front";
            public Camera camera;
            public int width = 1280;
            public int height = 720;
            [Tooltip("Target frame rate for capture and encoding. Higher values = smoother but more CPU/GPU usage. Recommended: 30 for real-time streaming.")]
            public int fps = 30; // Increased from 15 for better real-time performance
            public int bitrateKbps = 4000;
            public bool flipVertical = true; // raw GPU readback is typically bottom-up
        }

        public string rtspBaseUrl = "rtsp://127.0.0.1:8554/";
        public string ffmpegPath = "ffmpeg"; // Set absolute path if Editor PATH doesn't include ffmpeg

        public List<StreamConfig> streams = new List<StreamConfig>();
        [Tooltip("Force a specific encoder (e.g., 'h264_nvenc', 'h264_vaapi', 'libx264'). Leave empty for auto-detection.")]
        public string forceEncoder = ""; // Allow manual override
        
        [Header("Debug")]
        [Tooltip("Log streaming status periodically")]
        public bool logStreamingDebug = true;
        [Tooltip("Seconds between streaming debug logs")]
        public float debugLogIntervalSec = 15.0f;

        private readonly List<RenderTexture> _renderTextures = new List<RenderTexture>();
        private readonly List<RenderTexture> _resolveLdrTextures = new List<RenderTexture>();
        private readonly List<SimpleRtspPusher> _pushers = new List<SimpleRtspPusher>();
        private readonly List<float> _nextCaptureTime = new List<float>();
        private readonly List<bool> _readbackPending = new List<bool>();
        private readonly List<ulong> _frameIds = new List<ulong>();
        private readonly List<float> _nextDebugLogTime = new List<float>();
        private CameraMetaPublisher _metaPublisher;
        private int _generation = 0;
        private static readonly System.Collections.Generic.HashSet<string> _detectedEncoders = new System.Collections.Generic.HashSet<string>();

        private void OnEnable()
        {
            _metaPublisher = new CameraMetaPublisher();
            InitializeStreams();
        }

        private void OnDisable()
        {
            TeardownStreams();
            _metaPublisher?.Dispose();
            _metaPublisher = null;
        }

        private void InitializeStreams()
        {
            TeardownStreams();
            _renderTextures.Clear();
            _resolveLdrTextures.Clear();
            _pushers.Clear();
            _nextCaptureTime.Clear();
            _readbackPending.Clear();
            _frameIds.Clear();
            _nextDebugLogTime.Clear();
            _generation++;

            // Resolve ffmpeg absolute path if needed
            ffmpegPath = ResolveFfmpegPath(ffmpegPath);

            foreach (var sc in streams)
            {
                if (sc.camera == null) continue;

                var width = Mathf.Max(16, sc.width);
                var height = Mathf.Max(16, sc.height);

                // Always render to HDR target so URP post-processing is correct
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
                // Set viewport rect to full render texture to avoid frustum errors
                // When targetTexture is set, Unity adjusts the viewport, but we ensure it's correct
                sc.camera.rect = new Rect(0, 0, 1, 1); // Full viewport (normalized coordinates)

                // Create an LDR sRGB resolve target so we can apply gamma before encode
#if UNITY_2019_1_OR_NEWER
                var ldrDesc = new RenderTextureDescriptor(width, height)
                {
                    colorFormat = RenderTextureFormat.ARGB32,
                    depthBufferBits = 0,
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
#else
                var resolve = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    useMipMap = false,
                    antiAliasing = 1,
                    autoGenerateMips = false,
                    enableRandomWrite = false,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
#endif
                resolve.Create();
                _resolveLdrTextures.Add(resolve);

                var urp = sc.camera.GetUniversalAdditionalCameraData();
                if (urp != null)
                {
                    // Respect camera inspector settings. If PP is enabled but mask/trigger are unset, set safe defaults
                    if (urp.renderPostProcessing)
                    {
                        if (urp.volumeLayerMask == 0)
                            urp.volumeLayerMask = ~0; // include all layers by default
                        if (urp.volumeTrigger == null)
                            urp.volumeTrigger = sc.camera.transform;
                    }
                }

                var url = rtspBaseUrl.TrimEnd('/') + "/" + sc.streamName;
                var pusher = new SimpleRtspPusher(ffmpegPath, width, height, sc.fps, sc.bitrateKbps, url, sc.flipVertical, forceEncoder);
                pusher.Start();

                _renderTextures.Add(rt);
                _pushers.Add(pusher);
                _nextCaptureTime.Add(Time.time);
                _readbackPending.Add(false);
                _frameIds.Add(0);
                _nextDebugLogTime.Add(Time.time + debugLogIntervalSec);
            }
        }

        private void TeardownStreams()
        {
            foreach (var p in _pushers)
            {
                try { p?.Dispose(); } catch { /* ignore */ }
            }
            _pushers.Clear();

            foreach (var rt in _renderTextures)
            {
                if (rt == null) continue;
                try { rt.Release(); } catch { /* ignore */ }
            }
            _renderTextures.Clear();

            foreach (var rt in _resolveLdrTextures)
            {
                if (rt == null) continue;
                try { rt.Release(); } catch { /* ignore */ }
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

        private void Update()
        {
            for (int i = 0; i < streams.Count; i++)
            {
                var sc = streams[i];
                if (sc.camera == null) continue;
                if (_pushers.Count <= i || _renderTextures.Count <= i) continue;

                var pusher = _pushers[i];
                var rt = _renderTextures[i];

                var now = Time.time;
                var frameInterval = 1f / Mathf.Max(1, sc.fps);
                if (now < _nextCaptureTime[i]) continue;
                _nextCaptureTime[i] = now + frameInterval;

                if (pusher == null || !pusher.IsRunning) continue;

                // Skip if readback is already pending to avoid overwhelming GPU
                if (_readbackPending[i]) continue;

                // If HDR target is used, resolve to LDR sRGB before readback to apply gamma
                // Note: Graphics.Blit can be a bottleneck with multiple streams - consider disabling HDR if not needed
                RenderTexture src = rt;
                var resolve = _resolveLdrTextures.Count > i ? _resolveLdrTextures[i] : null;
                if (resolve != null)
                {
                    // Use async GPU blit if available, otherwise sync (may cause bottleneck)
                    Graphics.Blit(rt, resolve);
                    src = resolve;
                }

                _readbackPending[i] = true;

                var frameId = ++_frameIds[i];
                // Use UtcNow for absolute timestamp (aligns with other systems if synchronized)
                var captureTimestamp = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                
                _metaPublisher?.Publish(sc.streamName, frameId, captureTimestamp);

                // Throttled debug logging with encoding metrics
                if (logStreamingDebug && now >= _nextDebugLogTime[i])
                {
                    _nextDebugLogTime[i] = now + debugLogIntervalSec;
                    var status = pusher.IsRunning ? "running" : "stopped";
                    var metrics = pusher.IsRunning 
                        ? $"encoded={pusher.EncodedFrames} fps={pusher.EncodingFps:F1} speed={pusher.EncodingSpeed:F2}x queue={pusher.QueueSize}"
                        : "N/A";
                    UnityEngine.Debug.Log($"[BevRtspStreamer] {sc.streamName}: capture_frame={frameId} status={status} {metrics}");
                }

                var cbIndex = i;            // capture stable index
                var cbGeneration = _generation; // capture generation to discard stale callbacks

                // Use AsyncGPUReadback with mipLevel 0 for fastest readback
                // Note: GPU readback is often the bottleneck, not the encoder
                AsyncGPUReadback.Request(src, 0, TextureFormat.RGBA32, request =>
                {
                    if (cbGeneration != _generation) return; // streams reinitialized; drop
                    if (cbIndex < 0 || cbIndex >= _readbackPending.Count) return;
                    _readbackPending[cbIndex] = false;
                    if (request.hasError) return;
                    if (cbIndex < 0 || cbIndex >= _pushers.Count) return;
                    var p = _pushers[cbIndex];
                    if (p == null || !p.IsRunning) return;
                    var data = request.GetData<byte>();
                    // Copy NativeArray to managed array (required for thread safety)
                    // This copy is necessary but adds overhead - GPU readback is the main bottleneck
                    var bytes = new byte[data.Length];
                    data.CopyTo(bytes);
                    p.EnqueueFrame(bytes, frameId, captureTimestamp);
                });
            }
        }

        /*
        private sealed class FfmpegPusher : IDisposable
        {
            private readonly string _ffmpegPath;
            private readonly int _width;
            private readonly int _height;
            private readonly int _fps;
            private readonly int _bitrateKbps;
            private readonly string _rtspUrl;

            private Process _proc;
            private Thread _writerThread;
            private const int MaxQueuedFrames = 2; // Reduced for lower latency - drop frames if encoder can't keep up
            private readonly BlockingCollection<byte[]> _queue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), MaxQueuedFrames);
            private volatile bool _running;
            private int _logFrameCounter = 0; // Counter to reduce log frequency

            public bool IsRunning => _running && _proc != null && !_proc.HasExited;

            private readonly bool _flipVertical;
            private readonly string _forceEncoder;

            public FfmpegPusher(string ffmpegPath, int width, int height, int fps, int bitrateKbps, string rtspUrl, bool flipVertical, string forceEncoder = "")
            {
                _ffmpegPath = ffmpegPath;
                _width = width;
                _height = height;
                _fps = fps;
                _bitrateKbps = bitrateKbps;
                _rtspUrl = rtspUrl;
                _flipVertical = flipVertical;
                _forceEncoder = forceEncoder;
            }

            public void Start()
            {
                if (_running) return;

                var bitrate = _bitrateKbps <= 0 ? 4000 : _bitrateKbps;
                var maxrate = bitrate; // kbps
                var bufsize = bitrate; // Reduced from bitrate * 2 for lower latency
                // Minimal, robust filter chain: optional vertical flip + yuv420p conversion
                // Rely on encoder color metadata; some players mishandle manual range remapping (causing washout)
                var vfChain = _flipVertical ? "-vf vflip,format=yuv420p" : "-vf format=yuv420p";

                // Cross-platform encoder selection with hardware acceleration detection
                string encoder;
                string lowLatency = string.Empty;
                string colorFlags = string.Empty;
                
                // Use forced encoder if specified
                if (!string.IsNullOrEmpty(_forceEncoder))
                {
                    encoder = _forceEncoder;
                    UnityEngine.Debug.Log($"Using forced encoder: {encoder}");
                }
                else if (Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer)
                {
                    encoder = "h264_videotoolbox"; // macOS optimized (GPU)
                    lowLatency = "-allow_sw 0"; // Force hardware encoding
                    colorFlags = "-color_range tv -colorspace bt709 -color_trc bt709 -color_primaries bt709 -pix_fmt yuv420p";
                }
                else
                {
                    // Auto-detect best available encoder on Linux/Windows
                    encoder = BevRtspStreamer.DetectBestEncoder(_ffmpegPath);
                }
                
                // Configure encoder-specific settings
                if (encoder == "h264_videotoolbox")
                {
                    // macOS hardware encoder
                    lowLatency = "-allow_sw 0 -realtime 1";
                    colorFlags = "-color_range tv -colorspace bt709 -color_trc bt709 -color_primaries bt709 -pix_fmt yuv420p";
                }
                else if (encoder == "h264_nvenc")
                {
                    // NVIDIA GPU encoder (Linux/Windows)
                    // Optimized for multiple concurrent streams with low latency
                    // Preset: p1 (fastest) for real-time encoding, p2-p7 for better quality but slower
                    // Tune: ull (ultra low latency) for minimal delay
                    // Bitrate is controlled via -b:v parameter (already set)
                    lowLatency = "-preset p1 -tune ull -gpu 0 -zerolatency 1 -rc-lookahead 0";
                    colorFlags = "-color_range tv -colorspace bt709 -color_trc bt709 -color_primaries bt709";
                }
                else if (encoder == "h264_vaapi")
                {
                    // Intel/AMD GPU encoder (Linux)
                    lowLatency = "-rc_mode CBR -b_strategy 0 -bf 0 -g 30 -idr_interval 30";
                    colorFlags = "-color_range tv -colorspace bt709 -color_trc bt709 -color_primaries bt709";
                }
                else if (encoder == "h264_v4l2m2m")
                {
                    // Raspberry Pi hardware encoder
                    lowLatency = "";
                    colorFlags = "-color_range tv -colorspace bt709 -color_trc bt709 -color_primaries bt709";
                }
                else
                {
                    // Fallback to CPU encoder (libx264)
                    encoder = "libx264";
                    // Ultra-low latency x264 settings optimized for multiple streams
                    // Use more threads for better multi-stream performance, but limit lookahead
                    var threadCount = Mathf.Max(1, System.Environment.ProcessorCount / 4); // Share CPU across streams
                    var x264Params = $"keyint={_fps}:min-keyint={_fps}:scenecut=0:bframes=0:ref=1:no-mbtree=1:rc-lookahead=0:sync-lookahead=0:threads={threadCount}:colorprimaries=bt709:transfer=bt709:colormatrix=bt709";
                    lowLatency = $"-preset ultrafast -tune zerolatency -x264-params {x264Params}";
                    colorFlags = "-color_range tv -colorspace bt709 -color_trc bt709 -color_primaries bt709";
                    UnityEngine.Debug.LogWarning($"Using CPU encoder (libx264) - performance will be limited with multiple streams. Consider using GPU encoder (h264_nvenc/h264_vaapi) for better multi-stream performance.");
                }

                // Reduced GOP size for lower latency: g = fps (1 second) instead of fps * 2
                // Use intra-refresh for even lower latency (no keyframes, constant bitrate)
                var gopSize = _fps; // 1 second GOP for balance between latency and quality
                var args = $"-f rawvideo -pix_fmt rgba -s {_width}x{_height} -r {_fps} -i - {vfChain} {colorFlags} -f rtsp -rtsp_transport tcp -c:v {encoder} {lowLatency} -b:v {bitrate}k -maxrate {maxrate}k -bufsize {bufsize}k -g {gopSize} -fflags nobuffer -flags low_delay -strict experimental {_rtspUrl}";

                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false,
                    CreateNoWindow = true
                };

                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.ErrorDataReceived += (s, e) => 
                { 
                    if (!string.IsNullOrEmpty(e.Data)) 
                    {
                        // Always log errors immediately
                        if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) || 
                            e.Data.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                            e.Data.Contains("cannot", StringComparison.OrdinalIgnoreCase))
                        {
                            UnityEngine.Debug.LogError($"ffmpeg[{_rtspUrl}] ERROR: {e.Data}");
                        }
                        // Log frame progress only every 100 frames to reduce log spam
                        else if (e.Data.Contains("frame="))
                        {
                            _logFrameCounter++;
                            if (_logFrameCounter >= 100)
                            {
                                UnityEngine.Debug.Log($"ffmpeg[{_rtspUrl}]: {e.Data}");
                                _logFrameCounter = 0;
                            }
                        }
                        // Log other important messages (startup, warnings, etc.) immediately
                        else if (e.Data.Contains("Input #") || e.Data.Contains("Output #") || 
                                 e.Data.Contains("Stream mapping") || e.Data.Contains("deprecated") ||
                                 e.Data.Contains("warning", StringComparison.OrdinalIgnoreCase))
                        {
                            UnityEngine.Debug.Log($"ffmpeg[{_rtspUrl}]: {e.Data}");
                        }
                        // Skip verbose frame-by-frame logs
                    }
                };
                _proc.Exited += (s, e) => 
                { 
                    _running = false; 
                    UnityEngine.Debug.LogWarning($"ffmpeg[{_rtspUrl}] process exited with code {_proc.ExitCode}");
                    try { _queue.CompleteAdding(); } catch { } 
                };
                try
                {
                    UnityEngine.Debug.Log($"Starting RTSP stream: url={_rtspUrl} res={_width}x{_height} fps={_fps} bitrate={bitrate}k encoder={encoder}");
                    _proc.Start();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"FFmpeg start failed: '{_ffmpegPath}'. {ex.Message}");
                    _running = false;
                    return;
                }
                _proc.BeginErrorReadLine();

                _running = true;
                _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "FfmpegPusher" };
                _writerThread.Start();
            }

            public void EnqueueFrame(byte[] rgba)
            {
                if (!_running) return;
                if (_queue.IsAddingCompleted) return;
                // Aggressively drop frames if queue is full to minimize latency
                // Only keep the most recent frame
                if (!_queue.TryAdd(rgba))
                {
                    // Clear the queue and add only the newest frame
                    while (_queue.TryTake(out _)) { }
                    _queue.TryAdd(rgba);
                }
            }

            private void WriterLoop()
            {
                try
                {
                    using (var stdin = _proc.StandardInput.BaseStream)
                    {
                        // Use unbuffered writes for lower latency
                        foreach (var frame in _queue.GetConsumingEnumerable())
                        {
                            stdin.Write(frame, 0, frame.Length);
                            stdin.Flush(); // Immediate flush for lower latency
                        }
                    }
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                try
                {
                    _queue.CompleteAdding();
                }
                catch { }

                try
                {
                    _running = false;
                    _writerThread?.Join(500);
                }
                catch { }

                try
                {
                    if (_proc != null && !_proc.HasExited)
                    {
                        _proc.Kill();
                        _proc.WaitForExit(1000);
                    }
                }
                catch {                 }
                finally
                {
                    _proc?.Dispose();
                }
            }
        }
        */

        // Switch which Unity Camera is streamed for a given stream name at runtime.
        // This will rebuild the render textures and restart the ffmpeg pushers.
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

        private static string ResolveFfmpegPath(string configured)
        {
            // If user set an absolute path, use it only if it exists
            if (!string.IsNullOrEmpty(configured) && configured != "ffmpeg")
            {
                try { if (System.IO.File.Exists(configured)) return configured; } catch { }
            }

            // Try to resolve via `which ffmpeg` (helps when PATH is available)
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/which",
                    Arguments = "ffmpeg",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    var path = proc.StandardOutput.ReadLine();
                    proc.WaitForExit(500);
                    if (!string.IsNullOrEmpty(path))
                    {
                        try { if (System.IO.File.Exists(path)) return path; } catch { }
                    }
                }
            }
            catch { }

            // Common macOS locations when Unity is launched outside a login shell (PATH not inherited)
            string[] candidates =
            {
                "/opt/homebrew/bin/ffmpeg",    // Homebrew (Apple Silicon)
                "/usr/local/bin/ffmpeg",       // Homebrew (Intel) / manual
                "/opt/local/bin/ffmpeg",       // MacPorts
                "/usr/bin/ffmpeg"              // Rarely present on macOS
            };
            foreach (var c in candidates)
            {
                try { if (System.IO.File.Exists(c)) return c; } catch { }
            }

            // Last resort: return original token ("ffmpeg" or user input) so error message shows it
            return string.IsNullOrEmpty(configured) ? "ffmpeg" : configured;
        }

        private static string DetectBestEncoder(string ffmpegPath)
        {
            // Priority order for hardware encoders (best to worst)
            string[] hardwareEncoders = { "h264_nvenc", "h264_vaapi", "h264_v4l2m2m" };
            
            foreach (var encoder in hardwareEncoders)
            {
                if (IsEncoderAvailable(ffmpegPath, encoder))
                {
                    UnityEngine.Debug.Log($"Detected GPU encoder: {encoder}");
                    return encoder;
                }
            }
            
            UnityEngine.Debug.Log("No GPU encoder detected, falling back to CPU encoder (libx264)");
            return "libx264";
        }

        private static bool IsEncoderAvailable(string ffmpegPath, string encoder)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using (var proc = new Process { StartInfo = psi })
                {
                    var outputBuilder = new System.Text.StringBuilder();
                    var errorBuilder = new System.Text.StringBuilder();
                    
                    // Set up async reading to avoid deadlocks
                    proc.OutputDataReceived += (sender, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };
                    
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    
                    // Wait for process to complete (increase timeout for slower systems)
                    if (!proc.WaitForExit(5000))
                    {
                        proc.Kill();
                        return false;
                    }
                    
                    // Give it a moment to finish reading async streams
                    System.Threading.Thread.Sleep(100);
                    
                    // FFmpeg outputs encoder list to stderr, check if encoder is available
                    // Look for the encoder name in the list (format: "V..... h264_nvenc")
                    var combined = outputBuilder.ToString() + errorBuilder.ToString();
                    var found = combined.Contains(encoder);
                    
                    // Only log once per encoder type to reduce spam when multiple streams use same encoder
                    if (found && !_detectedEncoders.Contains(encoder))
                    {
                        UnityEngine.Debug.Log($"Encoder '{encoder}' detected in ffmpeg output");
                        _detectedEncoders.Add(encoder);
                    }
                    
                    return found;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Failed to check encoder availability for '{encoder}': {ex.Message}");
                return false;
            }
        }
    }
}


