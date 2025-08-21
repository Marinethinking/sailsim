using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Unity.Collections;
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
        private readonly List<FfmpegPusher> _pushers = new List<FfmpegPusher>();
        private readonly List<float> _nextCaptureTime = new List<float>();

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
            _pushers.Clear();
            _nextCaptureTime.Clear();

            // Resolve ffmpeg absolute path if needed
            ffmpegPath = ResolveFfmpegPath(ffmpegPath);

            foreach (var sc in streams)
            {
                if (sc.camera == null) continue;

                var width = Mathf.Max(16, sc.width);
                var height = Mathf.Max(16, sc.height);

                // Use HDR RT so URP post-processing (e.g. Bloom) is applied correctly
                var rt = new RenderTexture(width, height, 0, RenderTextureFormat.DefaultHDR)
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
                // Do not disable HDR; enable it so post-processing can render
                sc.camera.allowHDR = true;
                sc.camera.allowMSAA = false;

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

                AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
                {
                    if (request.hasError) return;
                    var data = request.GetData<byte>();
                    var bytes = new byte[data.Length];
                    data.CopyTo(bytes);
                    pusher.EnqueueFrame(bytes);
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
            private readonly BlockingCollection<byte[]> _queue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
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
                // Minimal, robust filter chain: vertical flip (optional) and NV12 conversion
                var vfChain = _flipVertical ? "-vf vflip,format=nv12" : "-vf format=nv12";
                var args = $"-f rawvideo -pix_fmt rgba -s {_width}x{_height} -r {_fps} -i - {vfChain} -f rtsp -rtsp_transport tcp -c:v h264_videotoolbox -b:v {bitrate}k -g {_fps * 2} {_rtspUrl}";

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
                try
                {
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
                if (!_queue.IsAddingCompleted)
                {
                    _queue.Add(rgba);
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

        private static string ResolveFfmpegPath(string configured)
        {
            if (!string.IsNullOrEmpty(configured) && configured != "ffmpeg")
                return configured;

            // Common macOS locations when launched outside a login shell
            string[] candidates =
            {
                configured,
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/usr/bin/ffmpeg"
            };
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                try
                {
                    if (System.IO.File.Exists(c)) return c;
                }
                catch { }
            }
            return configured; // fallback; user should set absolute path in Inspector
        }
    }
}


