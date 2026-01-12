using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nami
{
    [DisallowMultipleComponent]
    public class ImuSimulator : MonoBehaviour
    {
        [Header("Boat References")]
        public Vehicle vehicle;
        public Rigidbody boatRigidbody;
        [Tooltip("Optional: IMU mounting transform. If null, uses this transform.")]
        public Transform imuMount;

        [Header("IMU Settings")]
        [Range(50, 400)] public int updateRateHz = 200;
        [Tooltip("Enable raw sensor output (gyro/accel/mag)")]
        public bool enableRawOutput = true;
        [Tooltip("Enable fused attitude output (roll/pitch/yaw)")]
        public bool enableAttitudeOutput = true;

        [Header("Noise Parameters")]
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

        [Header("Magnetic Field")]
        [Tooltip("Earth magnetic field vector in world frame (µT). Default: pointing down (typical for northern hemisphere).")]
        public Vector3 earthMagneticField = new Vector3(0, 0, -50000f);

        [Header("Debug")]
        [Tooltip("Print periodic IMU debug lines to the Unity Console.")]
        public bool logImuDebug = false;
        [Tooltip("Seconds between IMU debug logs when enabled.")]
        public float debugLogIntervalSec = 20.0f;

        private UdpClient _tx;
        private IPEndPoint _telemetryEp;
        private CancellationTokenSource _cts;

        // Message type headers (4 bytes each)
        private static readonly byte[] MSG_IMU_RAW = { (byte)'I', (byte)'M', (byte)'U', (byte)'R' };
        private static readonly byte[] MSG_IMU_ATT = { (byte)'I', (byte)'M', (byte)'U', (byte)'A' };

        // Previous frame data for acceleration calculation
        private Vector3 _prevVelocity = Vector3.zero;
        private float _prevTime = 0f;

        // Current bias values (for random walk)
        private Vector3 _currentGyroBias;
        private Vector3 _currentAccelBias;
        private Vector3 _currentMagBias;
        private float _nextDebugLogTime;
        private byte[] _rawBuffer;
        private byte[] _attBuffer;

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
                if (imuMount == null)
                {
                    imuMount = transform;
                }

                _currentGyroBias = gyroBias;
                _currentAccelBias = accelBias;
                _currentMagBias = magBias;
                if (_rawBuffer == null) _rawBuffer = new byte[48];
                if (_attBuffer == null) _attBuffer = new byte[24];
                _prevTime = Time.time;
                _nextDebugLogTime = _prevTime + Mathf.Max(0.1f, debugLogIntervalSec);
                if (boatRigidbody != null)
                {
                    _prevVelocity = boatRigidbody.linearVelocity;
                }

                _cts = new CancellationTokenSource();
                SetupSockets();
                _ = RunTxLoop(_cts.Token);
                
                Debug.Log($"[ImuSimulator] Started: telemetry={UdpPublisher.TelemetryMulticastAddress}:{UdpPublisher.TelemetryPort}, rate={updateRateHz}Hz");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ImuSimulator] Failed to start: {e.Message}\n{e.StackTrace}");
                enabled = false;
            }
        }

        private void OnDisable()
        {
            try { _cts?.Cancel(); } catch { }
            try { _tx?.Dispose(); } catch { }
        }

        private void SetupSockets()
        {
            _telemetryEp = UdpPublisher.TelemetryEndpoint;
            _tx = UdpPublisher.CreateTelemetrySender();
        }

        private async Task RunTxLoop(CancellationToken ct)
        {
            var period = Mathf.Max(1f / Mathf.Max(1, updateRateHz), 0.0025f);
            var wait = TimeSpan.FromSeconds(period);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SendImuData();
                }
                catch (Exception e)
                {
                    Debug.LogError($"IMU TX error: {e.Message}");
                }
                try { await Task.Delay(wait, ct); } catch { break; }
            }
        }

        private void SendImuData()
        {
            if (boatRigidbody == null) return;

            var currentTime = Time.time;
            var deltaTime = currentTime - _prevTime;
            if (deltaTime <= 0f) deltaTime = 0.001f; // Prevent division by zero

            // Get world frame data
            var worldAngularVel = boatRigidbody.angularVelocity;
            var worldVelocity = boatRigidbody.linearVelocity;
            var worldAccel = (worldVelocity - _prevVelocity) / deltaTime;
            var worldGravity = Physics.gravity;

            // Transform to IMU body frame (world → body requires inverse rotation)
            var bodyAngularVel = imuMount.InverseTransformDirection(worldAngularVel);
            var bodyAccel = imuMount.InverseTransformDirection(worldAccel - worldGravity); // Specific force (accel - gravity)
            var bodyMag = imuMount.InverseTransformDirection(earthMagneticField);

            // Update bias with random walk
            _currentGyroBias += new Vector3(
                UnityEngine.Random.Range(-biasDriftRate, biasDriftRate),
                UnityEngine.Random.Range(-biasDriftRate, biasDriftRate),
                UnityEngine.Random.Range(-biasDriftRate, biasDriftRate)
            ) * deltaTime;
            _currentAccelBias += new Vector3(
                UnityEngine.Random.Range(-biasDriftRate, biasDriftRate),
                UnityEngine.Random.Range(-biasDriftRate, biasDriftRate),
                UnityEngine.Random.Range(-biasDriftRate, biasDriftRate)
            ) * deltaTime;
            _currentMagBias += new Vector3(
                UnityEngine.Random.Range(-biasDriftRate, biasDriftRate),
                UnityEngine.Random.Range(-biasDriftRate, biasDriftRate),
                UnityEngine.Random.Range(-biasDriftRate, biasDriftRate)
            ) * deltaTime;

            // Add noise and bias
            var gyro = bodyAngularVel + _currentGyroBias + AddNoise(gyroNoiseStdDev);
            var accel = bodyAccel + _currentAccelBias + AddNoise(accelNoiseStdDev);
            var mag = bodyMag + _currentMagBias + AddNoise(magNoiseStdDev);

            // Send raw sensor data
            if (enableRawOutput)
            {
                SendRawMessage(gyro, accel, mag, currentTime);
            }

            // Send attitude data
            if (enableAttitudeOutput)
            {
                var euler = imuMount.eulerAngles;
                var roll = NormalizeDeg(euler.z);
                var pitch = NormalizeDeg(euler.x);
                var yaw = NormalizeDeg(euler.y);
                SendAttitudeMessage(roll, pitch, yaw, currentTime);
            }

            // Throttled debug logging
            if (logImuDebug && currentTime >= _nextDebugLogTime)
            {
                _nextDebugLogTime = currentTime + Mathf.Max(0.1f, debugLogIntervalSec);
                
                Debug.Log(
                    $"[ImuSimulator] '{imuMount.name}' Sensor Data:\n" +
                    $"gyro=({gyro.x:F3},{gyro.y:F3},{gyro.z:F3}) rad/s " +
                    $"accel=({accel.x:F2},{accel.y:F2},{accel.z:F2}) m/s^2 " +
                    $"mag=({mag.x:F1},{mag.y:F1},{mag.z:F1}) µT");
            }

            // Update previous values
            _prevVelocity = worldVelocity;
            _prevTime = currentTime;
        }

        private void SendRawMessage(Vector3 gyro, Vector3 accel, Vector3 mag, float time)
        {
            // Message format: [4-byte header][8-byte timestamp][36-byte payload (9 floats)]
            // Total: 48 bytes
            var timestamp = (long)(time * 1000000); // Unix microseconds
            var buffer = _rawBuffer;
            int offset = 0;

            // Header
            Array.Copy(MSG_IMU_RAW, 0, buffer, offset, 4);
            offset += 4;

            // Timestamp (8 bytes, big-endian)
            EndianBitConverter.WriteInt64BE(buffer, offset, timestamp);
            offset += 8;

            // Payload: gyro[3], accel[3], mag[3] (36 bytes)
            EndianBitConverter.WriteFloatBE(buffer, offset, gyro.x); offset += 4;
            EndianBitConverter.WriteFloatBE(buffer, offset, gyro.y); offset += 4;
            EndianBitConverter.WriteFloatBE(buffer, offset, gyro.z); offset += 4;
            EndianBitConverter.WriteFloatBE(buffer, offset, accel.x); offset += 4;
            EndianBitConverter.WriteFloatBE(buffer, offset, accel.y); offset += 4;
            EndianBitConverter.WriteFloatBE(buffer, offset, accel.z); offset += 4;
            EndianBitConverter.WriteFloatBE(buffer, offset, mag.x); offset += 4;
            EndianBitConverter.WriteFloatBE(buffer, offset, mag.y); offset += 4;
            EndianBitConverter.WriteFloatBE(buffer, offset, mag.z); offset += 4;

            SendFrame(buffer);
        }

        private void SendAttitudeMessage(float roll, float pitch, float yaw, float time)
        {
            // Message format: [4-byte header][8-byte timestamp][12-byte payload (3 floats)]
            // Total: 24 bytes
            var timestamp = (long)(time * 1000000); // Unix microseconds
            var buffer = _attBuffer;
            int offset = 0;

            // Header
            Array.Copy(MSG_IMU_ATT, 0, buffer, offset, 4);
            offset += 4;

            // Timestamp (8 bytes, big-endian)
            EndianBitConverter.WriteInt64BE(buffer, offset, timestamp);
            offset += 8;

            // Payload: roll, pitch, yaw (12 bytes)
            EndianBitConverter.WriteFloatBE(buffer, offset, roll); offset += 4;
            EndianBitConverter.WriteFloatBE(buffer, offset, pitch); offset += 4;
            EndianBitConverter.WriteFloatBE(buffer, offset, yaw); offset += 4;

            SendFrame(buffer);
        }

        private void SendFrame(byte[] frame)
        {
            try { _tx.Send(frame, frame.Length, _telemetryEp); }
            catch (Exception e) { Debug.LogError($"IMU UDP send failed: {e.Message}"); }
        }

        private static Vector3 AddNoise(Vector3 stdDev)
        {
            return new Vector3(
                GaussianNoise(stdDev.x),
                GaussianNoise(stdDev.y),
                GaussianNoise(stdDev.z)
            );
        }

        private static float GaussianNoise(float stdDev)
        {
            // Box-Muller transform for Gaussian noise
            float u1 = 1.0f - UnityEngine.Random.value; // [0,1) -> (0,1]
            float u2 = 1.0f - UnityEngine.Random.value;
            float z = Mathf.Sqrt(-2.0f * Mathf.Log(u1)) * Mathf.Sin(2.0f * Mathf.PI * u2);
            return z * stdDev;
        }

        private static float NormalizeDeg(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg < -180f) deg += 360f;
            return deg;
        }
    }
}

