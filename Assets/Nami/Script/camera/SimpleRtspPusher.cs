using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace Nami
{
    /// <summary>
    /// Pure GStreamer RTSP pusher for low-latency video streaming.
    /// Receives raw RGBA frames from Unity and pushes H.264 encoded video to an RTSP server.
    /// Requires: gstreamer1.0-plugins-bad, gstreamer1.0-plugins-good, gstreamer1.0-plugins-ugly
    /// For NVIDIA: gstreamer1.0-plugins-bad (nvh264enc)
    /// </summary>
    public class SimpleRtspPusher : IDisposable
    {
        private readonly string _gstLaunchPath;
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;
        private readonly int _bitrateKbps;
        private readonly string _rtspUrl;
        private readonly bool _flipVertical;
        private readonly string _forceEncoder;
        
        private Process _proc;
        private string _selectedEncoder;
        private Thread _writerThread;
        private readonly BlockingCollection<Tuple<byte[], ulong>> _queue = new BlockingCollection<Tuple<byte[], ulong>>(new ConcurrentQueue<Tuple<byte[], ulong>>(), 2);
        private volatile bool _running;
        
        // Encoding metrics
        private volatile int _encodedFrames = 0;
        private volatile float _encodingFps = 0;
        private volatile float _encodingSpeed = 0;
        private volatile int _queueSize = 0;
        private int _lastFrameCount = 0;
        private DateTime _lastFpsUpdate;

        public bool IsRunning => _running && _proc != null && !_proc.HasExited;
        public int EncodedFrames => _encodedFrames;
        public float EncodingFps => _encodingFps;
        public float EncodingSpeed => _encodingSpeed;
        public int QueueSize => _queueSize;

        public SimpleRtspPusher(string gstPath, int width, int height, int fps, int bitrateKbps, string rtspUrl, bool flipVertical = true, string forceEncoder = "")
        {
            _gstLaunchPath = ResolveGstLaunchPath(gstPath);
            _width = width;
            _height = height;
            _fps = fps;
            _bitrateKbps = bitrateKbps;
            _rtspUrl = rtspUrl;
            _flipVertical = flipVertical;
            _forceEncoder = forceEncoder;
        }

        private string ResolveGstLaunchPath(string configured)
        {
            // If user specified gst-launch-1.0 directly, use it
            if (!string.IsNullOrEmpty(configured) && configured.Contains("gst-launch"))
            {
                return configured;
            }

            // Try common locations
            string[] candidates = {
                "/usr/bin/gst-launch-1.0",
                "/usr/local/bin/gst-launch-1.0",
                "/opt/homebrew/bin/gst-launch-1.0",
                "gst-launch-1.0"
            };

            foreach (var c in candidates)
            {
                try { if (System.IO.File.Exists(c)) return c; } catch { }
            }

            // Try which command
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/which",
                    Arguments = "gst-launch-1.0",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    var path = proc.StandardOutput.ReadLine();
                    proc.WaitForExit(500);
                    if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                        return path;
                }
            }
            catch { }

            return "gst-launch-1.0";
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _lastFpsUpdate = DateTime.Now;

            // Detect best encoder
            _selectedEncoder = DetectBestEncoder();
            
            // Start GStreamer pipeline
            StartGStreamer();
            
            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "GstWriter" };
            _writerThread.Start();
        }

        public void EnqueueFrame(byte[] rgba, ulong frameId, double timestamp)
        {
            if (!_running) return;
            if (_queue.IsAddingCompleted) return;
            
            // Keep only latest frame (drop old frames for low latency)
            while (_queue.Count > 0) _queue.TryTake(out _);
            _queue.TryAdd(new Tuple<byte[], ulong>(rgba, frameId));
        }

        private string DetectBestEncoder()
        {
            // If user forced an encoder, use it
            if (!string.IsNullOrEmpty(_forceEncoder))
            {
                UnityEngine.Debug.Log($"[SimpleRtspPusher] Using forced encoder: {_forceEncoder}");
                return _forceEncoder;
            }

            // Try hardware encoders in order of preference (GStreamer element names)
            string[] hwEncoders = { "nvh264enc", "vaapih264enc", "vtenc_h264" };
            
            foreach (var encoder in hwEncoders)
            {
                if (TestGstEncoder(encoder))
                {
                    UnityEngine.Debug.Log($"[SimpleRtspPusher] Hardware encoder detected: {encoder}");
                    return encoder;
                }
            }

            // Fallback to software encoder
            UnityEngine.Debug.Log($"[SimpleRtspPusher] No hardware encoder available, using x264enc (software)");
            return "x264enc";
        }

        private bool TestGstEncoder(string encoder)
        {
            try
            {
                // Test if the GStreamer element exists and works
                var testPipeline = $"videotestsrc num-buffers=3 ! video/x-raw,width=256,height=256,framerate=30/1 ! videoconvert ! {encoder} ! fakesink";

                var testProc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _gstLaunchPath,
                        Arguments = testPipeline,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                testProc.Start();
                testProc.WaitForExit(5000);
                
                if (!testProc.HasExited)
                {
                    testProc.Kill();
                    return false;
                }
                
                return testProc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private void StartGStreamer()
        {
            // Build encoder element with low-latency settings
            string encoderElement;
            string flipElement = _flipVertical ? "videoflip method=vertical-flip ! " : "";
            
            if (_selectedEncoder == "nvh264enc")
            {
                // NVIDIA NVENC - ultra low latency
                encoderElement = $"nvh264enc preset=low-latency-hq rc-mode=cbr bitrate={_bitrateKbps} zerolatency=true gop-size={_fps}";
            }
            else if (_selectedEncoder == "vaapih264enc")
            {
                // Intel/AMD VAAPI
                encoderElement = $"vaapih264enc rate-control=cbr bitrate={_bitrateKbps} keyframe-period={_fps}";
            }
            else if (_selectedEncoder == "vtenc_h264")
            {
                // Apple VideoToolbox (macOS)
                encoderElement = $"vtenc_h264 bitrate={_bitrateKbps} realtime=true max-keyframe-interval={_fps}";
            }
            else
            {
                // x264enc software encoder - ultra low latency
                encoderElement = $"x264enc tune=zerolatency speed-preset=ultrafast bitrate={_bitrateKbps} key-int-max={_fps}";
            }

            // Pure GStreamer pipeline
            // fdsrc: reads raw RGBA from stdin
            // rawvideoparse: parses raw video
            // queue: buffer management with frame dropping for low latency
            // videoconvert: color space conversion
            // encoder: H.264 encoding (NVENC or software)
            // h264parse: ensures proper NAL unit formatting
            // rtspclientsink: pushes to RTSP server (MediaMTX)
            var pipeline = 
                $"fdsrc fd=0 ! " +
                $"rawvideoparse format=rgba width={_width} height={_height} framerate={_fps}/1 ! " +
                $"queue max-size-buffers=1 leaky=downstream ! " +
                $"{flipElement}" +
                $"videoconvert ! video/x-raw,format=I420 ! " +
                $"queue max-size-buffers=1 leaky=downstream ! " +
                $"{encoderElement} ! " +
                $"h264parse config-interval=-1 ! " +
                $"rtspclientsink location={_rtspUrl} protocols=tcp latency=0";

            UnityEngine.Debug.Log($"[SimpleRtspPusher] Starting GStreamer pipeline: {_selectedEncoder}");

            _proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _gstLaunchPath,
                    Arguments = pipeline,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            _proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    var data = e.Data;
                    var dataLower = data.ToLower();
                    
                    // Log errors, warnings, and state changes
                    if (dataLower.Contains("error") || 
                        dataLower.Contains("warning") ||
                        dataLower.Contains("critical") ||
                        dataLower.Contains("playing") ||
                        dataLower.Contains("paused") ||
                        dataLower.Contains("rtspclientsink"))
                    {
                        UnityEngine.Debug.Log($"[SimpleRtspPusher] gst: {data}");
                    }
                }
            };

            _proc.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    // Log state changes
                    if (e.Data.Contains("PLAYING") || e.Data.Contains("Setting pipeline"))
                    {
                        UnityEngine.Debug.Log($"[SimpleRtspPusher] gst: {e.Data}");
                    }
                }
            };

            try
            {
                _proc.Start();
                _proc.BeginErrorReadLine();
                _proc.BeginOutputReadLine();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[SimpleRtspPusher] Failed to start GStreamer: {ex.Message}");
                _running = false;
            }
        }

        private void WriterLoop()
        {
            try
            {
                using (var stdin = _proc.StandardInput.BaseStream)
                {
                    foreach (var item in _queue.GetConsumingEnumerable())
                    {
                        if (!_running) break;
                        
                        _queueSize = _queue.Count;
                        stdin.Write(item.Item1, 0, item.Item1.Length);
                        stdin.Flush();
                        
                        _encodedFrames++;
                        
                        // Update FPS calculation every second
                        var now = DateTime.Now;
                        var elapsed = (now - _lastFpsUpdate).TotalSeconds;
                        if (elapsed >= 1.0)
                        {
                            var framesDelta = _encodedFrames - _lastFrameCount;
                            _encodingFps = (float)(framesDelta / elapsed);
                            _encodingSpeed = _encodingFps / _fps;
                            _lastFrameCount = _encodedFrames;
                            _lastFpsUpdate = now;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    UnityEngine.Debug.LogError($"[SimpleRtspPusher] Writer error: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _queue.CompleteAdding(); } catch { }
            try { _writerThread?.Join(500); } catch { }
            try 
            { 
                if (_proc != null && !_proc.HasExited)
                {
                    // Close stdin to send EOS
                    try { _proc.StandardInput.Close(); } catch { }
                    _proc.WaitForExit(1000);
                    if (!_proc.HasExited) _proc.Kill();
                }
            } 
            catch { }
            try { _proc?.Dispose(); } catch { }
        }
    }
}
