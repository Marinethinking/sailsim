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
    /// <summary>
    /// A multi-sensor simulator that manages multiple radars and IMUs from a single component.
    /// This allows all sensors to share parameters for easy tuning while maintaining individual IDs and mounts.
    /// </summary>
    [DisallowMultipleComponent]
    public class SensorArraySimulator : MonoBehaviour
    {
        [Serializable]
        public struct RadarMount
        {
            public string id;
            public Transform mount;
        }

        [Serializable]
        public struct ImuMount
        {
            public string id;
            public Transform mount;
        }

        [Header("Boat References")]
        public Vehicle vehicle;
        public Rigidbody boatRigidbody;

        [Header("Sensor Array")]
        [Tooltip("List of radar mounts and their unique IDs.")]
        public List<RadarMount> radars = new List<RadarMount>();
        [Tooltip("List of IMU mounts and their unique IDs.")]
        public List<ImuMount> imus = new List<ImuMount>();

        [Header("Radar Settings (Shared)")]
        [Range(10, 30)] public int radarUpdateRateHz = 20;
        [Range(0.5f, 2.0f)] public float azimuthResolutionDeg = 1.0f;
        [Range(50f, 500f)] public float maxRangeM = 200f;
        [Tooltip("Azimuth field of view in degrees. 360 = full sweep, <360 = sector scan")]
        [Range(10f, 360f)] public float azimuthFovDeg = 120;
        public float minElevationDeg = -15f;
        public float maxElevationDeg = 15f;
        [Tooltip("Elevation beamwidth (degrees) used to place multiple vertical beams between min and max.")]
        public float elevationBeamwidthDeg = 10f;
        [Tooltip("Layers to raycast against")]
        public LayerMask raycastLayers = -1;

        [Header("Radar Processing (Shared)")]
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

        [Header("IMU Settings (Shared)")]
        [Range(50, 400)] public int imuUpdateRateHz = 200;
        [Tooltip("Enable raw sensor output (gyro/accel/mag)")]
        public bool enableRawOutput = true;
        [Tooltip("Enable fused attitude output (roll/pitch/yaw)")]
        public bool enableAttitudeOutput = true;

        [Header("IMU Noise (Shared)")]
        [Tooltip("Gyroscope noise standard deviation (rad/s per axis)")]
        public Vector3 gyroNoiseStdDev = new Vector3(0.01f, 0.01f, 0.01f);
        [Tooltip("Accelerometer noise standard deviation (m/s² per axis)")]
        public Vector3 accelNoiseStdDev = new Vector3(0.1f, 0.1f, 0.1f);
        [Tooltip("Magnetometer noise standard deviation (µT per axis)")]
        public Vector3 magNoiseStdDev = new Vector3(0.1f, 0.1f, 0.1f);
        [Tooltip("Gyroscope bias (rad/s per axis). Bias drift is simulated as slow random walk.")]
        public Vector3 gyroBias = Vector3.zero;
        [Tooltip("Accelerometer bias (m/s² per axis)")]
        public Vector3 accelBias = Vector3.zero;
        [Tooltip("Magnetometer bias (µT per axis)")]
        public Vector3 magBias = Vector3.zero;
        [Tooltip("Bias drift rate (how fast bias changes, 0-1)")]
        [Range(0f, 1f)] public float biasDriftRate = 0.0001f;
        [Tooltip("Earth magnetic field vector in world frame (µT).")]
        public Vector3 earthMagneticField = new Vector3(0, 0, -50000f);

        [Header("Debug")]
        [Tooltip("Seconds between debug log messages")]
        public float logIntervalSec = 20.0f;
        [Tooltip("If true, send raw detections without CFAR filtering")]
        public bool bypassCFAR = false;
        [Tooltip("Print periodic IMU debug lines to the Unity Console.")]
        public bool logImuDebug = false;

        private UdpClient _tx;
        private IPEndPoint _telemetryEp;
        private CancellationTokenSource _cts;

        // Message type headers
        private static readonly byte[] MSG_RADAR = { (byte)'R', (byte)'A', (byte)'D', (byte)'R' };
        private static readonly byte[] MSG_IMU_RAW = { (byte)'I', (byte)'M', (byte)'U', (byte)'R' };
        private static readonly byte[] MSG_IMU_ATT = { (byte)'I', (byte)'M', (byte)'U', (byte)'A' };

        // Radar internal state
        private List<Vector3> _rayDirections = new List<Vector3>();
        private bool _rayDirectionsDirty = true;
        private uint _radarFrameCounter = 0;
        private float _nextRadarLogTime = 0f;

        // IMU internal state
        private struct ImuState
        {
            public Vector3 currentGyroBias;
            public Vector3 currentAccelBias;
            public Vector3 currentMagBias;
            public Vector3 prevVelocity;
            public float prevTime;
            public byte[] rawBuffer;
            public byte[] attBuffer;
        }
        private Dictionary<string, ImuState> _imuStates = new Dictionary<string, ImuState>();

        private struct RadarDetection
        {
            public float range;
            public float azimuth;
            public float elevation;
            public float doppler;
            public float power;
        }

        private void OnEnable()
        {
            try
            {
                if (vehicle == null) vehicle = GetComponentInParent<Vehicle>();
                if (boatRigidbody == null && vehicle != null)
                {
                    boatRigidbody = vehicle.engine != null ? vehicle.engine.RB : GetComponentInParent<Rigidbody>();
                }

                InitializeImuStates();
                _rayDirectionsDirty = true;
                _cts = new CancellationTokenSource();
                SetupSockets();

                _ = RunRadarLoop(_cts.Token);
                _ = RunImuLoop(_cts.Token);

                Debug.Log($"[SensorArraySimulator] Started with {radars.Count} radars and {imus.Count} IMUs.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SensorArraySimulator] Failed to start: {e.Message}\n{e.StackTrace}");
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
            _rayDirectionsDirty = true;
        }

        private void InitializeImuStates()
        {
            _imuStates.Clear();
            foreach (var imu in imus)
            {
                if (string.IsNullOrEmpty(imu.id)) continue;
                _imuStates[imu.id] = new ImuState
                {
                    currentGyroBias = gyroBias,
                    currentAccelBias = accelBias,
                    currentMagBias = magBias,
                    prevVelocity = boatRigidbody != null ? boatRigidbody.linearVelocity : Vector3.zero,
                    prevTime = Time.time,
                    rawBuffer = new byte[64], // Header(4) + ID(16) + TS(8) + Payload(36)
                    attBuffer = new byte[40]  // Header(4) + ID(16) + TS(8) + Payload(12)
                };
            }
        }

        private void SetupSockets()
        {
            _telemetryEp = UdpPublisher.TelemetryEndpoint;
            _tx = UdpPublisher.CreateTelemetrySender();
        }

        private async Task RunRadarLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var period = Mathf.Max(1f / Mathf.Max(1, radarUpdateRateHz), 0.033f);
                try { SendRadarData(); }
                catch (Exception e) { Debug.LogError($"[SensorArraySimulator] Radar loop error: {e.Message}"); }
                try { await Task.Delay(TimeSpan.FromSeconds(period), ct); } catch { break; }
            }
        }

        private async Task RunImuLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var period = Mathf.Max(1f / Mathf.Max(1, imuUpdateRateHz), 0.0025f);
                try { SendImuData(); }
                catch (Exception e) { Debug.LogError($"[SensorArraySimulator] IMU loop error: {e.Message}"); }
                try { await Task.Delay(TimeSpan.FromSeconds(period), ct); } catch { break; }
            }
        }

        private void SendRadarData()
        {
            if (boatRigidbody == null || radars.Count == 0) return;

            if (_rayDirectionsDirty)
            {
                ComputeRayDirections();
                _rayDirectionsDirty = false;
            }

            int raysPerRadar = _rayDirections.Count;
            int totalRays = raysPerRadar * radars.Count;
            if (totalRays == 0) return;

            var commands = new NativeArray<RaycastCommand>(totalRays, Allocator.TempJob);
            var results = new NativeArray<RaycastHit>(totalRays, Allocator.TempJob);

            // Prepare batched raycasts for all radars
            for (int r = 0; r < radars.Count; r++)
            {
                var mount = radars[r].mount;
                if (mount == null) continue;
                var pos = mount.position;
                var rot = mount.rotation;

                for (int i = 0; i < raysPerRadar; i++)
                {
                    int idx = r * raysPerRadar + i;
                    commands[idx] = new RaycastCommand(pos, rot * _rayDirections[i], maxRangeM)
                    {
                        layerMask = raycastLayers
                    };
                }
            }

            // Schedule and wait for all raycasts in one go
            RaycastCommand.ScheduleBatch(commands, results, 1).Complete();

            var radarVel = boatRigidbody.linearVelocity;
            var frameId = _radarFrameCounter++;
            var timestamp = (long)(Time.time * 1000000);

            // Process results for each radar
            for (int r = 0; r < radars.Count; r++)
            {
                var mount = radars[r].mount;
                if (mount == null) continue;
                var rot = mount.rotation;
                var detections = new List<RadarDetection>();

                for (int i = 0; i < raysPerRadar; i++)
                {
                    int idx = r * raysPerRadar + i;
                    var hit = results[idx];
                    if (hit.collider == null) continue;

                    var localDir = _rayDirections[i];
                    var worldDir = rot * localDir;
                    var targetVel = hit.rigidbody != null ? hit.rigidbody.linearVelocity : Vector3.zero;
                    var doppler = Vector3.Dot(targetVel - radarVel, worldDir);
                    var power = EstimatePower(hit, hit.distance, worldDir, localDir);

                    detections.Add(new RadarDetection
                    {
                        range = hit.distance,
                        azimuth = Mathf.Atan2(localDir.x, localDir.z),
                        elevation = Mathf.Asin(localDir.y),
                        doppler = doppler,
                        power = power
                    });
                }

                var filtered = bypassCFAR ? detections : ApplyCFAR(detections);
                SendDetectionMessage(radars[r].id, filtered, frameId, timestamp);
            }

            commands.Dispose();
            results.Dispose();

            // Only update the timer AFTER all individual radar logs have had a chance to print
            if (Time.time >= _nextRadarLogTime)
            {
                Debug.Log($"[SensorArraySimulator] Radar sweep complete for {radars.Count} radars.");
                _nextRadarLogTime = Time.time + logIntervalSec;
            }
        }

        private void ComputeRayDirections()
        {
            _rayDirections.Clear();
            var azSteps = Mathf.CeilToInt(azimuthFovDeg / azimuthResolutionDeg);
            var elSteps = Mathf.Abs(maxElevationDeg - minElevationDeg) < 0.1f ? 0 : Mathf.CeilToInt((maxElevationDeg - minElevationDeg) / elevationBeamwidthDeg);

            for (int j = 0; j <= elSteps; j++)
            {
                var elev = elSteps == 0 ? (minElevationDeg + maxElevationDeg) * 0.5f : Mathf.Lerp(minElevationDeg, maxElevationDeg, (float)j / elSteps);
                var elevRad = elev * Mathf.Deg2Rad;
                var cosElev = Mathf.Cos(elevRad);
                var sinElev = Mathf.Sin(elevRad);

                for (int i = 0; i <= azSteps; i++)
                {
                    var azim = Mathf.Lerp(-azimuthFovDeg * 0.5f, azimuthFovDeg * 0.5f, (float)i / azSteps);
                    var azimRad = azim * Mathf.Deg2Rad;
                    _rayDirections.Add(new Vector3(Mathf.Sin(azimRad) * cosElev, sinElev, Mathf.Cos(azimRad) * cosElev));
                }
            }
        }

        private float EstimatePower(RaycastHit hit, float range, Vector3 worldDir, Vector3 localDir)
        {
            float rcs = 1.0f;
            if (hit.collider != null)
            {
                var size = hit.collider.bounds.size;
                rcs = Mathf.Max(size.x * size.y, size.x * size.z, size.y * size.z);
                var tag = hit.collider.tag;
                if (tag == "Boat" || tag == "Vehicle") rcs *= 10f;
                else if (tag == "Buoy" || tag == "Marker") rcs *= 2f;
            }
            var rangeAtten = 1.0f / (range * range * range * range + 0.1f);
            var rangeLossDb = -20f * Mathf.Log10(range + 0.1f);
            var antGain = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(localDir, Vector3.forward)), antennaGain);
            var antGainDb = 20f * Mathf.Log10(antGain + 0.01f);
            var clutterDb = seaClutterDb - 20f * Mathf.Log10(range + 1f);
            var powerDb = 10f * Mathf.Log10(rcs * rangeAtten + 1e-10f) + antGainDb + rangeLossDb;
            var noiseDb = noiseFloorDb + GaussianNoise(2.0f);
            return 10f * Mathf.Log10(Mathf.Pow(10, powerDb / 10) + Mathf.Pow(10, noiseDb / 10) + Mathf.Pow(10, clutterDb / 10));
        }

        private List<RadarDetection> ApplyCFAR(List<RadarDetection> detections)
        {
            if (detections.Count == 0) return detections;
            detections.Sort((a, b) => a.range.CompareTo(b.range));
            var binSize = maxRangeM / 100f;
            var bins = new Dictionary<int, List<float>>();
            foreach (var d in detections)
            {
                int b = Mathf.FloorToInt(d.range / binSize);
                if (!bins.ContainsKey(b)) bins[b] = new List<float>();
                bins[b].Add(d.power);
            }
            var filtered = new List<RadarDetection>();
            foreach (var d in detections)
            {
                int b = Mathf.FloorToInt(d.range / binSize);
                float noise = noiseFloorDb;
                int count = 0;
                for (int i = b - cfarTrainingCells - cfarGuardCells; i <= b + cfarTrainingCells + cfarGuardCells; i++)
                {
                    if (Mathf.Abs(i - b) <= cfarGuardCells) continue;
                    if (bins.TryGetValue(i, out var pList)) { foreach (var p in pList) { noise += p; count++; } }
                }
                if (count > 0) noise /= count;
                if (d.power > noise + cfarThreshold * 3.0f) filtered.Add(d);
            }
            return filtered;
        }

        private void SendDetectionMessage(string id, List<RadarDetection> detections, uint frameId, long timestamp)
        {
            const int MAX_PER_PACKET = 68;
            int total = detections.Count;

            // Throttled log for this specific radar
            if (Time.time >= _nextRadarLogTime)
            {
                Debug.Log($"[SensorArraySimulator] Radar '{id}' sent: {total} detections, frame={frameId}");
            }

            int packets = total == 0 ? 1 : Mathf.CeilToInt((float)total / MAX_PER_PACKET);
            for (int i = 0; i < packets; i++)
            {
                int offset = i * MAX_PER_PACKET;
                int count = Mathf.Min(total - offset, MAX_PER_PACKET);
                if (count < 0) count = 0;
                SendDetectionPacket(id, detections, offset, count, frameId, timestamp, i, packets);
            }
        }

        private void SendDetectionPacket(string id, List<RadarDetection> detections, int start, int count, uint frameId, long ts, int seq, int totalPkts)
        {
            int size = 38 + count * 20;
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                int o = 0;
                Array.Copy(MSG_RADAR, 0, buffer, o, 4); o += 4;
                var idB = System.Text.Encoding.UTF8.GetBytes(id ?? "radar");
                Array.Copy(idB, 0, buffer, o, Mathf.Min(idB.Length, 16));
                for (int i = idB.Length; i < 16; i++) buffer[o + i] = 0;
                o += 16;
                EndianBitConverter.WriteUInt32BE(buffer, o, frameId); o += 4;
                EndianBitConverter.WriteInt64BE(buffer, o, ts); o += 8;
                EndianBitConverter.WriteUInt16BE(buffer, o, (ushort)seq); o += 2;
                EndianBitConverter.WriteUInt16BE(buffer, o, (ushort)totalPkts); o += 2;
                EndianBitConverter.WriteUInt16BE(buffer, o, (ushort)count); o += 2;
                for (int i = 0; i < count; i++)
                {
                    var d = detections[start + i];
                    EndianBitConverter.WriteFloatBE(buffer, o, d.range); o += 4;
                    EndianBitConverter.WriteFloatBE(buffer, o, d.azimuth); o += 4;
                    EndianBitConverter.WriteFloatBE(buffer, o, d.elevation); o += 4;
                    EndianBitConverter.WriteFloatBE(buffer, o, d.doppler); o += 4;
                    EndianBitConverter.WriteFloatBE(buffer, o, d.power); o += 4;
                }
                _tx.Send(buffer, size, _telemetryEp);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        private void SendImuData()
        {
            if (boatRigidbody == null || imus.Count == 0) return;
            var now = Time.time;
            var worldAngVel = boatRigidbody.angularVelocity;
            var worldVel = boatRigidbody.linearVelocity;
            var worldGrav = Physics.gravity;

            foreach (var imu in imus)
            {
                if (imu.mount == null || !_imuStates.TryGetValue(imu.id, out var state)) continue;
                var dt = now - state.prevTime;
                if (dt <= 0) dt = 0.001f;

                var accel = (worldVel - state.prevVelocity) / dt;
                var bodyAngVel = imu.mount.InverseTransformDirection(worldAngVel);
                var bodyAccel = imu.mount.InverseTransformDirection(accel - worldGrav);
                var bodyMag = imu.mount.InverseTransformDirection(earthMagneticField);

                var newState = state;
                newState.currentGyroBias += AddNoise(new Vector3(biasDriftRate, biasDriftRate, biasDriftRate)) * dt;
                newState.currentAccelBias += AddNoise(new Vector3(biasDriftRate, biasDriftRate, biasDriftRate)) * dt;
                newState.currentMagBias += AddNoise(new Vector3(biasDriftRate, biasDriftRate, biasDriftRate)) * dt;
                
                var gyroOut = bodyAngVel + newState.currentGyroBias + AddNoise(gyroNoiseStdDev);
                var accelOut = bodyAccel + newState.currentAccelBias + AddNoise(accelNoiseStdDev);
                var magOut = bodyMag + newState.currentMagBias + AddNoise(magNoiseStdDev);

                if (enableRawOutput) SendImuRaw(imu.id, gyroOut, accelOut, magOut, now, newState.rawBuffer);
                if (enableAttitudeOutput)
                {
                    var e = imu.mount.eulerAngles;
                    SendImuAtt(imu.id, NormalizeDeg(e.z), NormalizeDeg(e.x), NormalizeDeg(e.y), now, newState.attBuffer);
                }

                newState.prevVelocity = worldVel;
                newState.prevTime = now;
                _imuStates[imu.id] = newState;

                if (logImuDebug && now >= _nextRadarLogTime) // Reusing log time for simplicity
                {
                    Debug.Log($"[SensorArraySimulator] IMU {imu.id}: gyro={gyroOut}, accel={accelOut}");
                }
            }
        }

        private void SendImuRaw(string id, Vector3 g, Vector3 a, Vector3 m, float t, byte[] buf)
        {
            int o = 0;
            Array.Copy(MSG_IMU_RAW, 0, buf, o, 4); o += 4;
            var idB = System.Text.Encoding.UTF8.GetBytes(id ?? "imu");
            Array.Copy(idB, 0, buf, o, Mathf.Min(idB.Length, 16));
            for (int i = idB.Length; i < 16; i++) buf[o + i] = 0;
            o += 16;
            EndianBitConverter.WriteInt64BE(buf, o, (long)(t * 1000000)); o += 8;
            EndianBitConverter.WriteFloatBE(buf, o, g.x); o += 4;
            EndianBitConverter.WriteFloatBE(buf, o, g.y); o += 4;
            EndianBitConverter.WriteFloatBE(buf, o, g.z); o += 4;
            EndianBitConverter.WriteFloatBE(buf, o, a.x); o += 4;
            EndianBitConverter.WriteFloatBE(buf, o, a.y); o += 4;
            EndianBitConverter.WriteFloatBE(buf, o, a.z); o += 4;
            EndianBitConverter.WriteFloatBE(buf, o, m.x); o += 4;
            EndianBitConverter.WriteFloatBE(buf, o, m.y); o += 4;
            EndianBitConverter.WriteFloatBE(buf, o, m.z); o += 4;
            _tx.Send(buf, 64, _telemetryEp);
        }

        private void SendImuAtt(string id, float r, float p, float y, float t, byte[] buf)
        {
            int o = 0;
            Array.Copy(MSG_IMU_ATT, 0, buf, o, 4); o += 4;
            var idB = System.Text.Encoding.UTF8.GetBytes(id ?? "imu");
            Array.Copy(idB, 0, buf, o, Mathf.Min(idB.Length, 16));
            for (int i = idB.Length; i < 16; i++) buf[o + i] = 0;
            o += 16;
            EndianBitConverter.WriteInt64BE(buf, o, (long)(t * 1000000)); o += 8;
            EndianBitConverter.WriteFloatBE(buf, o, r); o += 4;
            EndianBitConverter.WriteFloatBE(buf, o, p); o += 4;
            EndianBitConverter.WriteFloatBE(buf, o, y); o += 4;
            _tx.Send(buf, 40, _telemetryEp);
        }

        private static Vector3 AddNoise(Vector3 stdDev) => new Vector3(GaussianNoise(stdDev.x), GaussianNoise(stdDev.y), GaussianNoise(stdDev.z));
        private static float GaussianNoise(float stdDev)
        {
            float u1 = 1f - UnityEngine.Random.value, u2 = 1f - UnityEngine.Random.value;
            if (u1 <= 0) u1 = 1e-6f;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Sin(2f * Mathf.PI * u2) * stdDev;
        }
        private static float NormalizeDeg(float d) { d %= 360; return d > 180 ? d - 360 : (d < -180 ? d + 360 : d); }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            foreach (var r in radars)
            {
                if (r.mount == null) continue;
                Gizmos.DrawRay(r.mount.position, r.mount.forward * maxRangeM * 0.1f);
                var fovRad = azimuthFovDeg * 0.5f * Mathf.Deg2Rad;
                Gizmos.DrawRay(r.mount.position, r.mount.rotation * new Vector3(Mathf.Sin(-fovRad), 0, Mathf.Cos(-fovRad)) * maxRangeM);
                Gizmos.DrawRay(r.mount.position, r.mount.rotation * new Vector3(Mathf.Sin(fovRad), 0, Mathf.Cos(fovRad)) * maxRangeM);
            }
        }
    }
}

