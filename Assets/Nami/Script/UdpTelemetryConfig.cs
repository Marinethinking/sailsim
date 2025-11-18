using System.Net;
using System.Net.Sockets;

namespace Nami
{
    /// <summary>
    /// Central UDP endpoint configuration for the simulated boat.
    /// </summary>
    public static class UdpTelemetryConfig
    {
        // Multicast group used as the simulated telemetry "bus" (NMEA2000 + sensors).
        public const string TelemetryMulticastAddress = "239.10.0.1";
        public const int TelemetryPort = 20000;
        public const int TelemetryMulticastTtl = 1;

        // Control port for throttle/steering/heartbeat commands to the simulator.
        public const int ControlPort = 20101;

        private static readonly IPAddress s_TelemetryMulticastIp = IPAddress.Parse(TelemetryMulticastAddress);
        private static readonly IPEndPoint s_TelemetryEndpoint = new IPEndPoint(s_TelemetryMulticastIp, TelemetryPort);
        private static readonly IPEndPoint s_ControlListenEndpoint = new IPEndPoint(IPAddress.Any, ControlPort);

        public static IPAddress TelemetryMulticastIp => s_TelemetryMulticastIp;

        public static IPEndPoint TelemetryEndpoint => s_TelemetryEndpoint;

        public static IPEndPoint ControlListenEndpoint => s_ControlListenEndpoint;

        public static IPEndPoint ControlEndpoint(IPAddress simulatorAddress) =>
            new IPEndPoint(simulatorAddress, ControlPort);

        public static UdpClient CreateTelemetrySender(bool allowLoopback = true)
        {
            try
            {
                var client = new UdpClient(AddressFamily.InterNetwork);
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, TelemetryMulticastTtl);
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, allowLoopback);
                UnityEngine.Debug.Log($"[UdpTelemetryConfig] Created telemetry sender for {TelemetryMulticastAddress}:{TelemetryPort}");
                return client;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[UdpTelemetryConfig] Failed to create telemetry sender: {e.Message}");
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
                UnityEngine.Debug.Log($"[UdpTelemetryConfig] Created control listener on port {ControlPort}");
                return client;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[UdpTelemetryConfig] Failed to bind control port {ControlPort}: {e.Message}. Port may be in use.");
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