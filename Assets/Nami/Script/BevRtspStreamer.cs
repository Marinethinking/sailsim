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
            public int fps = 15;
            public int bitrateKbps = 4000;
            public bool flipVertical = true; // raw GPU readback is typically bottom-up
        }

        public string rtspBaseUrl = "rtsp://127.0.0.1:8554/";
        public string ffmpegPath = "ffmpeg"; // Set absolute path if Editor PATH doesn't include ffmpeg
        public List<StreamConfig> streams = new List<StreamConfig>();

        private readonly List<RenderTexture> _renderTextures = new List<RenderTexture>();
        private readonly List<RenderTexture> _resolveLdrTextures = new List<RenderTexture>();
        private readonly List<FfmpegPusher> _pushers = new List<FfmpegPusher>();
        private readonly List<float> _nextCaptureTime = new List<float>();
        private readonly List<bool> _readbackPending = new List<bool>();
        private int _generation = 0;

        private void OnEnable()
        {
            InitializeStreams();
        }

        private void OnDisable()
        {
            TeardownStreams();
        }

        private void InitializeStreams()
        {
            TeardownStreams();
            _renderTextures.Clear();
            _resolveLdrTextures.Clear();
            _pushers.Clear();
            _nextCaptureTime.Clear();
            _readbackPending.Clear();
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
                var pusher = new FfmpegPusher(ffmpegPath, width, height, sc.fps, sc.bitrateKbps, url, sc.flipVertical);
                pusher.Start();

                _renderTextures.Add(rt);
                _pushers.Add(pusher);
                _nextCaptureTime.Add(Time.time);
                _readbackPending.Add(false);
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

                // If HDR target is used, resolve to LDR sRGB before readback to apply gamma
                RenderTexture src = rt;
                var resolve = _resolveLdrTextures.Count > i ? _resolveLdrTextures[i] : null;
                if (resolve != null)
                {
                    Graphics.Blit(rt, resolve);
                    src = resolve;
                }

                if (_readbackPending[i]) continue; // avoid piling up GPU readbacks
                _readbackPending[i] = true;

                var cbIndex = i;            // capture stable index
                var cbGeneration = _generation; // capture generation to discard stale callbacks

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
                    var bytes = new byte[data.Length];
                    data.CopyTo(bytes);
                    p.EnqueueFrame(bytes);
                });
            }
        }

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
            private const int MaxQueuedFrames = 3; // bound latency and memory
            private readonly BlockingCollection<byte[]> _queue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), MaxQueuedFrames);
            private volatile bool _running;

            public bool IsRunning => _running && _proc != null && !_proc.HasExited;

            private readonly bool _flipVertical;

            public FfmpegPusher(string ffmpegPath, int width, int height, int fps, int bitrateKbps, string rtspUrl, bool flipVertical)
            {
                _ffmpegPath = ffmpegPath;
                _width = width;
                _height = height;
                _fps = fps;
                _bitrateKbps = bitrateKbps;
                _rtspUrl = rtspUrl;
                _flipVertical = flipVertical;
            }

            public void Start()
            {
                if (_running) return;

                var bitrate = _bitrateKbps <= 0 ? 4000 : _bitrateKbps;
                var maxrate = bitrate; // kbps
                var bufsize = bitrate * 2; // kb
                // Minimal, robust filter chain: optional vertical flip + yuv420p conversion
                // Rely on encoder color metadata; some players mishandle manual range remapping (causing washout)
                var vfChain = _flipVertical ? "-vf vflip,format=yuv420p" : "-vf format=yuv420p";

                // Cross-platform encoder selection
                string encoder;
                if (Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer)
                {
                    encoder = "h264_videotoolbox"; // macOS optimized
                    // encoder = "libx264";
                }
                else
                {
                    encoder = "libx264"; // Linux, Windows, and other platforms
                }

                // Color metadata/levels for better consistency in players (bt709). Default to TV/limited.
                string colorFlags = encoder == "libx264"
                    ? "-color_range tv -colorspace bt709 -color_trc bt709 -color_primaries bt709 -x264-params colorprimaries=bt709:transfer=bt709:colormatrix=bt709"
                    : "-color_range tv -colorspace bt709 -color_trc bt709 -color_primaries bt709 -pix_fmt yuv420p";

                var lowLatency = encoder == "libx264" ? "-preset veryfast -tune zerolatency" : string.Empty;
                var args = $"-f rawvideo -pix_fmt rgba -s {_width}x{_height} -r {_fps} -i - {vfChain} {colorFlags} -f rtsp -rtsp_transport tcp -c:v {encoder} {lowLatency} -b:v {bitrate}k -maxrate {maxrate}k -bufsize {bufsize}k -g {_fps * 2} {_rtspUrl}";

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
                _proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) UnityEngine.Debug.Log($"ffmpeg[{_rtspUrl}]: {e.Data}"); };
                _proc.Exited += (s, e) => { _running = false; try { _queue.CompleteAdding(); } catch { } };
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
                // Drop oldest if queue is full to keep latency bounded
                while (!_queue.TryAdd(rgba))
                {
                    _queue.TryTake(out _);
                }
            }

            private void WriterLoop()
            {
                try
                {
                    using (var stdin = _proc.StandardInput.BaseStream)
                    {
                        foreach (var frame in _queue.GetConsumingEnumerable())
                        {
                            stdin.Write(frame, 0, frame.Length);
                            stdin.Flush();
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
                catch { }
                finally
                {
                    _proc?.Dispose();
                }
            }
        }

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
    }
}


