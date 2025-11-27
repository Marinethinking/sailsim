using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nami
{
    [DisallowMultipleComponent]
    public class Nmea2000UdpBridge : MonoBehaviour
    {
        [Header("Boat References")]
        public Vehicle vehicle;
        public Rigidbody boatRigidbody;

        [Header("GPS Origin (Lat,Lon)")]
        public Vector2 gpsLocalOrigin = new Vector2(44.6374254f, -63.5636231f);

        [Header("Control Mapping")]
        [Tooltip("Maximum vectoring angle of the outboard (degrees), used to map PGN 0x1FC01 to steering input and to publish 127245.")]
        public float maxSteerDeg = 60f;

        [Header("UDP Settings")]
        [Range(1, 120)] public int updateRateHz = 20;
        [Tooltip("Seconds before throttle/steering fail-safe when no control heartbeats arrive. Set <=0 to disable.")]
        public float controlTimeoutSeconds = 1.5f;

        private UdpClient _tx;
        private UdpClient _rx;
        private IPEndPoint _telemetryEp;
        private CancellationTokenSource _cts;
        private readonly byte[] _frameBuffer = new byte[13];
        private readonly byte[] _payloadBuffer = new byte[8];
        private long _lastHeartbeatTicks;
        private bool _controlFailSafeActive;

        private const uint PGN_POS_LATLON = 0x1F801;  // 129025 simplified
        private const uint PGN_COG_SOG = 0x1F802;  // 129026 simplified
        private const uint PGN_ENG_RPM = 0x1F200;  // 127488 simplified
        private const uint PGN_RUDDER = 0x1F10D;  // 127245 simplified
        private const uint PGN_ATTITUDE = 0x1F119;  // 127257 simplified
        private const uint PGN_SET_THROT = 0x1FC00;  // custom
        private const uint PGN_SET_RUDDER = 0x1FC01;  // custom
        private const uint PGN_CONTROL_HEARTBEAT = 0x1FC02;  // custom heartbeat

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

                GPSEncoder.SetLocalOrigin(gpsLocalOrigin);

                _cts = new CancellationTokenSource();
                SetupSockets();
                _lastHeartbeatTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                _controlFailSafeActive = false;
                _ = RunTxLoop(_cts.Token);
                _ = RunRxLoop(_cts.Token);
                
                Debug.Log($"[Nmea2000UdpBridge] Started: telemetry={UdpPublisher.TelemetryMulticastAddress}:{UdpPublisher.TelemetryPort}, control={UdpPublisher.ControlPort}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Nmea2000UdpBridge] Failed to start: {e.Message}\n{e.StackTrace}");
                enabled = false;
            }
        }

        private void OnDisable()
        {
            try { _cts?.Cancel(); } catch { }
            try { _tx?.Dispose(); } catch { }
            try { _rx?.Dispose(); } catch { }
        }

        private void SetupSockets()
        {
            _telemetryEp = UdpPublisher.TelemetryEndpoint;
            _tx = UdpPublisher.CreateTelemetrySender();
            _rx = UdpPublisher.CreateControlListener();
        }

        private async Task RunTxLoop(CancellationToken ct)
        {
            var period = Mathf.Max(1f / Mathf.Max(1, updateRateHz), 0.005f);
            var wait = TimeSpan.FromSeconds(period);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SendTelemetry();
                }
                catch (Exception e)
                {
                    Debug.LogError($"NMEA2000 TX error: {e.Message}");
                }
                try { await Task.Delay(wait, ct); } catch { break; }
            }
        }

        private async Task RunRxLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _rx.ReceiveAsync().WithCancellation(ct);
                    if (result.Buffer.Length >= 13)
                    {
                        HandleControlFrame(result.Buffer);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { /* ignore */ }
            }
        }

        private void SendTelemetry()
        {
            if (boatRigidbody == null) return;

            var pos = boatRigidbody.position;
            var vel = boatRigidbody.linearVelocity;
            var yawPitchRoll = transform.eulerAngles;
            var yaw = NormalizeDeg(yawPitchRoll.y);
            var pitch = NormalizeDeg(yawPitchRoll.x);
            var roll = NormalizeDeg(yawPitchRoll.z);

            var gps = GPSEncoder.USCToGPS(pos);
            int lat = (int)Mathf.Round(gps.x * 1e7f);
            int lon = (int)Mathf.Round(gps.y * 1e7f);

            // 129025 lat/lon
            SendFrame(PGN_POS_LATLON, payload =>
            {
                EndianBitConverter.WriteInt32BE(payload, 0, lat);
                EndianBitConverter.WriteInt32BE(payload, 4, lon);
            });

            // 129026 COG/SOG
            float speed = new Vector2(vel.x, vel.z).magnitude; // m/s
            float cogDeg = ComputeCourseOverGroundDeg(vel);
            ushort cog = (ushort)Mathf.Clamp(Mathf.RoundToInt(cogDeg * 100f), 0, 65535);
            ushort sog = (ushort)Mathf.Clamp(Mathf.RoundToInt(speed * 100f), 0, 65535);
            SendFrame(PGN_COG_SOG, payload =>
            {
                EndianBitConverter.WriteUInt16BE(payload, 0, cog);
                EndianBitConverter.WriteUInt16BE(payload, 2, sog);
            });

            // 127488 Engine RPM
            float rpmF = Mathf.Clamp01(vehicle != null ? vehicle.throttle : 0f) * 3000f;
            ushort rpm = (ushort)Mathf.RoundToInt(rpmF);
            SendFrame(PGN_ENG_RPM, payload =>
            {
                payload[0] = 0;
                EndianBitConverter.WriteUInt16BE(payload, 1, rpm);
            });

            // 127245 Rudder angle
            float rudderDeg = Mathf.Clamp((vehicle != null ? vehicle.steering : 0f), -1f, 1f) * Mathf.Max(1f, maxSteerDeg);
            short rudder = (short)Mathf.RoundToInt(rudderDeg * 100f);
            SendFrame(PGN_RUDDER, payload =>
            {
                payload[0] = 0;
                EndianBitConverter.WriteInt16BE(payload, 1, rudder);
            });

            // 127257 Attitude roll/pitch/yaw
            short r = (short)Mathf.RoundToInt(roll * 100f);
            short p = (short)Mathf.RoundToInt(pitch * 100f);
            short y = (short)Mathf.RoundToInt(yaw * 100f);
            SendFrame(PGN_ATTITUDE, payload =>
            {
                EndianBitConverter.WriteInt16BE(payload, 0, r);
                EndianBitConverter.WriteInt16BE(payload, 2, p);
                EndianBitConverter.WriteInt16BE(payload, 4, y);
            });
        }

        private void HandleControlFrame(byte[] buf)
        {
            uint canId = EndianBitConverter.ReadUInt32BE(buf, 0);
            byte dlc = buf[4];
            // data starts at 5
            if (canId == PGN_SET_THROT && dlc >= 2)
            {
                // data layout: data[0]=instance, data[1]=percent
                float percent = buf[6] / 100f;
                if (vehicle != null) vehicle.throttle = Mathf.Clamp01(percent);
                MarkHeartbeat();
            }
            else if (canId == PGN_SET_RUDDER && dlc >= 3)
            {
                short angleCentideg = EndianBitConverter.ReadInt16BE(buf, 6);
                float angleDeg = angleCentideg / 100f;
                float norm = Mathf.Clamp(angleDeg / Mathf.Max(1f, maxSteerDeg), -1f, 1f);
                if (vehicle != null) vehicle.steering = norm;
                MarkHeartbeat();
            }
            else if (canId == PGN_CONTROL_HEARTBEAT)
            {
                MarkHeartbeat();
            }
        }
        private void SendFrame(uint canId, Action<byte[]> payloadWriter)
        {
            Array.Clear(_payloadBuffer, 0, _payloadBuffer.Length);
            payloadWriter?.Invoke(_payloadBuffer);

            EndianBitConverter.WriteUInt32BE(_frameBuffer, 0, canId);
            _frameBuffer[4] = 8;
            Buffer.BlockCopy(_payloadBuffer, 0, _frameBuffer, 5, 8);

            try { _tx.Send(_frameBuffer, _frameBuffer.Length, _telemetryEp); }
            catch (Exception e) { Debug.LogError($"UDP send failed: {e.Message}"); }
        }

        private void Update()
        {
            if (controlTimeoutSeconds <= 0f || vehicle == null) return;

            var lastTicks = Interlocked.Read(ref _lastHeartbeatTicks);
            var elapsed = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - lastTicks) / System.Diagnostics.Stopwatch.Frequency;
            var inactive = elapsed > controlTimeoutSeconds;
            
            if (inactive)
            {
                if (!_controlFailSafeActive)
                {
                    vehicle.throttle = 0f;
                    vehicle.steering = 0f;
                    _controlFailSafeActive = true;
                    UnityEngine.Debug.LogWarning($"Control fail-safe activated: no heartbeat for {elapsed:F2}s");
                }
            }
            else
            {
                _controlFailSafeActive = false;
            }
        }

        private void MarkHeartbeat()
        {
            Interlocked.Exchange(ref _lastHeartbeatTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        private static float NormalizeDeg(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg < -180f) deg += 360f;
            return deg;
        }

        private static float ComputeCourseOverGroundDeg(Vector3 velocity)
        {
            Vector2 v = new Vector2(velocity.x, velocity.z);
            if (v.sqrMagnitude < 1e-6f) return 0f;
            float angle = Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg; // 0 along +Z, right-handed
            if (angle < 0f) angle += 360f;
            return angle;
        }
    }

    internal static class TaskExt
    {
        public static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken ct)
        {
            using (ct.Register(() => { try { task.Wait(0); } catch { } }))
            {
                return await task.ConfigureAwait(false);
            }
        }
    }
}


