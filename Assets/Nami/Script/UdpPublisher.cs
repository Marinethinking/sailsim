using System.Net;
using System.Net.Sockets;

namespace Nami
{
    /// <summary>
    /// Central UDP endpoint configuration and factory for the simulated boat.
    /// Handles Telemetry (NMEA2000/Sensors) and Camera Metadata.
    /// </summary>
    public static class UdpPublisher
    {
        // --- Telemetry Configuration ---
        // Multicast group used as the simulated telemetry "bus" (NMEA2000 + sensors).
        public const string TelemetryMulticastAddress = "239.10.0.1";
        public const int TelemetryPort = 20000;
        public const int TelemetryMulticastTtl = 1;

        // Control port for throttle/steering/heartbeat commands to the simulator.
        public const int ControlPort = 20101;

        // --- Camera Metadata Configuration ---
        public const string CameraMetaMulticastAddress = "224.0.0.1";
        public const int CameraMetaPort = 6000;
        public const int CameraMetaMulticastTtl = 1;


        private static readonly IPAddress s_TelemetryMulticastIp = IPAddress.Parse(TelemetryMulticastAddress);
        private static readonly IPEndPoint s_TelemetryEndpoint = new IPEndPoint(s_TelemetryMulticastIp, TelemetryPort);
        private static readonly IPEndPoint s_ControlListenEndpoint = new IPEndPoint(IPAddress.Any, ControlPort);
        
        private static readonly IPAddress s_CameraMetaMulticastIp = IPAddress.Parse(CameraMetaMulticastAddress);
        private static readonly IPEndPoint s_CameraMetaEndpoint = new IPEndPoint(s_CameraMetaMulticastIp, CameraMetaPort);

        public static IPAddress TelemetryMulticastIp => s_TelemetryMulticastIp;
        public static IPEndPoint TelemetryEndpoint => s_TelemetryEndpoint;
        public static IPEndPoint ControlListenEndpoint => s_ControlListenEndpoint;
        
        public static IPAddress CameraMetaMulticastIp => s_CameraMetaMulticastIp;
        public static IPEndPoint CameraMetaEndpoint => s_CameraMetaEndpoint;

        public static IPEndPoint ControlEndpoint(IPAddress simulatorAddress) =>
            new IPEndPoint(simulatorAddress, ControlPort);

        public static UdpClient CreateTelemetrySender(bool allowLoopback = true)
        {
            return CreateMulticastSender(TelemetryMulticastAddress, TelemetryPort, TelemetryMulticastTtl, allowLoopback, "Telemetry");
        }
        
        public static UdpClient CreateCameraMetaSender(bool allowLoopback = true)
        {
            return CreateMulticastSender(CameraMetaMulticastAddress, CameraMetaPort, CameraMetaMulticastTtl, allowLoopback, "CameraMeta");
        }

        private static UdpClient CreateMulticastSender(string ip, int port, int ttl, bool loopback, string name)
        {
            try
            {
                var client = new UdpClient(AddressFamily.InterNetwork);
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl);
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, loopback);
                UnityEngine.Debug.Log($"[UdpPublisher] Created {name} sender for {ip}:{port}");
                return client;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[UdpPublisher] Failed to create {name} sender: {e.Message}");
                throw;
            }
        }

        public static UdpClient CreateControlListener()
        {
            try
            {
                var client = new UdpClient();
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.Client.Bind(ControlListenEndpoint);
                UnityEngine.Debug.Log($"[UdpPublisher] Created control listener on port {ControlPort}");
                return client;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[UdpPublisher] Failed to bind control port {ControlPort}: {e.Message}. Port may be in use.");
                throw;
            }
        }

        /// <summary>
        /// Create a UDP client to receive telemetry multicast messages.
        /// Use this if the simulator needs to listen to its own telemetry or other boats.
        /// </summary>
        public static UdpClient CreateTelemetryReceiver()
        {
            var client = new UdpClient();
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, TelemetryPort));
            client.JoinMulticastGroup(TelemetryMulticastIp);
            return client;
        }
    }
}
