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
        public string telemetryAddress = "127.0.0.1:20100";
        public string controlBindAddress = "0.0.0.0:20101";
        [Range(1, 120)] public int updateRateHz = 20;

        private UdpClient _tx;
        private UdpClient _rx;
        private IPEndPoint _telemetryEp;
        private CancellationTokenSource _cts;

        private const uint PGN_POS_LATLON = 0x1F801;  // 129025 simplified
        private const uint PGN_COG_SOG = 0x1F802;  // 129026 simplified
        private const uint PGN_ENG_RPM = 0x1F200;  // 127488 simplified
        private const uint PGN_RUDDER = 0x1F10D;  // 127245 simplified
        private const uint PGN_ATTITUDE = 0x1F119;  // 127257 simplified
        private const uint PGN_SET_THROT = 0x1FC00;  // custom
        private const uint PGN_SET_RUDDER = 0x1FC01;  // custom

        private void OnEnable()
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
            _ = RunTxLoop(_cts.Token);
            _ = RunRxLoop(_cts.Token);
        }

        private void OnDisable()
        {
            try { _cts?.Cancel(); } catch { }
            try { _tx?.Dispose(); } catch { }
            try { _rx?.Dispose(); } catch { }
        }

        private void SetupSockets()
        {
            var parts = telemetryAddress.Split(':');
            if (parts.Length != 2) throw new Exception("telemetryAddress must be host:port");
            _telemetryEp = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1]));
            _tx = new UdpClient();

            var bindParts = controlBindAddress.Split(':');
            if (bindParts.Length != 2) throw new Exception("controlBindAddress must be host:port");
            _rx = new UdpClient(new IPEndPoint(IPAddress.Parse(bindParts[0]), int.Parse(bindParts[1])));
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
            var f1 = BuildFrame(PGN_POS_LATLON, BitConverter.GetBytes(IPAddress.HostToNetworkOrder(lat)), BitConverter.GetBytes(IPAddress.HostToNetworkOrder(lon)));
            SendFrame(f1);

            // 129026 COG/SOG
            float speed = new Vector2(vel.x, vel.z).magnitude; // m/s
            float cogDeg = ComputeCourseOverGroundDeg(vel);
            ushort cog = (ushort)Mathf.Clamp(Mathf.RoundToInt(cogDeg * 100f), 0, 65535);
            ushort sog = (ushort)Mathf.Clamp(Mathf.RoundToInt(speed * 100f), 0, 65535);
            var f2 = BuildFrame(PGN_COG_SOG, ToBE(cog), ToBE(sog), new byte[4]);
            SendFrame(f2);

            // 127488 Engine RPM
            float rpmF = Mathf.Clamp01(vehicle != null ? vehicle.throttle : 0f) * 3000f;
            ushort rpm = (ushort)Mathf.RoundToInt(rpmF);
            byte[] eng = new byte[8];
            eng[0] = 0; // instance
            var rpmBE = ToBE(rpm);
            eng[1] = rpmBE[0]; eng[2] = rpmBE[1];
            var f3 = BuildFrame(PGN_ENG_RPM, eng);
            SendFrame(f3);

            // 127245 Rudder angle
            float rudderDeg = Mathf.Clamp((vehicle != null ? vehicle.steering : 0f), -1f, 1f) * Mathf.Max(1f, maxSteerDeg);
            short rudder = (short)Mathf.RoundToInt(rudderDeg * 100f);
            byte[] rud = new byte[8];
            rud[0] = 0; // instance
            var rudBE = ToBE(rudder);
            rud[1] = rudBE[0]; rud[2] = rudBE[1];
            var f4 = BuildFrame(PGN_RUDDER, rud);
            SendFrame(f4);

            // 127257 Attitude roll/pitch/yaw
            short r = (short)Mathf.RoundToInt(roll * 100f);
            short p = (short)Mathf.RoundToInt(pitch * 100f);
            short y = (short)Mathf.RoundToInt(yaw * 100f);
            var f5 = BuildFrame(PGN_ATTITUDE, ToBE(r), ToBE(p), ToBE(y), new byte[2]);
            SendFrame(f5);
        }

        private void HandleControlFrame(byte[] buf)
        {
            uint canId = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(new byte[] { buf[0], buf[1], buf[2], buf[3] }, 0));
            byte dlc = buf[4];
            // data starts at 5
            if (canId == PGN_SET_THROT && dlc >= 2)
            {
                // data layout: data[0]=instance, data[1]=percent
                float percent = buf[5 + 1] / 100f;
                if (vehicle != null) vehicle.throttle = Mathf.Clamp01(percent);
            }
            else if (canId == PGN_SET_RUDDER && dlc >= 3)
            {
                short angleCentideg = (short)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(new byte[] { buf[6], buf[7] }, 0)); // data[1..2]
                float angleDeg = angleCentideg / 100f;
                float norm = Mathf.Clamp(angleDeg / Mathf.Max(1f, maxSteerDeg), -1f, 1f);
                if (vehicle != null) vehicle.steering = norm;
            }
        }

        private static byte[] ToBE(ushort v)
        {
            var arr = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(arr);
            return arr;
        }

        private static byte[] ToBE(short v)
        {
            var arr = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(arr);
            return arr;
        }

        private static byte[] BuildFrame(uint canId, params byte[][] payloads)
        {
            byte[] data = new byte[8];
            int offset = 0;
            foreach (var p in payloads)
            {
                int len = Mathf.Min(p.Length, 8 - offset);
                Array.Copy(p, 0, data, offset, len);
                offset += len;
                if (offset >= 8) break;
            }

            byte[] frame = new byte[13];
            var idBE = BitConverter.GetBytes((int)canId);
            if (BitConverter.IsLittleEndian) Array.Reverse(idBE);
            frame[0] = idBE[0]; frame[1] = idBE[1]; frame[2] = idBE[2]; frame[3] = idBE[3];
            frame[4] = 8; // dlc
            Array.Copy(data, 0, frame, 5, 8);
            return frame;
        }

        private void SendFrame(byte[] frame)
        {
            try { _tx.Send(frame, frame.Length, _telemetryEp); }
            catch (Exception e) { Debug.LogError($"UDP send failed: {e.Message}"); }
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


