using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Nami
{
    [DisallowMultipleComponent]
    public class RadarSimulator : MonoBehaviour
    {
        [Header("Boat References")]
        public Vehicle vehicle;
        public Rigidbody boatRigidbody;
        [Tooltip("Optional: Radar mounting transform. If null, uses this transform.")]
        public Transform radarMount;
        [Tooltip("If true, reverses the radar forward direction (uses -Z instead of +Z)")]
        public bool reverseDirection = false;

        [Header("Radar Settings")]
        [Tooltip("Unique radar ID to identify this radar in UDP messages (e.g., 'front', 'back', max 16 chars)")]
        public string radarId = "radar";
        [Range(10, 30)] public int updateRateHz = 20;
        [Range(0.5f, 2.0f)] public float azimuthResolutionDeg = 1.0f;
        [Range(50f, 500f)] public float maxRangeM = 200f;
        [Tooltip("Azimuth field of view in degrees. 360 = full sweep, <360 = sector scan")]
        [Range(10f, 360f)] public float azimuthFovDeg = 120;
        [Tooltip("Minimum elevation angle (degrees). TI-style mmWave typically uses ~-15 to +15.")]
        public float minElevationDeg = -15f;
        [Tooltip("Maximum elevation angle (degrees). TI-style mmWave typically uses ~-15 to +15.")]
        public float maxElevationDeg = 15f;
        [Tooltip("Elevation beamwidth (degrees) used to place multiple vertical beams between min and max.")]
        public float elevationBeamwidthDeg = 10f;
        [Tooltip("Layers to raycast against")]
        public LayerMask raycastLayers = -1;

        [Header("Radar Parameters")]
        [Tooltip("Noise floor in dB")]
        public float noiseFloorDb = -90f;
        [Tooltip("Sea clutter level in dB (range-dependent)")]
        public float seaClutterDb = -80f;
        [Tooltip("CFAR threshold multiplier (higher = fewer false alarms, more missed detections)")]
        [Range(1.0f, 5.0f)] public float cfarThreshold = 2.0f;
        [Tooltip("CFAR guard cells (cells around target excluded from noise estimation)")]
        [Range(1, 5)] public int cfarGuardCells = 2;
        [Tooltip("CFAR training cells (cells used for noise estimation)")]
        [Range(4, 20)] public int cfarTrainingCells = 10;
        [Tooltip("Antenna gain pattern: 1.0 = uniform, higher = more directional")]
        [Range(0.5f, 2.0f)] public float antennaGain = 1.0f;

        [Header("Debug")]
        [Tooltip("Seconds between debug log messages (applies to all radar logs)")]
        public float logIntervalSec = 20.0f;
        [Tooltip("If true, send raw detections without CFAR filtering (for debugging)")]
        public bool bypassCFAR = false;

        private UdpClient _tx;
        private IPEndPoint _telemetryEp;
        private CancellationTokenSource _cts;

        // Debug / stats
        private long _totalMessagesSent = 0;
        private long _totalDetectionsSent = 0;
        private float _nextDebugLogTime = 0f;
        private float _nextDetectionLogTime = 0f;

        // Message type header (4 bytes)
        private static readonly byte[] MSG_RADAR = { (byte)'R', (byte)'A', (byte)'D', (byte)'R' };

        // Detection data structure
        private struct RadarDetection
        {
            public float range;
            public float azimuth;
            public float elevation;
            public float doppler;
            public float power;
        }

        // Pre-computed ray directions
        private List<Vector3> _rayDirections = new List<Vector3>();
        private bool _rayDirectionsDirty = true;

        private void OnEnable()
        {
            try
            {
                if (vehicle == null)
                {
                    vehicle = GetComponentInParent<Vehicle>();
                }
                if (boatRigidbody == null && vehicle != null)
                {
                    boatRigidbody = vehicle.engine != null ? vehicle.engine.RB : GetComponentInParent<Rigidbody>();
                }
                if (radarMount == null)
                {
                    radarMount = transform;
                }

                _cts = new CancellationTokenSource();
                SetupSockets();
                _rayDirectionsDirty = true;
                _ = RunTxLoop(_cts.Token);
                
                Debug.Log($"[RadarSimulator] Started: id={radarId}, telemetry={UdpPublisher.TelemetryMulticastAddress}:{UdpPublisher.TelemetryPort}, rate={updateRateHz}Hz");
            }
            catch (Exception e)
            {
                Debug.LogError($"[RadarSimulator] Failed to start: {e.Message}\n{e.StackTrace}");
                enabled = false;
            }
        }

        private void OnDisable()
        {
            try { _cts?.Cancel(); } catch { }
            try { _tx?.Dispose(); } catch { }
        }

        private void OnValidate()
        {
            // Mark ray directions as dirty when parameters change
            _rayDirectionsDirty = true;
        }

        private void SetupSockets()
        {
            _telemetryEp = UdpPublisher.TelemetryEndpoint;
            _tx = UdpPublisher.CreateTelemetrySender();
        }

        private async Task RunTxLoop(CancellationToken ct)
        {
            var period = Mathf.Max(1f / Mathf.Max(1, updateRateHz), 0.033f);
            var wait = TimeSpan.FromSeconds(period);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SendRadarData();
                }
                catch (Exception e)
                {
                    Debug.LogError($"Radar TX error: {e.Message}");
                }
                try { await Task.Delay(wait, ct); } catch { break; }
            }
        }

        private void SendRadarData()
        {
            if (boatRigidbody == null || radarMount == null) return;

            // Recompute ray directions if parameters changed
            if (_rayDirectionsDirty)
            {
                ComputeRayDirections();
                _rayDirectionsDirty = false;
            }

            // Perform raycasts
            var detections = PerformRaycasts();
            var rawCount = detections.Count;

            // Apply CFAR thresholding (unless bypassed)
            var filteredDetections = bypassCFAR ? detections : ApplyCFAR(detections);
            var filteredCount = filteredDetections.Count;

            var now = Time.time;
            if (now >= _nextDetectionLogTime)
            {
                _nextDetectionLogTime = now + Mathf.Max(0.1f, logIntervalSec);
                Debug.Log($"[RadarSimulator] detections: id={radarId}, raw={rawCount}, filtered={filteredCount}, maxRange={maxRangeM}, fovDeg={azimuthFovDeg}");
            }

            // Send detections over UDP
            SendDetectionMessage(filteredDetections);
        }

        private void ComputeRayDirections()
        {
            _rayDirections.Clear();

            var azimuthStart = -azimuthFovDeg * 0.5f;
            var azimuthEnd = azimuthFovDeg * 0.5f;
            var azimuthSteps = Mathf.CeilToInt(azimuthFovDeg / azimuthResolutionDeg);

            // Determine elevation angles
            var elevationAngles = new List<float>();
            if (Mathf.Abs(maxElevationDeg - minElevationDeg) < 0.1f)
            {
                // Single beam
                elevationAngles.Add((minElevationDeg + maxElevationDeg) * 0.5f * Mathf.Deg2Rad);
            }
            else
            {
                // Multi-beam
                var elevationSteps = Mathf.CeilToInt((maxElevationDeg - minElevationDeg) / elevationBeamwidthDeg);
                for (int i = 0; i <= elevationSteps; i++)
                {
                    var elev = Mathf.Lerp(minElevationDeg, maxElevationDeg, i / (float)elevationSteps);
                    elevationAngles.Add(elev * Mathf.Deg2Rad);
                }
            }

            // Generate ray directions in local radar frame (forward = +Z, up = +Y, right = +X)
            foreach (var elevRad in elevationAngles)
            {
                for (int i = 0; i <= azimuthSteps; i++)
                {
                    var azimDeg = Mathf.Lerp(azimuthStart, azimuthEnd, i / (float)azimuthSteps);
                    var azimRad = azimDeg * Mathf.Deg2Rad;

                    // Convert polar to Cartesian in local frame
                    var cosElev = Mathf.Cos(elevRad);
                    var dir = new Vector3(
                        Mathf.Sin(azimRad) * cosElev,
                        Mathf.Sin(elevRad),
                        Mathf.Cos(azimRad) * cosElev
                    );
                    _rayDirections.Add(dir);
                }
            }
        }

        private List<RadarDetection> PerformRaycasts()
        {
            var detections = new List<RadarDetection>();
            if (_rayDirections.Count == 0) return detections;

            var radarPos = radarMount.position;
            var radarRot = radarMount.rotation;
            var radarVel = boatRigidbody != null ? boatRigidbody.linearVelocity : Vector3.zero;

            // Prepare raycast commands
            var commands = new NativeArray<RaycastCommand>(_rayDirections.Count, Allocator.TempJob);
            var results = new NativeArray<RaycastHit>(_rayDirections.Count, Allocator.TempJob);

            for (int i = 0; i < _rayDirections.Count; i++)
            {
                var localDir = _rayDirections[i];
                // Apply 180° yaw (flip X and Z) if needed
                if (reverseDirection)
                {
                    localDir = new Vector3(-localDir.x, localDir.y, -localDir.z);
                }
                var worldDir = radarRot * localDir;
                commands[i] = new RaycastCommand(radarPos, worldDir, maxRangeM)
                {
                    layerMask = raycastLayers
                };
            }

            // Schedule batch raycasts
            var jobHandle = RaycastCommand.ScheduleBatch(commands, results, 1);
            jobHandle.Complete();

            // Process results
            for (int i = 0; i < results.Length; i++)
            {
                var hit = results[i];
                if (hit.collider == null) continue;

                var range = hit.distance;
                var localDir = _rayDirections[i];
                // Keep azimuth/elevation consistent with actual ray direction
                if (reverseDirection)
                {
                    localDir = new Vector3(-localDir.x, localDir.y, -localDir.z);
                }
                var worldDir = radarRot * localDir;

                // Compute azimuth and elevation from local direction
                var azimuth = Mathf.Atan2(localDir.x, localDir.z);
                var elevation = Mathf.Asin(localDir.y);

                // Compute Doppler (radial velocity)
                var targetVel = Vector3.zero;
                var targetRb = hit.rigidbody;
                if (targetRb != null)
                {
                    targetVel = targetRb.linearVelocity;
                }
                var relativeVel = targetVel - radarVel;
                var doppler = Vector3.Dot(relativeVel, worldDir);

                // Estimate power/SNR
                var power = EstimatePower(hit, range, worldDir, localDir);

                detections.Add(new RadarDetection
                {
                    range = range,
                    azimuth = azimuth,
                    elevation = elevation,
                    doppler = doppler,
                    power = power
                });
            }

            commands.Dispose();
            results.Dispose();

            return detections;
        }

        private float EstimatePower(RaycastHit hit, float range, Vector3 worldDir, Vector3 localDir)
        {
            // Base RCS (simplified model based on object size/material)
            float rcs = 1.0f; // Default RCS in m²
            var collider = hit.collider;
            if (collider != null)
            {
                // Estimate RCS from collider bounds
                var bounds = collider.bounds;
                var size = bounds.size;
                rcs = Mathf.Max(size.x * size.y, size.x * size.z, size.y * size.z); // Approximate cross-section

                // Tag-based RCS multiplier (can be extended)
                // NOTE: we intentionally avoid CompareTag() here to prevent
                // Unity errors when tags like \"Boat\" / \"Vehicle\" / \"Buoy\" / \"Marker\"
                // are not defined in the project's Tag Manager.
                var tag = collider.tag;
                if (tag == "Boat" || tag == "Vehicle")
                {
                    rcs *= 10.0f; // Larger targets
                }
                else if (tag == "Buoy" || tag == "Marker")
                {
                    rcs *= 2.0f; // Medium targets
                }
            }

            // Range-dependent attenuation (1/R^4)
            var rangeAttenuation = 1.0f / (range * range * range * range);
            var rangeLossDb = -20.0f * Mathf.Log10(range + 0.1f); // Approximate

            // Antenna pattern gain (simplified: higher gain for forward direction)
            var forwardGain = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(localDir, Vector3.forward)), antennaGain);
            var antennaGainDb = 20.0f * Mathf.Log10(forwardGain + 0.01f);

            // Sea clutter (range-dependent)
            var clutterDb = seaClutterDb - 20.0f * Mathf.Log10(range + 1.0f);

            // Total power estimate
            var powerDb = 10.0f * Mathf.Log10(rcs * rangeAttenuation + 1e-10f) + antennaGainDb + rangeLossDb;

            // Add noise
            var noiseDb = noiseFloorDb + GaussianNoise(2.0f); // 2dB noise std dev
            var totalPowerDb = 10.0f * Mathf.Log10(Mathf.Pow(10f, powerDb / 10f) + Mathf.Pow(10f, noiseDb / 10f) + Mathf.Pow(10f, clutterDb / 10f));

            return totalPowerDb;
        }

        private List<RadarDetection> ApplyCFAR(List<RadarDetection> detections)
        {
            if (detections.Count == 0) return detections;

            // Sort detections by range for CFAR processing
            detections.Sort((a, b) => a.range.CompareTo(b.range));

            // Group detections into range bins for CFAR
            var rangeBinSize = maxRangeM / 100f; // 100 range bins
            var bins = new Dictionary<int, List<float>>();

            foreach (var det in detections)
            {
                var binIdx = Mathf.FloorToInt(det.range / rangeBinSize);
                if (!bins.ContainsKey(binIdx))
                {
                    bins[binIdx] = new List<float>();
                }
                bins[binIdx].Add(det.power);
            }

            // Apply CA-CFAR (Cell-Averaging CFAR)
            var filtered = new List<RadarDetection>();
            foreach (var det in detections)
            {
                var binIdx = Mathf.FloorToInt(det.range / rangeBinSize);

                // Estimate noise from training cells
                float noiseEstimate = noiseFloorDb;
                int trainingCount = 0;

                for (int i = binIdx - cfarTrainingCells - cfarGuardCells; i <= binIdx + cfarTrainingCells + cfarGuardCells; i++)
                {
                    if (i == binIdx) continue; // Skip target cell
                    if (Mathf.Abs(i - binIdx) <= cfarGuardCells) continue; // Skip guard cells

                    if (bins.ContainsKey(i) && bins[i].Count > 0)
                    {
                        foreach (var power in bins[i])
                        {
                            noiseEstimate += power;
                            trainingCount++;
                        }
                    }
                }

                if (trainingCount > 0)
                {
                    noiseEstimate /= trainingCount;
                }

                // Threshold check
                var threshold = noiseEstimate + (cfarThreshold * 3.0f); // 3dB per threshold unit
                if (det.power > threshold)
                {
                    filtered.Add(det);
                }
            }

            return filtered;
        }

        private void SendDetectionMessage(List<RadarDetection> detections)
        {
            // Message format: [4-byte header][16-byte radar ID string][8-byte timestamp][2-byte count][N*20-byte detections]
            // Detection: range(float), azimuth(float), elevation(float), doppler(float), power(float) = 20 bytes
            var timestamp = (long)(Time.time * 1000000); // Unix microseconds
            var count = (ushort)Mathf.Min(detections.Count, 65535);
            var messageSize = 30 + (count * 20);
            var buffer = ArrayPool<byte>.Shared.Rent(messageSize);
            try
            {
                int offset = 0;

                // Header
                Array.Copy(MSG_RADAR, 0, buffer, offset, 4);
                offset += 4;

                // Radar ID (16 bytes, UTF-8 encoded, null-padded)
                var idBytes = System.Text.Encoding.UTF8.GetBytes(radarId ?? "radar");
                var idLen = Mathf.Min(idBytes.Length, 16);
                Array.Copy(idBytes, 0, buffer, offset, idLen);
                // Zero-fill remaining bytes
                for (int i = idLen; i < 16; i++)
                {
                    buffer[offset + i] = 0;
                }
                offset += 16;

                // Timestamp (8 bytes, big-endian)
                EndianBitConverter.WriteInt64BE(buffer, offset, timestamp);
                offset += 8;

                // Detection count (2 bytes, big-endian)
                EndianBitConverter.WriteUInt16BE(buffer, offset, count);
                offset += 2;

                // Detections (20 bytes each: range, azimuth, elevation, doppler, power)
                for (int i = 0; i < count; i++)
                {
                    var det = detections[i];
                    EndianBitConverter.WriteFloatBE(buffer, offset, det.range); offset += 4;
                    EndianBitConverter.WriteFloatBE(buffer, offset, det.azimuth); offset += 4;
                    EndianBitConverter.WriteFloatBE(buffer, offset, det.elevation); offset += 4;
                    EndianBitConverter.WriteFloatBE(buffer, offset, det.doppler); offset += 4;
                    EndianBitConverter.WriteFloatBE(buffer, offset, det.power); offset += 4;
                }

                SendFrame(buffer, messageSize);

                // Update stats and emit throttled debug log
                _totalMessagesSent++;
                _totalDetectionsSent += count;

                var now = Time.time;
                if (now >= _nextDebugLogTime)
                {
                    _nextDebugLogTime = now + Mathf.Max(0.1f, logIntervalSec);
                    Debug.Log($"[RadarSimulator] TX radar: id={radarId}, ts_us={timestamp}, detections_in_msg={count}, total_msgs={_totalMessagesSent}, total_points={_totalDetectionsSent}");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void SendFrame(byte[] frame, int length)
        {
            try { _tx.Send(frame, length, _telemetryEp); }
            catch (Exception e) { Debug.LogError($"Radar UDP send failed: {e.Message}"); }
        }

        private static float GaussianNoise(float stdDev)
        {
            // Box-Muller transform for Gaussian noise
            float u1 = 1.0f - UnityEngine.Random.value;
            float u2 = 1.0f - UnityEngine.Random.value;
            float z = Mathf.Sqrt(-2.0f * Mathf.Log(u1)) * Mathf.Sin(2.0f * Mathf.PI * u2);
            return z * stdDev;
        }

        // Debug visualization
        private void OnDrawGizmosSelected()
        {
            if (radarMount == null) return;

            Gizmos.color = Color.yellow;
            var pos = radarMount.position;
            var rot = radarMount.rotation;

            // Draw FOV cone
            var fovRad = azimuthFovDeg * 0.5f * Mathf.Deg2Rad;
            var forwardDir = reverseDirection ? Vector3.back : Vector3.forward;
            var forward = rot * forwardDir;
            var right = rot * Vector3.right;
            var up = rot * Vector3.up;

            // Draw forward direction
            Gizmos.DrawRay(pos, forward * maxRangeM * 0.1f);

            // Draw FOV boundaries (honor 180° yaw flip when reverseDirection is true)
            var leftLocal = new Vector3(Mathf.Sin(-fovRad), 0, Mathf.Cos(-fovRad));
            var rightLocal = new Vector3(Mathf.Sin(fovRad), 0, Mathf.Cos(fovRad));
            if (reverseDirection)
            {
                leftLocal = new Vector3(-leftLocal.x, 0, -leftLocal.z);
                rightLocal = new Vector3(-rightLocal.x, 0, -rightLocal.z);
            }
            var leftBound = rot * leftLocal;
            var rightBound = rot * rightLocal;
            Gizmos.DrawRay(pos, leftBound * maxRangeM);
            Gizmos.DrawRay(pos, rightBound * maxRangeM);
        }
    }
}

